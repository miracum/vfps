using System.Security.Claims;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <summary>
/// Namespace operations shared by the gRPC adapter (<see cref="Services.NamespaceService"/>) and
/// Blazor Server components, so business logic and authorization live in exactly one place
/// regardless of caller. Every method takes the caller's <see cref="ClaimsPrincipal"/> explicitly
/// (rather than resolving "current user" ambiently) since it needs to work identically whether
/// the caller is a gRPC request or a Blazor Server component.
/// </summary>
public interface INamespaceAppService
{
    /// <summary>
    /// Creates the given namespace. <paramref name="namespaceToCreate"/> only needs
    /// Name/Description/PseudonymGenerationMethod/PseudonymLength/PseudonymPrefix/PseudonymSuffix
    /// populated - CreatedAt/LastUpdatedAt are set here. Requires admin access.
    /// </summary>
    Task<Namespace> CreateAsync(
        Namespace namespaceToCreate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Lists namespaces the caller has read access to (all of them, if admin). Namespace
    /// cardinality is expected to stay low (unlike pseudonyms), so this intentionally isn't
    /// paginated.
    /// </summary>
    Task<IReadOnlyList<Namespace>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>Gets a single namespace by name. Requires read access to that namespace.</summary>
    /// <exception cref="NamespaceNotFoundException">No namespace named <paramref name="namespaceName"/> exists.</exception>
    Task<Namespace> GetAsync(
        string namespaceName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes a namespace and every pseudonym it contains. Requires admin access - there's no
    /// existing namespace-scoped write grant to check once it's gone, same reasoning as
    /// <see cref="CreateAsync"/>.
    /// </summary>
    /// <exception cref="NamespaceNotFoundException">No namespace named <paramref name="namespaceName"/> exists.</exception>
    Task DeleteAsync(
        string namespaceName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Thrown when creating a namespace whose name already exists. Namespaces are immutable by
/// design (see the existing gRPC API) - callers must delete and re-create instead.
/// </summary>
public class NamespaceAlreadyExistsException(string namespaceName)
    : Exception($"A namespace named '{namespaceName}' already exists.")
{
    public string NamespaceName { get; } = namespaceName;
}
