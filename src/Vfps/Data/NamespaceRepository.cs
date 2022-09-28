
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
    public async Task<Namespace?> FindAsync(string namespaceName)
    {
        return await Context.Namespaces.FindAsync(namespaceName);
    }
}
