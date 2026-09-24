using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;
using Vfps.Data;

namespace Vfps.Tests.DataTests;

public class CachingPseudonymRepositoryTests : ServiceTests.ServiceTestBase
{
    private CachingPseudonymRepository CreateSut() =>
        new(
            ContextFactory,
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 2048 }),
            new CacheConfig()
        );

    private async Task<Data.Models.Namespace> ExistingNamespaceAsync() =>
        (
            await new NamespaceRepository(ContextFactory).FindAsync(
                "existingNamespace",
                CancellationToken.None
            )
        )!;

    // Deletes the row behind the repository's back, so a later lookup can only still see it by
    // having been served from the cache.
    private async Task DeleteStoredPseudonymAsync(string originalValue)
    {
        InMemoryPseudonymContext.Pseudonyms.Remove(
            InMemoryPseudonymContext.Pseudonyms.Single(p => p.OriginalValue == originalValue)
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();
    }

    [Fact]
    public async Task FindFirstByOriginalValueAsync_WithAStoredValue_ShouldServeRepeatsFromTheCache()
    {
        var sut = CreateSut();
        var @namespace = await ExistingNamespaceAsync();

        var first = await sut.FindFirstByOriginalValueAsync(
            @namespace,
            "an original value",
            CancellationToken.None
        );
        await DeleteStoredPseudonymAsync("an original value");
        var second = await sut.FindFirstByOriginalValueAsync(
            @namespace,
            "an original value",
            CancellationToken.None
        );

        first!.PseudonymValue.Should().Be("existingPseudonym");
        second!.PseudonymValue.Should().Be("existingPseudonym");
    }

    [Fact]
    public async Task FindFirstByOriginalValueAsync_AfterAMiss_ShouldFindTheValueOnceItIsStored()
    {
        var sut = CreateSut();
        var @namespace = await ExistingNamespaceAsync();

        var beforeCreate = await sut.FindFirstByOriginalValueAsync(
            @namespace,
            "created later",
            CancellationToken.None
        );
        await sut.CreateIfNotExistBatchAsync(
            [
                new Data.Models.Pseudonym
                {
                    NamespaceName = @namespace.Name,
                    OriginalValue = "created later",
                    PseudonymValue = "created-later-pseudonym",
                },
            ],
            CancellationToken.None
        );
        var afterCreate = await sut.FindFirstByOriginalValueAsync(
            @namespace,
            "created later",
            CancellationToken.None
        );

        beforeCreate.Should().BeNull();
        afterCreate!.PseudonymValue.Should().Be("created-later-pseudonym");
    }

    [Fact]
    public async Task FindFirstByOriginalValueAsync_ForANamespaceRecreatedUnderTheSameName_ShouldNotServeTheOldEntry()
    {
        var sut = CreateSut();
        var original = await ExistingNamespaceAsync();
        await sut.FindFirstByOriginalValueAsync(
            original,
            "an original value",
            CancellationToken.None
        );
        await DeleteStoredPseudonymAsync("an original value");
        var recreated = new Data.Models.Namespace
        {
            Name = original.Name,
            PseudonymLength = original.PseudonymLength,
            CreatedAt = original.CreatedAt.AddMinutes(1),
        };

        var found = await sut.FindFirstByOriginalValueAsync(
            recreated,
            "an original value",
            CancellationToken.None
        );

        found.Should().BeNull();
    }
}
