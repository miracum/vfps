using Vfps.Data.Models;

namespace Vfps.Authorization;

/// <summary>
/// One caller's effective namespace permissions, resolved once by
/// <see cref="INamespacePermissionChecker.ResolveAsync"/> and then queried without further I/O.
/// Immutable: it answers from the grant snapshot it was built from, so a set of checks made
/// against it can't disagree with one another halfway through an operation.
/// </summary>
public sealed class NamespacePermissions
{
    /// <summary>
    /// Everything is permitted, in every namespace: either authorization is switched off
    /// entirely (in which case everyone is treated as an admin, as everywhere else in this
    /// codebase) or the caller holds an admin role.
    /// </summary>
    public static NamespacePermissions Unrestricted { get; } = new();

    private readonly bool _unrestricted;

    // Namespace-scoped grants, per permission. Ordinal comparison, matching how namespace names
    // are compared everywhere else in this codebase.
    private readonly HashSet<string> _readable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _writable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reverseLookupable = new(StringComparer.Ordinal);

    // Grants with no namespace of their own - they apply to every namespace, including ones
    // created after the grant.
    private bool _globalRead;
    private bool _globalWrite;
    private bool _globalReverseLookup;

    private NamespacePermissions()
    {
        _unrestricted = true;
        IsAdmin = true;
    }

    /// <param name="grants">
    /// Only the grants that actually apply to this caller - see
    /// <see cref="NamespacePermissionChecker"/>, which does the grantee matching.
    /// </param>
    public NamespacePermissions(IEnumerable<NamespaceAccessGrant> grants)
    {
        foreach (var grant in grants)
        {
            if (grant.NamespaceName is null)
            {
                _globalRead |= grant.CanRead;
                _globalWrite |= grant.CanWrite;
                _globalReverseLookup |= grant.CanReverseLookup;
                continue;
            }

            if (grant.CanRead)
            {
                _readable.Add(grant.NamespaceName);
            }

            if (grant.CanWrite)
            {
                _writable.Add(grant.NamespaceName);
            }

            if (grant.CanReverseLookup)
            {
                _reverseLookupable.Add(grant.NamespaceName);
            }
        }
    }

    public bool IsAdmin { get; }

    public bool HasReadAccess(string namespaceName) =>
        _unrestricted || _globalRead || _readable.Contains(namespaceName);

    public bool HasWriteAccess(string namespaceName) =>
        _unrestricted || _globalWrite || _writable.Contains(namespaceName);

    public bool HasReverseLookupAccess(string namespaceName) =>
        _unrestricted || _globalReverseLookup || _reverseLookupable.Contains(namespaceName);
}
