using System.Security.Claims;
using Vfps.Config;

namespace Vfps.Tests.ServiceTests;

/// <summary>
/// The lookup-only half of <see cref="PseudonymAppService"/> - what backs the gRPC
/// <c>Resolve</c>, the FHIR <c>$resolve-pseudonym</c> operation and the two lookup-only
/// <see cref="Data.Models.PseudonymizeMode"/>s a CSV job can run in.
/// </summary>
public class PseudonymResolveTests : ServiceTestBase
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

    [Fact]
    public async Task ResolveAsync_WithAStoredOriginalValue_ShouldReturnItsExistingPseudonym()
    {
        var sut = CreateSut();

        var resolved = await sut.ResolveAsync(
            "existingNamespace",
            "an original value",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        resolved.Should().ContainSingle();
        resolved[0].PseudonymValue.Should().Be("existingPseudonym");
    }

    [Fact]
    public async Task ResolveAsync_WithAnUnknownOriginalValue_ShouldReturnEmptyAndStoreNothing()
    {
        var sut = CreateSut();
        var countBefore = InMemoryPseudonymContext.Pseudonyms.Count();

        var resolved = await sut.ResolveAsync(
            "existingNamespace",
            "never stored",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        resolved.Should().BeEmpty();

        // The entire point of the operation: an unknown value leaves the namespace exactly as it
        // was, rather than quietly recording a subject that was never meant to be in it.
        InMemoryPseudonymContext.Pseudonyms.Count().Should().Be(countBefore);
    }

    [Fact]
    public async Task ResolveAsync_WithoutWriteAccess_ShouldThrowForbidden()
    {
        // Gated on write, not read - see IPseudonymAppService.ResolveAsync for why: this tells a
        // caller whether a value is in the namespace at all, which a create call never can.
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "reader", read: true, reverseLookup: true)
        );

        var act = () =>
            sut.ResolveAsync(
                "existingNamespace",
                "an original value",
                UserWithRoles("reader"),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ResolveAsync_WithWriteAccess_ShouldResolve()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "writer", write: true)
        );

        var resolved = await sut.ResolveAsync(
            "existingNamespace",
            "an original value",
            UserWithRoles("writer"),
            CancellationToken.None
        );

        resolved.Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveAsync_WithUnknownNamespace_ShouldThrowNamespaceNotFound()
    {
        var sut = CreateSut();

        var act = () =>
            sut.ResolveAsync(
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
    public async Task ResolveAsync_WithBlankOriginalValue_ShouldThrowArgumentException(
        string blankValue
    )
    {
        var sut = CreateSut();

        var act = () =>
            sut.ResolveAsync(
                "existingNamespace",
                blankValue,
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ResolveAsync_WithNonMatchingValidationRegex_ShouldThrowRatherThanReportAbsent()
    {
        // A value the namespace could never have stored is rejected as invalid, not reported as
        // merely absent - the same ordering CreateAsync uses, so the two answer identically on a
        // value neither of them would accept.
        var @namespace = InMemoryPseudonymContext.Namespaces.Single(n =>
            n.Name == "emptyNamespace"
        );
        @namespace.OriginalValueValidationRegex = "^[0-9]+$";
        InMemoryPseudonymContext.Namespaces.Update(@namespace);
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var sut = CreateSut();

        var act = () =>
            sut.ResolveAsync(
                "emptyNamespace",
                "not-a-number",
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<OriginalValueValidationException>();
    }

    [Fact]
    public async Task ResolveTrustedBatchAsync_ShouldOmitUnknownValuesRatherThanMapThemToNull()
    {
        // The contract the CSV flush path is written against, and the same one
        // ReverseLookupTrustedBatchAsync has: an absent key is how "nothing stored" is expressed.
        var sut = CreateSut();
        var @namespace = InMemoryPseudonymContext.Namespaces.Single(n =>
            n.Name == "existingNamespace"
        );

        var resolved = await sut.ResolveTrustedBatchAsync(
            [(@namespace, "an original value"), (@namespace, "never stored")],
            CancellationToken.None
        );

        resolved
            .Should()
            .ContainKey(("existingNamespace", "an original value"))
            .WhoseValue.PseudonymValue.Should()
            .Be("existingPseudonym");
        resolved.Should().NotContainKey(("existingNamespace", "never stored"));
    }

    [Fact]
    public async Task ResolveTrustedBatchAsync_WithAMultiPsnNamespace_ShouldReturnTheFirstSequence()
    {
        // Matches what the single-value CreateTrustedAsync overload the create path uses returns,
        // so switching a job to a lookup-only mode doesn't switch which pseudonym lands in a cell.
        var sut = CreateSut();
        var @namespace = InMemoryPseudonymContext.Namespaces.Single(n =>
            n.Name == "multiPsnNamespace"
        );

        await sut.CreateTrustedAsync(@namespace, "multi", 3, CancellationToken.None);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var stored = InMemoryPseudonymContext
            .Pseudonyms.Where(p =>
                p.NamespaceName == "multiPsnNamespace" && p.OriginalValue == "multi"
            )
            .OrderBy(p => p.SequenceNumber)
            .ToList();
        stored.Should().HaveCount(3);

        var resolved = await sut.ResolveTrustedBatchAsync(
            [(@namespace, "multi")],
            CancellationToken.None
        );

        resolved.Should().ContainSingle();
        resolved[("multiPsnNamespace", "multi")]
            .PseudonymValue.Should()
            .Be(stored[0].PseudonymValue);
    }

    [Fact]
    public async Task ResolveTrustedBatchAsync_WithNoRequests_ShouldNotTouchTheDatabase()
    {
        var sut = CreateSut();

        var resolved = await sut.ResolveTrustedBatchAsync([], CancellationToken.None);

        resolved.Should().BeEmpty();
    }
}
