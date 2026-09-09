using Vfps.Data.Models;

namespace Vfps.Tests;

/// <summary>
/// A grant source fixed at construction, standing in for the real
/// <see cref="NamespaceAccessGrantCache"/> so permission checks in tests don't need a database.
/// </summary>
internal sealed class StaticNamespaceAccessGrantCache(params NamespaceAccessGrant[] grants)
    : INamespaceAccessGrantCache
{
    public int InvalidateCallCount { get; private set; }

    public Task<IReadOnlyList<NamespaceAccessGrant>> GetAllAsync(
        CancellationToken cancellationToken
    ) => Task.FromResult<IReadOnlyList<NamespaceAccessGrant>>(grants);

    public void Invalidate() => InvalidateCallCount++;
}

/// <summary>
/// Builders for the access grants that replaced the old <c>Authorization:NamespaceRules</c>
/// config. <c>namespaceName: null</c> is a grant covering every namespace.
/// </summary>
internal static class Grants
{
    public static NamespaceAccessGrant ForRole(
        string? namespaceName,
        string role,
        bool read = false,
        bool write = false,
        bool reverseLookup = false
    ) => Build(namespaceName, GranteeType.Role, role, read, write, reverseLookup);

    public static NamespaceAccessGrant ForEmail(
        string? namespaceName,
        string email,
        bool read = false,
        bool write = false,
        bool reverseLookup = false
    ) => Build(namespaceName, GranteeType.Email, email, read, write, reverseLookup);

    private static NamespaceAccessGrant Build(
        string? namespaceName,
        GranteeType granteeType,
        string grantee,
        bool read,
        bool write,
        bool reverseLookup
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            NamespaceName = namespaceName,
            GranteeType = granteeType,
            Grantee = grantee,
            CanRead = read,
            CanWrite = write,
            CanReverseLookup = reverseLookup,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
}
