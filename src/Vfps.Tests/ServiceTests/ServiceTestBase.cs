using EntityFramework.Exceptions.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Vfps.Tests.ServiceTests;

public class ServiceTestBase : IDisposable
{
    private bool disposedValue;

    public ServiceTestBase()
    {
        var _connection = new SqliteConnection("Filename=:memory:;Cache=Private");
        _connection.Open();

        var _contextOptions = new DbContextOptionsBuilder<PseudonymContext>()
            .UseSqlite(_connection)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .UseExceptionProcessor()
            .Options;

        InMemoryPseudonymContext = new PseudonymContext(_contextOptions);

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
