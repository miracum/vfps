using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Vfps.Data;

/// <summary>
/// The `migrate` subcommand: applies every pending EF Core migration, installs Hangfire's own
/// schema, and exits.
/// </summary>
/// <remarks>
/// This replaces the two `dotnet ef migrations bundle` executables (`efbundle`,
/// `efbundle-dataprotection`) the container image used to ship alongside the app. Each bundle was
/// a self-contained single-file apphost - it embedded an entire copy of the .NET runtime plus the
/// migrations - so the pair added ~117MB to an image that already contains the runtime and the
/// exact same migration classes in Vfps.dll. Running them from the app assembly instead costs
/// nothing extra and keeps a single code path for "apply the migrations".
///
/// Deliberately not a <c>WebApplication</c>: a migration run has no business binding Kestrel,
/// reaching an OIDC discovery endpoint or an S3 bucket, or starting Hangfire's job server -
/// installing Hangfire's schema needs none of that, see
/// <see cref="InstallHangfireSchema"/>. It builds the DbContexts straight off
/// <see cref="IConfiguration"/>.
/// </remarks>
internal static class DatabaseMigrator
{
    /// <summary>
    /// Keeps the `--connection=&lt;connection string&gt;` flag that `efbundle` accepted working,
    /// by mapping it onto the configuration key the contexts actually read. Everything else -
    /// `ConnectionStrings__PostgreSQL` and friends - comes from the environment as usual.
    /// </summary>
    private static readonly Dictionary<string, string> SwitchMappings = new()
    {
        ["--connection"] = "ConnectionStrings:PostgreSQL",
    };

    /// <summary>
    /// The Postgres schema Hangfire's own tables live in. Shared with Program.cs rather than left
    /// to <see cref="PostgreSqlStorageOptions" />'s default on both sides, so that the schema this
    /// job installs and the one the app reads can't drift apart.
    /// </summary>
    public const string HangfireSchemaName = "hangfire";

    public static async Task<int> RunAsync(string[] args)
    {
        // `efbundle --verbose` was a bare flag; the configuration command-line provider only
        // accepts `--key=value` and throws on anything else, so it is pulled out here rather than
        // handed to it. It keeps the same meaning it had: show the SQL being executed, which
        // appsettings.json otherwise caps at Warning for the EF Core categories.
        var verbose = args.Any(arg => arg is "--verbose" or "-v");
        var configArgs = args.Where(arg => arg is not ("--verbose" or "-v")).ToArray();

        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                Args = [],
                ContentRootPath = AppContext.BaseDirectory,
            }
        );

        builder.Configuration.AddCommandLine(configArgs, SwitchMappings);

        if (verbose)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Information",
                }
            );
        }

        builder.Services.AddDbContext<PseudonymContext>(
            (isp, options) =>
                ConfigurePseudonymContext(isp.GetRequiredService<IConfiguration>(), options)
        );

        // Both the Data Protection key ring and Hangfire live in ConnectionStrings:PostgreSQL,
        // so one check gates both, mirroring Program.cs: neither exists where there is no Postgres
        // to put it in.
        var postgresConnectionString = builder.Configuration.GetConnectionString("PostgreSQL");
        var hasPostgres = !string.IsNullOrEmpty(postgresConnectionString);
        if (hasPostgres)
        {
            builder.Services.AddDbContext<DataProtectionKeyContext>(options =>
                options.UseNpgsql(postgresConnectionString)
            );
        }

        using var host = builder.Build();
        var logger = host
            .Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Vfps.Migrate");

        try
        {
            using var scope = host.Services.CreateScope();

            await MigrateAsync<PseudonymContext>(scope.ServiceProvider, logger);

            if (hasPostgres)
            {
                await MigrateAsync<DataProtectionKeyContext>(scope.ServiceProvider, logger);
                InstallHangfireSchema(postgresConnectionString!, logger);
            }
            else
            {
                // Not fatal - a deployment can legitimately run without one - but silently
                // skipping it is how you end up with the app failing at startup on
                // `relation "data_protection_keys" does not exist`.
                logger.LogWarning(
                    "No PostgreSQL connection string configured, skipping {Context} migrations "
                        + "and the Hangfire schema",
                    nameof(DataProtectionKeyContext)
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Applying migrations failed");
            return 1;
        }

        logger.LogInformation("Done.");
        return 0;
    }

    /// <summary>
    /// Creates or upgrades Hangfire's own schema, which is not an EF Core migration and so is not
    /// covered by <see cref="MigrateAsync{TContext}" />.
    /// </summary>
    /// <remarks>
    /// Hangfire.PostgreSql does this itself from <c>PostgreSqlStorage</c>'s constructor on every
    /// instance that starts, because <c>PrepareSchemaIfNecessary</c> defaults to true. Its
    /// Install.sql takes no advisory lock and no table lock - each version step is guarded only by
    /// a "has this already been applied?" read of the "schema" table from inside the same
    /// transaction - so replicas starting together all see the same old version and all run the
    /// same DDL concurrently. That DDL is largely `ALTER TABLE ... ALTER COLUMN ... TYPE`, which
    /// rebuilds dependent constraints, and two of those racing produce an internal Postgres error:
    /// `XX000: could not find tuple for constraint NNNNN`. The Helm chart makes it *more* likely,
    /// not less - every pod waits on this job through a `wait-for-migrations-job` init container,
    /// so they are all released to run the installer at the same instant.
    ///
    /// Running it here gives the Hangfire schema the same single owner the EF Core migrations
    /// already have: one job pod, no concurrency left to serialize. Program.cs turns
    /// <c>PrepareSchemaIfNecessary</c> off wherever this job is what applies migrations, and
    /// leaves it on where the app itself does (development, `ForceRunDatabaseMigrations`) - both
    /// of which are single-instance, so the race can't arise there either.
    ///
    /// Note this only runs any DDL at all on a fresh database or after a Hangfire.PostgreSql
    /// upgrade; on an up-to-date schema every step short-circuits on its own version check.
    /// </remarks>
    private static void InstallHangfireSchema(string connectionString, ILogger logger)
    {
        logger.LogInformation(
            "Installing/upgrading the Hangfire schema in {Schema}",
            HangfireSchemaName
        );

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        PostgreSqlObjectsInstaller.Install(connection, HangfireSchemaName);
    }

    private static async Task MigrateAsync<TContext>(IServiceProvider services, ILogger logger)
        where TContext : DbContext
    {
        var context = services.GetRequiredService<TContext>();

        var pending = (await context.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("{Context}: no pending migrations", typeof(TContext).Name);
            return;
        }

        logger.LogInformation(
            "{Context}: applying {Count} pending migration(s): {Migrations}",
            typeof(TContext).Name,
            pending.Count,
            string.Join(", ", pending)
        );

        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// Resolves the backing store and its connection string the same way the app does, and points
    /// <paramref name="options"/> at the matching provider.
    /// </summary>
    /// <remarks>
    /// Shared with Program.cs so the two can't drift on which connection string a context ends up
    /// using. The app additionally layers <c>EnableRetryOnFailure</c> on top of this; a migration
    /// run deliberately does not. Retrying for the app's 15-attempt/5-minute budget would turn an
    /// unreachable or misconfigured database into a job that hangs for the better part of an hour
    /// instead of failing fast - and the Helm chart already puts a `wait-for-db` init container in
    /// front of the migration container, with `backoffLimit` to retry the job as a whole.
    /// </remarks>
    public static void ConfigurePseudonymContext(
        IConfiguration config,
        DbContextOptionsBuilder options,
        Action<Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder>? configureNpgsql =
            null
    )
    {
        var backingStore =
            config.GetValue<string>("Pseudonymization:BackingStore")
            ?? throw new InvalidOperationException(
                "Failed to get backing store config. Make sure Pseudonymization:BackingStore is set"
            );

        var connString =
            config.GetConnectionString(backingStore)
            ?? throw new InvalidOperationException(
                $"Failed to get connection string for '{backingStore}' backing store"
            );

        switch (backingStore.ToLowerInvariant())
        {
            case "postgresql":
                options.UseNpgsql(connString, configureNpgsql);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported backing store specified: {backingStore}"
                );
        }
    }
}
