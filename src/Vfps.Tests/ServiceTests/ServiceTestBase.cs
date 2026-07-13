using EntityFramework.Exceptions.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.PseudonymGenerators;

namespace Vfps.Tests.ServiceTests;

public class ServiceTestBase : IDisposable
{
    private bool disposedValue;
    private readonly SqliteConnection _connection;

    public ServiceTestBase()
    {
        _connection = new SqliteConnection("Filename=:memory:;Cache=Private");
        _connection.Open();

        InMemoryPseudonymContext = new PseudonymContext(BuildContextOptions());

        var existingNamespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            Description = "existing namespace description",
            PseudonymLength = 32,
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
            PseudonymGenerationMethod = PseudonymGenerationMethod.Unspecified,
            PseudonymPrefix = "",
            PseudonymSuffix = "",
        };

        var emptyNamespace = new Data.Models.Namespace
        {
            Name = "emptyNamespace",
            Description = "existing empty namespace",
            PseudonymLength = 32,
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
            PseudonymGenerationMethod = PseudonymGenerationMethod.SecureRandomBase64UrlEncoded,
            PseudonymPrefix = "pre-",
            PseudonymSuffix = "-suf",
        };

        var existingPseudonym = new Data.Models.Pseudonym
        {
            NamespaceName = "existingNamespace",
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
            OriginalValue = "an original value",
            PseudonymValue = "existingPseudonym",
        };

        InMemoryPseudonymContext.Database.EnsureCreated();
        InMemoryPseudonymContext.Namespaces.AddRange(existingNamespace, emptyNamespace);
        InMemoryPseudonymContext.Pseudonyms.AddRange(existingPseudonym);
        InMemoryPseudonymContext.SaveChanges();
        InMemoryPseudonymContext.ChangeTracker.Clear();
    }

    protected PseudonymContext InMemoryPseudonymContext { get; }

    private DbContextOptions<PseudonymContext> BuildContextOptions() =>
        new DbContextOptionsBuilder<PseudonymContext>()
            .UseSqlite(_connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .UseExceptionProcessor()
            .Options;

    /// <summary>
    /// A permission checker with authorization disabled (the default) - every check passes,
    /// matching this codebase's off-by-default idiom. Pass a populated <see cref="AuthorizationConfig"/>
    /// to test actual enforcement.
    /// </summary>
    protected static INamespacePermissionChecker CreatePermissionChecker(
        AuthorizationConfig? config = null
    ) => new NamespacePermissionChecker(Options.Create(config ?? new AuthorizationConfig()));

    protected PseudonymAppService CreatePseudonymAppService(
        INamespaceRepository namespaceRepository,
        IPseudonymRepository pseudonymRepository,
        AuthorizationConfig? config = null
    ) =>
        new(
            namespaceRepository,
            pseudonymRepository,
            CreatePermissionChecker(config),
            new PseudonymizationMethodsLookup(),
            new TestPseudonymContextFactory(BuildContextOptions)
        );

    /// <summary>
    /// Every "new" DbContext this factory produces shares the same open SQLite connection as
    /// <see cref="InMemoryPseudonymContext"/> (a private, connection-scoped in-memory database
    /// otherwise wouldn't be visible across separate connections/contexts), so it sees the same
    /// test data. Mirrors <see cref="IDbContextFactory{PseudonymContext}"/>, which
    /// PseudonymAppService's trusted methods use in production to get a fresh context per
    /// concurrent call instead of reusing one shared, non-thread-safe instance.
    /// </summary>
    private sealed class TestPseudonymContextFactory(
        Func<DbContextOptions<PseudonymContext>> optionsFactory
    ) : IDbContextFactory<PseudonymContext>
    {
        public PseudonymContext CreateDbContext() => new(optionsFactory());

        public Task<PseudonymContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CreateDbContext());
    }

    protected static NamespaceAppService CreateNamespaceAppService(
        INamespaceRepository namespaceRepository,
        AuthorizationConfig? config = null
    ) =>
        new(
            namespaceRepository,
            CreatePermissionChecker(config),
            new PseudonymizationMethodsLookup()
        );

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                InMemoryPseudonymContext.Dispose();
            }

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
