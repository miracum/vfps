using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <summary>
/// Namespace operations shared by the gRPC adapter (<see cref="Services.NamespaceService"/>) and
/// Blazor Server components, so business logic and (eventually) authorization live in exactly
/// one place regardless of caller.
/// </summary>
public interface INamespaceAppService
{
    /// <summary>
    /// Creates the given namespace. <paramref name="namespaceToCreate"/> only needs
    /// Name/Description/PseudonymGenerationMethod/PseudonymLength/PseudonymPrefix/PseudonymSuffix
    /// populated - CreatedAt/LastUpdatedAt are set here.
    /// </summary>
    Task<Namespace> CreateAsync(Namespace namespaceToCreate, CancellationToken cancellationToken);

    /// <summary>
    /// Lists all namespaces. Namespace cardinality is expected to stay low (unlike pseudonyms),
    /// so this intentionally isn't paginated.
    /// </summary>
    Task<IReadOnlyList<Namespace>> GetAllAsync(CancellationToken cancellationToken);
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
