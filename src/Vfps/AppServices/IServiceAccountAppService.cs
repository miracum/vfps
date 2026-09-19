using System.Security.Claims;
using Vfps.Data.Models;

namespace Vfps.AppServices;

/// <summary>
/// Managing the named non-human principals that service-account tokens authenticate as. Every
/// method requires admin access: creating one and granting it access is the same authority as
/// holding that access, which is exactly why a personal access token - which any user can create
/// - carries only its owner's own permissions instead.
///
/// Shaped like <see cref="INamespaceAccessGrantAppService"/>: the caller's
/// <see cref="ClaimsPrincipal"/> is passed explicitly rather than resolved ambiently.
/// </summary>
public interface IServiceAccountAppService
{
    Task<IReadOnlyList<ServiceAccount>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    Task<ServiceAccount?> FindAsync(
        string name,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Creates an account. <paramref name="name"/> is trimmed and lower-cased, and must be 2-64
    /// characters of lower-case letters, digits and hyphens, starting and ending alphanumeric -
    /// the same shape a Kubernetes object or an OIDC client id has, so it reads unambiguously in
    /// a log line and in the grants table next to role names and email addresses.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank or malformed.</exception>
    /// <exception cref="ServiceAccountAlreadyExistsException">That name is taken.</exception>
    Task<ServiceAccount> CreateAsync(
        string name,
        string? description,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Deletes an account, every token issued for it, and every access grant made to it.
    ///
    /// The grants go with it deliberately: they name the account by value, so leaving them behind
    /// would silently re-grant the deleted account's access to anyone who later creates an
    /// account with the same name.
    /// </summary>
    /// <exception cref="ServiceAccountNotFoundException">No such account.</exception>
    Task DeleteAsync(string name, ClaimsPrincipal user, CancellationToken cancellationToken);
}

/// <summary>Thrown when creating a service account whose name is already taken.</summary>
public class ServiceAccountAlreadyExistsException(string name)
    : Exception($"A service account named '{name}' already exists.")
{
    public string Name { get; } = name;
}

/// <summary>Thrown when addressing a service account that doesn't exist.</summary>
public class ServiceAccountNotFoundException(string name)
    : Exception($"No service account named '{name}' exists.")
{
    public string Name { get; } = name;
}
