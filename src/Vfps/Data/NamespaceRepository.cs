using Vfps.Data.Models;

namespace Vfps.Data;

public class NamespaceRepository : INamespaceRepository
{
    public NamespaceRepository(PseudonymContext context)
    {
        Context = context;
    }

    private PseudonymContext Context { get; }

    /// <inheritdoc/>
    public async Task<Namespace> CreateAsync(
        Namespace @namespace,
        CancellationToken cancellationToken
    )
    {
        Context.Add(@namespace);
        await Context.SaveChangesAsync(cancellationToken);
        return @namespace;
    }

    /// <inheritdoc/>
    public async Task<Namespace?> FindAsync(
        string namespaceName,
        CancellationToken cancellationToken
    )
    {
        return await Context.Namespaces.FindAsync(namespaceName, cancellationToken);
    }
}
