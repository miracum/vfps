using System.Security.Claims;
using Vfps.Config;

namespace Vfps.Tests.ServiceTests;

public class PseudonymAppServiceTests : ServiceTestBase
{
    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTrustedAsync_WithBlankOriginalValue_ShouldThrowArgumentException(
        string blankValue
    )
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var sut = CreatePseudonymAppService(namespaceRepository, pseudonymRepository);

        var act = () =>
            sut.CreateTrustedAsync("existingNamespace", blankValue, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateTrustedAsync_WithResolvedNamespace_ShouldCreateWithoutLookingItUp()
    {
        // The CSV job runner resolves each namespace once up front and passes the object
        // directly - this overload must not need to look it up again (or even need it to exist
        // in the "Namespaces" table under that exact instance), which is what makes the
        // once-per-job resolution actually save the redundant per-row lookups.
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(InMemoryPseudonymContext),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
        };

        var created = await sut.CreateTrustedAsync(
            @namespace,
            "resolved-namespace-value",
            CancellationToken.None
        );

        created.OriginalValue.Should().Be("resolved-namespace-value");
        created.PseudonymValue.Should().HaveLength(16);
    }

    [Fact]
    public async Task CreateTrustedAsync_CalledManyTimesConcurrently_ShouldNotThrowAndShouldCreateAll()
    {
        // The CSV job runner now calls this concurrently for a whole chunk of rows at once (see
        // CsvPseudonymizationJobRunner.FlushChunkAsync). DbContext instances aren't safe for
        // concurrent use, so this only works because CreateTrustedAsync(Namespace, ...) resolves
        // its own fresh DbContext per call via IDbContextFactory rather than reusing one shared
        // instance - if that regressed, this would throw a DbContext concurrency exception
        // instead of completing cleanly.
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(InMemoryPseudonymContext),
            new PseudonymRepository(InMemoryPseudonymContext)
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
        };

        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(i =>
                    sut.CreateTrustedAsync(
                        @namespace,
                        $"concurrent-value-{i}",
                        CancellationToken.None
                    )
                )
        );

        results
            .Select(r => r.OriginalValue)
            .Should()
            .BeEquivalentTo(Enumerable.Range(0, 20).Select(i => $"concurrent-value-{i}"));
        results.Select(r => r.PseudonymValue).Distinct().Should().HaveCount(20);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTrustedAsync_WithResolvedNamespaceAndBlankOriginalValue_ShouldThrowArgumentException(
        string blankValue
    )
    {
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(InMemoryPseudonymContext),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
        };

        var act = () => sut.CreateTrustedAsync(@namespace, blankValue, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WithBlankOriginalValue_ShouldThrowArgumentExceptionBeforeUpsert()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var sut = CreatePseudonymAppService(namespaceRepository, pseudonymRepository);

        var act = () =>
            sut.CreateAsync("existingNamespace", " ", UserWithRoles(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListAsync_WithAuthorizationEnabledAndNoReadAccess_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true }
        );

        var act = () =>
            sut.ListAsync(
                "existingNamespace",
                25,
                null,
                includeTotalSize: false,
                UserWithRoles(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ReverseLookupAsync_WithReadAccessButNotReverseLookupAccess_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "existingNamespace",
                        ReadRoles = ["read-only"],
                    },
                ],
            }
        );

        var act = () =>
            sut.ReverseLookupAsync(
                "existingNamespace",
                "existingPseudonym",
                UserWithRoles("read-only"),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ReverseLookupAsync_WithReverseLookupAccess_ShouldRevealOriginalValue()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig
            {
                IsEnabled = true,
                NamespaceRules =
                [
                    new NamespaceRule
                    {
                        Namespace = "existingNamespace",
                        ReverseLookupRoles = ["can-reverse-lookup"],
                    },
                ],
            }
        );

        var result = await sut.ReverseLookupAsync(
            "existingNamespace",
            "existingPseudonym",
            UserWithRoles("can-reverse-lookup"),
            CancellationToken.None
        );

        result.Should().NotBeNull();
        result!.OriginalValue.Should().Be("an original value");
    }

    [Fact]
    public async Task ReverseLookupTrustedAsync_WithNoPermissionCheckerAccess_ShouldStillRevealOriginalValue()
    {
        // ReverseLookupTrustedAsync deliberately skips the permission check - it's only called by
        // the CSV job runner, which already verified reverse-lookup access to every namespace a
        // de-pseudonymization job's mappings reference, up front at job creation time.
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true }
        );

        var result = await sut.ReverseLookupTrustedAsync(
            "existingNamespace",
            "existingPseudonym",
            CancellationToken.None
        );

        result.Should().NotBeNull();
        result!.OriginalValue.Should().Be("an original value");
    }

    [Fact]
    public async Task ReverseLookupTrustedAsync_WithUnknownPseudonym_ShouldReturnNull()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var sut = CreatePseudonymAppService(namespaceRepository, pseudonymRepository);

        var result = await sut.ReverseLookupTrustedAsync(
            "existingNamespace",
            "no-such-pseudonym",
            CancellationToken.None
        );

        result.Should().BeNull();
    }
}
