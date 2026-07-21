using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;
using Vfps.Data;

namespace Vfps.Tests.DataTests;

public class CachingPseudonymRepositoryTests : ServiceTests.ServiceTestBase
{
    private CachingPseudonymRepository CreateSut() =>
        new(
            InMemoryPseudonymContext,
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 2048 }),
            new CacheConfig()
        );

    [Fact]
    public async Task CreateIfNotExist_CalledTwiceWithSameOriginalValue_ShouldCacheTheResult()
    {
        var sut = CreateSut();
        var pseudonym = new Data.Models.Pseudonym
        {
            NamespaceName = "existingNamespace",
            OriginalValue = nameof(
                CreateIfNotExist_CalledTwiceWithSameOriginalValue_ShouldCacheTheResult
            ),
            PseudonymValue = "cachedPseudonymValue",
        };

        var first = await sut.CreateIfNotExist(pseudonym);
        first.Should().NotBeNull();

        // Delete it directly from the underlying context, bypassing the repository, so a second
        // CreateIfNotExist call can only return the same result by having actually served the
        // cached hit - if it re-queried instead, it would re-insert the now-missing row.
        InMemoryPseudonymContext.Pseudonyms.Remove(
            InMemoryPseudonymContext.Pseudonyms.Single(p =>
                p.PseudonymValue == "cachedPseudonymValue"
            )
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var second = await sut.CreateIfNotExist(pseudonym);

        second.Should().NotBeNull();
        second!.PseudonymValue.Should().Be(first!.PseudonymValue);
        InMemoryPseudonymContext
            .Pseudonyms.Should()
            .NotContain(p => p.PseudonymValue == "cachedPseudonymValue");
    }
}
