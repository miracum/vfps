using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Tests.DataTests;

public class NamespaceAccessGrantRepositoryTests : ServiceTests.ServiceTestBase
{
    private NamespaceAccessGrantRepository CreateSut() => new(InMemoryPseudonymContext);

    private static NamespaceAccessGrant Grant(string? namespaceName, string grantee)
    {
        var now = DateTimeOffset.UtcNow;
        return new NamespaceAccessGrant
        {
            Id = Guid.NewGuid(),
            NamespaceName = namespaceName,
            GranteeType = GranteeType.Role,
            Grantee = grantee,
            CanRead = true,
            CreatedAt = now,
            LastUpdatedAt = now,
        };
    }

    [Fact]
    public async Task DeletingANamespace_ShouldCascadeToItsGrantsButLeaveGlobalOnesAlone()
    {
        var sut = CreateSut();
        await sut.CreateAsync(Grant("existingNamespace", "scoped"), CancellationToken.None);
        await sut.CreateAsync(Grant(null, "global"), CancellationToken.None);

        await new NamespaceRepository(InMemoryPseudonymContext).DeleteAsync(
            "existingNamespace",
            CancellationToken.None
        );

        var remaining = await sut.GetAllAsync(CancellationToken.None);
        remaining.Should().ContainSingle().Which.Grantee.Should().Be("global");
    }

    [Fact]
    public async Task FindByGranteeAsync_ShouldDistinguishAScopedGrantFromAGlobalOne()
    {
        var sut = CreateSut();
        var scoped = await sut.CreateAsync(
            Grant("existingNamespace", "reader"),
            CancellationToken.None
        );
        var global = await sut.CreateAsync(Grant(null, "reader"), CancellationToken.None);

        var foundScoped = await sut.FindByGranteeAsync(
            "existingNamespace",
            GranteeType.Role,
            "reader",
            CancellationToken.None
        );
        var foundGlobal = await sut.FindByGranteeAsync(
            null,
            GranteeType.Role,
            "reader",
            CancellationToken.None
        );

        foundScoped!.Id.Should().Be(scoped.Id);
        foundGlobal!.Id.Should().Be(global.Id);
    }

    [Fact]
    public async Task UpdateAsync_ShouldOnlyChangeThePermissionFlags()
    {
        var sut = CreateSut();
        var grant = await sut.CreateAsync(
            Grant("existingNamespace", "reader"),
            CancellationToken.None
        );

        grant.CanRead = false;
        grant.CanWrite = true;
        grant.CanReverseLookup = true;
        grant.LastUpdatedAt = DateTimeOffset.UtcNow;
        await sut.UpdateAsync(grant, CancellationToken.None);

        var stored = await sut.FindAsync(grant.Id, CancellationToken.None);
        stored!.CanRead.Should().BeFalse();
        stored.CanWrite.Should().BeTrue();
        stored.CanReverseLookup.Should().BeTrue();
        stored.NamespaceName.Should().Be("existingNamespace");
        stored.Grantee.Should().Be("reader");
    }
}
