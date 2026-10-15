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
            null,
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
            null,
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
                    null,
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
            null,
            new ClaimsPrincipal(),
            CancellationToken.None
        );
        await sut.AppendAsync(
            "multiPsnNamespace",
            "mixed",
            null,
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
                null,
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
                null,
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
            null,
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
                null,
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
                null,
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
                null,
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
            null,
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
            null,
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        appended.SequenceNumber.Should().Be(1);
        StoredFor("multiPsnNamespace", "not-a-number").Should().HaveCount(2);
    }

    [Fact]
    public async Task AppendAsync_RetriedWithTheSameRequestId_ShouldReturnTheFirstPseudonymAndStoreNothing()
    {
        var sut = CreateSut();
        var requestId = Guid.NewGuid();

        var first = await sut.AppendAsync(
            "multiPsnNamespace",
            "retried",
            requestId,
            new ClaimsPrincipal(),
            CancellationToken.None
        );
        var retried = await sut.AppendAsync(
            "multiPsnNamespace",
            "retried",
            requestId,
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        retried.PseudonymValue.Should().Be(first.PseudonymValue);
        retried.SequenceNumber.Should().Be(first.SequenceNumber);
        StoredFor("multiPsnNamespace", "retried").Should().ContainSingle();
    }

    [Fact]
    public async Task AppendAsync_WithADifferentRequestId_ShouldAppendAgain()
    {
        var sut = CreateSut();

        foreach (var _ in Enumerable.Range(0, 2))
        {
            await sut.AppendAsync(
                "multiPsnNamespace",
                "distinct-requests",
                Guid.NewGuid(),
                new ClaimsPrincipal(),
                CancellationToken.None
            );
        }

        StoredFor("multiPsnNamespace", "distinct-requests").Should().HaveCount(2);
    }

    [Fact]
    public async Task AppendAsync_ReusingARequestIdForAnotherValue_ShouldThrowWithoutRevealingAnything()
    {
        var sut = CreateSut();
        var requestId = Guid.NewGuid();
        var first = await sut.AppendAsync(
            "multiPsnNamespace",
            "first-value",
            requestId,
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var act = () =>
            sut.AppendAsync(
                "multiPsnNamespace",
                "second-value",
                requestId,
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        var thrown = await act.Should().ThrowAsync<AppendRequestIdConflictException>();
        thrown.Which.Message.Should().NotContain("first-value");
        thrown.Which.Message.Should().NotContain(first.PseudonymValue);
        StoredFor("multiPsnNamespace", "second-value").Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_RetriedWithoutWriteAccess_ShouldStillBeForbidden()
    {
        // The retry lookup must not become a way around the permission check.
        var requestId = Guid.NewGuid();
        await CreateSut()
            .AppendAsync(
                "multiPsnNamespace",
                "guarded",
                requestId,
                new ClaimsPrincipal(),
                CancellationToken.None
            );
        var restricted = CreateSut(
            new AuthorizationConfig { IsEnabled = true, AdminRoles = ["admin"] },
            Grants.ForRole("multiPsnNamespace", "reader", read: true)
        );

        var act = () =>
            restricted.AppendAsync(
                "multiPsnNamespace",
                "guarded",
                requestId,
                UserWithRoles("reader"),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }
}
