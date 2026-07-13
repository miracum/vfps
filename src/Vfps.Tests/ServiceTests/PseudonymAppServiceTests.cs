using System.Security.Claims;
using Vfps.Config;

namespace Vfps.Tests.ServiceTests;

public class PseudonymAppServiceTests : ServiceTestBase
{
    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

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
