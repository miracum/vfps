using Microsoft.EntityFrameworkCore;

namespace Vfps.Data;

/// <summary>
/// The `migrate` subcommand: applies every pending EF Core migration and exits.
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
/// reaching an OIDC discovery endpoint or an S3 bucket, or starting Hangfire's job server. It
/// builds the DbContexts straight off <see cref="IConfiguration"/>.
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

        // Only registered when a PostgreSQL connection string is set, mirroring Program.cs: the
        // Data Protection key ring lives in Postgres only when there is a Postgres to put it in.
        var dataProtectionConnectionString = builder.Configuration.GetConnectionString(
            "PostgreSQL"
        );
        var hasDataProtectionContext = !string.IsNullOrEmpty(dataProtectionConnectionString);
        if (hasDataProtectionContext)
        {
            builder.Services.AddDbContext<DataProtectionKeyContext>(options =>
                options.UseNpgsql(dataProtectionConnectionString)
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

            if (hasDataProtectionContext)
            {
                await MigrateAsync<DataProtectionKeyContext>(scope.ServiceProvider, logger);
            }
            else
            {
                // Not fatal - a deployment can legitimately run without one - but silently
                // skipping it is how you end up with the app failing at startup on
                // `relation "data_protection_keys" does not exist`.
                logger.LogWarning(
                    "No PostgreSQL connection string configured, skipping {Context} migrations",
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
