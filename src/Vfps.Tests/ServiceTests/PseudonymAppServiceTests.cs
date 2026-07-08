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
}
