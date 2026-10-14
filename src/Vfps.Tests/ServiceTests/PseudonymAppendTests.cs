using System.Security.Claims;
using Vfps.Config;

namespace Vfps.Tests.ServiceTests;

/// <summary>
/// <see cref="PseudonymAppService.AppendAsync"/> - the non-idempotent counterpart to
/// <see cref="PseudonymAppService.CreateAsync"/>'s grow-to-N for multi-psn namespaces.
/// </summary>
public class PseudonymAppendTests : ServiceTestBase
{
    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    private PseudonymAppService CreateSut(
        AuthorizationConfig? config = null,
        params Data.Models.NamespaceAccessGrant[] grants
    ) =>
        CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            new PseudonymRepository(ContextFactory),
            config,
            grants
        );

    private List<Data.Models.Pseudonym> StoredFor(string namespaceName, string originalValue)
    {
        InMemoryPseudonymContext.ChangeTracker.Clear();
        return InMemoryPseudonymContext
            .Pseudonyms.Where(p =>
                p.NamespaceName == namespaceName && p.OriginalValue == originalValue
            )
            .OrderBy(p => p.SequenceNumber)
            .ToList();
    }

    [Fact]
    public async Task AppendAsync_WithNothingStored_ShouldStoreTheFirstSequence()
    {
        var sut = CreateSut();

        var appended = await sut.AppendAsync(
            "multiPsnNamespace",
            "fresh",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        appended.SequenceNumber.Should().Be(0);
        appended.OriginalValue.Should().Be("fresh");
        StoredFor("multiPsnNamespace", "fresh")
            .Should()
            .ContainSingle()
            .Which.PseudonymValue.Should()
            .Be(appended.PseudonymValue);
    }

    [Fact]
    public async Task AppendAsync_WithAnExistingSet_ShouldAddOneAfterTheHighestSequence()
    {
        var sut = CreateSut();
        await sut.CreateAsync(
            "multiPsnNamespace",
            "grown",
            3,
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var appended = await sut.AppendAsync(
            "multiPsnNamespace",
            "grown",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        appended.SequenceNumber.Should().Be(3);
        var stored = StoredFor("multiPsnNamespace", "grown");
        stored.Select(p => p.SequenceNumber).Should().Equal(0, 1, 2, 3);
        stored.Select(p => p.PseudonymValue).Distinct().Should().HaveCount(4);
        stored[3].PseudonymValue.Should().Be(appended.PseudonymValue);
    }

    [Fact]
    public async Task AppendAsync_CalledRepeatedly_ShouldAddAPseudonymEveryTime()
    {
        var sut = CreateSut();

        var appended = new List<Data.Models.Pseudonym>();
        for (var i = 0; i < 3; i++)
        {
            appended.Add(
                await sut.AppendAsync(
                    "multiPsnNamespace",
                    "repeated",
                    new ClaimsPrincipal(),
                    CancellationToken.None
                )
            );
        }

        appended.Select(p => p.SequenceNumber).Should().Equal(0, 1, 2);
        StoredFor("multiPsnNamespace", "repeated").Should().HaveCount(3);
    }

    [Fact]
    public async Task AppendAsync_FollowedByCreate_ShouldLeaveCreateANoOp()
    {
        // Appending must not change what Create considers already satisfied: a count at or below
        // what's stored still returns the set unchanged, appended pseudonyms included.
        var sut = CreateSut();
        await sut.AppendAsync(
            "multiPsnNamespace",
            "mixed",
            new ClaimsPrincipal(),
            CancellationToken.None
        );
        await sut.AppendAsync(
            "multiPsnNamespace",
            "mixed",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var created = await sut.CreateAsync(
            "multiPsnNamespace",
            "mixed",
            1,
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        created.Should().HaveCount(2);
        StoredFor("multiPsnNamespace", "mixed").Should().HaveCount(2);
    }

    [Fact]
    public async Task AppendAsync_OnANamespaceWithoutMultiplePseudonyms_ShouldThrowAndStoreNothing()
    {
        var sut = CreateSut();
        var countBefore = InMemoryPseudonymContext.Pseudonyms.Count();

        var act = () =>
            sut.AppendAsync(
                "existingNamespace",
                "an original value",
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<MultiplePseudonymsNotAllowedException>();
        InMemoryPseudonymContext.Pseudonyms.Count().Should().Be(countBefore);
    }

    [Fact]
    public async Task AppendAsync_WithoutWriteAccess_ShouldThrowForbidden()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("multiPsnNamespace", "reader", read: true, reverseLookup: true)
        );

        var act = () =>
            sut.AppendAsync(
                "multiPsnNamespace",
                "forbidden",
                UserWithRoles("reader"),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task AppendAsync_WithWriteAccess_ShouldAppend()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("multiPsnNamespace", "writer", write: true)
        );

        var appended = await sut.AppendAsync(
            "multiPsnNamespace",
            "allowed",
            UserWithRoles("writer"),
            CancellationToken.None
        );

        appended.SequenceNumber.Should().Be(0);
    }

    [Fact]
    public async Task AppendAsync_WithUnknownNamespace_ShouldThrowNamespaceNotFound()
    {
        var sut = CreateSut();

        var act = () =>
            sut.AppendAsync(
                "noSuchNamespace",
                "an original value",
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AppendAsync_WithBlankOriginalValue_ShouldThrowArgumentException(
        string blankValue
    )
    {
        var sut = CreateSut();

        var act = () =>
            sut.AppendAsync(
                "multiPsnNamespace",
                blankValue,
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AppendAsync_WithNonMatchingValidationRegex_ShouldThrowAndStoreNothing()
    {
        var @namespace = InMemoryPseudonymContext.Namespaces.Single(n =>
            n.Name == "multiPsnNamespace"
        );
        @namespace.OriginalValueValidationRegex = "^[0-9]+$";
        InMemoryPseudonymContext.Namespaces.Update(@namespace);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var sut = CreateSut();

        var act = () =>
            sut.AppendAsync(
                "multiPsnNamespace",
                "not-a-number",
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<OriginalValueValidationException>();
        StoredFor("multiPsnNamespace", "not-a-number").Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_WithAStoredValueNoLongerMatchingAnEditedRegex_ShouldAppend()
    {
        // The value entered the namespace before the pattern was tightened, so it is already
        // admitted - appending another pseudonym for it stays possible.
        var sut = CreateSut();
        await sut.AppendAsync(
            "multiPsnNamespace",
            "not-a-number",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var @namespace = InMemoryPseudonymContext.Namespaces.Single(n =>
            n.Name == "multiPsnNamespace"
        );
        @namespace.OriginalValueValidationRegex = "^[0-9]+$";
        InMemoryPseudonymContext.Namespaces.Update(@namespace);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var appended = await sut.AppendAsync(
            "multiPsnNamespace",
            "not-a-number",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        appended.SequenceNumber.Should().Be(1);
        StoredFor("multiPsnNamespace", "not-a-number").Should().HaveCount(2);
    }
}
