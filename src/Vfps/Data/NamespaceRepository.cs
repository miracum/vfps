using Vfps.Data.Models;

namespace Vfps.Data;

public class NamespaceRepository(PseudonymContext context) : INamespaceRepository
{
    /// <inheritdoc/>
    public async Task<Namespace> CreateAsync(
        Namespace @namespace,
        CancellationToken cancellationToken
    )
    {
        context.Add(@namespace);
        await context.SaveChangesAsync(cancellationToken);
        return @namespace;
    }

    /// <inheritdoc/>
    public async Task<Namespace?> FindAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        return await context.Namespaces.FindAsync(namespaceName, cancellationToken);
    }
}
