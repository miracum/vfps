using System.Security.Claims;
using Vfps.Config;

namespace Vfps.Tests.ServiceTests;

/// <summary>
/// <see cref="PseudonymAppService.ImportAsync"/> - what backs the gRPC <c>Import</c> and its
/// <c>POST /v1/namespaces/{namespace}/pseudonyms:import</c> mapping, and the guards that keep
/// Create and Append away from the <see cref="PseudonymGenerationMethod.External"/> namespaces it
/// writes to.
/// </summary>
public class PseudonymImportTests : ServiceTestBase
{
    private const string External = "externalNamespace";
    private const string ExternalMulti = "externalMultiNamespace";

    public PseudonymImportTests()
    {
        AddNamespace(External, allowsMultiple: false);
        AddNamespace(ExternalMulti, allowsMultiple: true);
    }

    private void AddNamespace(string name, bool allowsMultiple, string? regex = null)
    {
        InMemoryPseudonymContext.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = name,
                PseudonymLength = 0,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                PseudonymGenerationMethod = PseudonymGenerationMethod.External,
                AllowsMultiplePseudonyms = allowsMultiple,
                OriginalValueValidationRegex = regex,
            }
        );
        InMemoryPseudonymContext.SaveChanges();
        InMemoryPseudonymContext.ChangeTracker.Clear();
    }

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

    private List<Data.Models.Pseudonym> StoredIn(string namespaceName)
    {
        InMemoryPseudonymContext.ChangeTracker.Clear();
        return InMemoryPseudonymContext
            .Pseudonyms.Where(p => p.NamespaceName == namespaceName)
            .OrderBy(p => p.OriginalValue)
            .ThenBy(p => p.SequenceNumber)
            .ToList();
    }

    [Fact]
    public async Task ImportAsync_WithAFreshPair_ShouldStoreThePseudonymVerbatim()
    {
        var sut = CreateSut();

        var imported = await sut.ImportAsync(
            External,
            "patient-1",
            "Ext-0001",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        imported.OriginalValue.Should().Be("patient-1");
        imported.PseudonymValue.Should().Be("Ext-0001");
        imported.SequenceNumber.Should().Be(0);
        StoredIn(External).Should().ContainSingle().Which.PseudonymValue.Should().Be("Ext-0001");
    }

    [Fact]
    public async Task ImportAsync_WithTheSamePairTwice_ShouldSucceedAndStoreItOnce()
    {
        var sut = CreateSut();
        await sut.ImportAsync(
            External,
            "patient-1",
            "Ext-0001",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var again = await sut.ImportAsync(
            External,
            "patient-1",
            "Ext-0001",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        again.PseudonymValue.Should().Be("Ext-0001");
        StoredIn(External).Should().ContainSingle();
    }

    [Fact]
    public async Task ImportAsync_WithADifferentPseudonymForAStoredOriginalValue_ShouldConflictAndKeepTheStoredOne()
    {
        var sut = CreateSut();
        await sut.ImportAsync(
            External,
            "patient-1",
            "Ext-0001",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var act = () =>
            sut.ImportAsync(
                External,
                "patient-1",
                "Ext-0002",
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        (await act.Should().ThrowAsync<PseudonymConflictException>())
            .Which.Outcome.Should()
            .Be(PseudonymImportOutcome.OriginalValueConflict);
        StoredIn(External).Should().ContainSingle().Which.PseudonymValue.Should().Be("Ext-0001");
    }

    [Fact]
    public async Task ImportAsync_WithAPseudonymAlreadyUsedForAnotherOriginalValue_ShouldConflictWithoutNamingIt()
    {
        var sut = CreateSut();
        await sut.ImportAsync(
            External,
            "patient-1",
            "Ext-0001",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var act = () =>
            sut.ImportAsync(
                External,
                "patient-2",
                "Ext-0001",
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        var ex = (await act.Should().ThrowAsync<PseudonymConflictException>()).Which;
        ex.Outcome.Should().Be(PseudonymImportOutcome.PseudonymValueConflict);
        ex.Message.Should().NotContain("patient-1");
        StoredIn(External).Should().ContainSingle();
    }

    [Fact]
    public async Task ImportAsync_InAMultiPsnNamespace_ShouldAppendADifferentPseudonymForTheSameOriginalValue()
    {
        var sut = CreateSut();
        await sut.ImportAsync(
            ExternalMulti,
            "patient-1",
            "Ext-0001",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var second = await sut.ImportAsync(
            ExternalMulti,
            "patient-1",
            "Ext-0002",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        second.SequenceNumber.Should().Be(1);
        StoredIn(ExternalMulti)
            .Select(p => p.PseudonymValue)
            .Should()
            .Equal("Ext-0001", "Ext-0002");
    }

    [Fact]
    public async Task ImportAsync_IntoANamespaceThatGeneratesItsOwnPseudonyms_ShouldThrowNamespaceNotExternal()
    {
        var sut = CreateSut();

        var act = () =>
            sut.ImportAsync(
                "emptyNamespace",
                "patient-1",
                "Ext-0001",
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<NamespaceNotExternalException>();
        StoredIn("emptyNamespace").Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_IntoAMissingNamespace_ShouldThrowNamespaceNotFound()
    {
        var sut = CreateSut();

        var act = () =>
            sut.ImportAsync(
                "nope",
                "patient-1",
                "Ext-0001",
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<NamespaceNotFoundException>();
    }

    [Theory]
    [InlineData("", "Ext-0001")]
    [InlineData("  ", "Ext-0001")]
    [InlineData("patient-1", "")]
    [InlineData("patient-1", " ")]
    public async Task ImportAsync_WithABlankValue_ShouldThrowArgumentException(
        string originalValue,
        string pseudonymValue
    )
    {
        var sut = CreateSut();

        var act = () =>
            sut.ImportAsync(
                External,
                originalValue,
                pseudonymValue,
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
        StoredIn(External).Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_WithAnOriginalValueFailingTheValidationRegex_ShouldThrowAndStoreNothing()
    {
        AddNamespace("externalValidated", allowsMultiple: false, regex: "^[0-9]+$");
        var sut = CreateSut();

        var act = () =>
            sut.ImportAsync(
                "externalValidated",
                "not-digits",
                "Ext-0001",
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<OriginalValueValidationException>();
        StoredIn("externalValidated").Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_WithoutWriteAccess_ShouldBeForbidden()
    {
        var sut = CreateSut(
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole(External, "reader", read: true, reverseLookup: true)
        );

        var act = () =>
            sut.ImportAsync(
                External,
                "patient-1",
                "Ext-0001",
                UserWithRoles("reader"),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
        StoredIn(External).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_InAnExternalNamespace_ShouldThrowAndGenerateNothing()
    {
        var sut = CreateSut();

        var act = () =>
            sut.CreateAsync(
                External,
                "patient-1",
                1,
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should()
            .ThrowAsync<PseudonymGenerators.PseudonymGenerationMethodNotSupportedException>();
        StoredIn(External).Should().BeEmpty();
    }

    [Fact]
    public async Task AppendAsync_InAnExternalNamespace_ShouldThrowAndGenerateNothing()
    {
        var sut = CreateSut();

        var act = () =>
            sut.AppendAsync(
                ExternalMulti,
                "patient-1",
                null,
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should()
            .ThrowAsync<PseudonymGenerators.PseudonymGenerationMethodNotSupportedException>();
        StoredIn(ExternalMulti).Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_InAnExternalNamespace_ShouldReturnTheImportedPseudonym()
    {
        var sut = CreateSut();
        await sut.ImportAsync(
            External,
            "patient-1",
            "Ext-0001",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        var resolved = await sut.ResolveAsync(
            External,
            "patient-1",
            new ClaimsPrincipal(),
            CancellationToken.None
        );

        resolved.Should().ContainSingle().Which.PseudonymValue.Should().Be("Ext-0001");
    }
}
