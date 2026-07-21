using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;
using Vfps.Data;

namespace Vfps.Tests.DataTests;

public class CachingNamespaceRepositoryTests : ServiceTests.ServiceTestBase
{
    private CachingNamespaceRepository CreateSut() =>
        new(
            InMemoryPseudonymContext,
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 2048 }),
            new CacheConfig()
        );

    [Fact]
    public async Task FindAsync_WhenNamespaceIsCreatedAfterAnInitialMiss_ShouldFindItOnTheNextCall()
    {
        // Regression test: InitNamespacesBackgroundService calls FindAsync to check for an
        // existing namespace immediately before calling CreateAsync for a new one. A cached
        // miss must not persist past that CreateAsync, or the namespace becomes invisible to
        // every single-namespace lookup (Create/List/Browse) for the rest of the cache lifetime
        // even though GetAllAsync (uncached) already shows it in the namespace list.
        var sut = CreateSut();

        var beforeCreate = await sut.FindAsync("newNamespace", CancellationToken.None);
        beforeCreate.Should().BeNull();

        await sut.CreateAsync(
            new Data.Models.Namespace
            {
                Name = "newNamespace",
                Description = "created after the initial miss",
                PseudonymLength = 16,
                PseudonymGenerationMethod = Protos
                    .PseudonymGenerationMethod
                    .SecureRandomBase64UrlEncoded,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
            },
            CancellationToken.None
        );

        var afterCreate = await sut.FindAsync("newNamespace", CancellationToken.None);

        afterCreate.Should().NotBeNull();
        afterCreate!.Name.Should().Be("newNamespace");
    }

    [Fact]
    public async Task FindAsync_WithExistingNamespace_ShouldCacheTheHit()
    {
        var sut = CreateSut();

        var first = await sut.FindAsync("existingNamespace", CancellationToken.None);
        first.Should().NotBeNull();

        // Delete it directly from the underlying context, bypassing the repository, so a
        // second FindAsync call can only succeed by having actually served the cached hit
        // rather than re-querying (which would now find nothing).
        InMemoryPseudonymContext.Namespaces.Remove(
            InMemoryPseudonymContext.Namespaces.Single(n => n.Name == "existingNamespace")
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var second = await sut.FindAsync("existingNamespace", CancellationToken.None);

        second.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteAsync_WithPreviouslyCachedHit_ShouldEvictItSoFindAsyncSeesTheDeletion()
    {
        var sut = CreateSut();

        var beforeDelete = await sut.FindAsync("existingNamespace", CancellationToken.None);
        beforeDelete.Should().NotBeNull();

        await sut.DeleteAsync("existingNamespace", CancellationToken.None);

        var afterDelete = await sut.FindAsync("existingNamespace", CancellationToken.None);
        afterDelete.Should().BeNull();
    }
}
