using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Tests.AuthorizationTests;

public class NamespaceAccessGrantCacheTests : ServiceTests.ServiceTestBase
{
    private NamespaceAccessGrantCache CreateSut(TimeSpan cacheDuration) =>
        new(
            ContextFactory,
            Options.Create(
                new AuthorizationConfig { IsEnabled = true, GrantCacheDuration = cacheDuration }
            )
        );

    private async Task<NamespaceAccessGrant> AddGrantAsync(string grantee)
    {
        var now = DateTimeOffset.UtcNow;
        return await new NamespaceAccessGrantRepository(InMemoryPseudonymContext).CreateAsync(
            new NamespaceAccessGrant
            {
                Id = Guid.NewGuid(),
                NamespaceName = "existingNamespace",
                GranteeType = GranteeType.Role,
                Grantee = grantee,
                CanRead = true,
                CreatedAt = now,
                LastUpdatedAt = now,
            },
            CancellationToken.None
        );
    }

    [Fact]
    public async Task GetAllAsync_ShouldServeASecondCallFromTheSnapshot()
    {
        var sut = CreateSut(TimeSpan.FromMinutes(5));
        await AddGrantAsync("first");

        var initial = await sut.GetAllAsync(CancellationToken.None);
        await AddGrantAsync("second");
        var cached = await sut.GetAllAsync(CancellationToken.None);

        initial.Should().ContainSingle();
        cached.Should().ContainSingle("the snapshot is still within its cache duration");
    }

    [Fact]
    public async Task Invalidate_ShouldMakeTheNextCallReadTheDatabaseAgain()
    {
        var sut = CreateSut(TimeSpan.FromMinutes(5));
        await AddGrantAsync("first");
        await sut.GetAllAsync(CancellationToken.None);
        await AddGrantAsync("second");

        sut.Invalidate();

        (await sut.GetAllAsync(CancellationToken.None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllAsync_WithAZeroCacheDuration_ShouldNeverServeAStaleSnapshot()
    {
        var sut = CreateSut(TimeSpan.Zero);
        await AddGrantAsync("first");
        await sut.GetAllAsync(CancellationToken.None);

        await AddGrantAsync("second");

        (await sut.GetAllAsync(CancellationToken.None)).Should().HaveCount(2);
    }
}
