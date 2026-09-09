using System.Security.Claims;
using System.Text.RegularExpressions;
using EntityFramework.Exceptions.Common;
using Microsoft.EntityFrameworkCore;
using Vfps.Authorization;
using Vfps.Data;
using Vfps.Data.Models;
using Vfps.PseudonymGenerators;

namespace Vfps.AppServices;

/// <inheritdoc cref="INamespaceAppService"/>
public class NamespaceAppService(
    INamespaceRepository namespaceRepository,
    INamespacePermissionChecker permissionChecker,
    PseudonymizationMethodsLookup methodsLookup,
    IDbContextFactory<PseudonymContext> contextFactory
) : INamespaceAppService
{
    /// <inheritdoc/>
    public async Task<Namespace> CreateAsync(
        Namespace namespaceToCreate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // Namespace creation has no existing namespace to scope a read/write grant to, so it's
        // gated by admin access only.
        if (!permissionChecker.IsAdmin(user))
        {
            throw new ForbiddenException("Creating a namespace requires admin access.");
        }

        if (namespaceToCreate.PseudonymLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(namespaceToCreate),
                "Pseudonym length must be larger than 0."
            );
        }

        // Some generation methods (UUIDs, SHA-256 hex) have no configurable length at all - catch
        // a mismatch here, at namespace creation, rather than leaving it to fail lazily on every
        // subsequent pseudonym creation in this namespace.
        var fixedLength = methodsLookup.GetFixedPseudonymLength(
            namespaceToCreate.PseudonymGenerationMethod
        );
        if (fixedLength is not null && namespaceToCreate.PseudonymLength != fixedLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(namespaceToCreate),
                $"The '{namespaceToCreate.PseudonymGenerationMethod}' pseudonym generation "
                    + $"method requires a pseudonym length of exactly {fixedLength}."
            );
        }

        // Caught here, at namespace creation, rather than leaving an invalid pattern to fail
        // lazily on every subsequent pseudonym creation in this namespace.
        if (!string.IsNullOrEmpty(namespaceToCreate.OriginalValueValidationRegex))
        {
            try
            {
                _ = new Regex(namespaceToCreate.OriginalValueValidationRegex);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    $"The original value validation regex '{namespaceToCreate.OriginalValueValidationRegex}' "
                        + $"is not a valid regular expression: {ex.Message}",
                    nameof(namespaceToCreate),
                    ex
                );
            }
        }

        // A validation mode with nothing to validate against is a configuration mistake - caught
        // here rather than silently doing nothing on every subsequent pseudonym create.
        // Fully qualified: a `using Vfps.Protos` here would make the unqualified `Namespace`
        // used throughout this file ambiguous with the generated proto message of the same name.
        if (
            namespaceToCreate.ParentValidationMode != Protos.ParentValidationMode.Unspecified
            && string.IsNullOrEmpty(namespaceToCreate.ParentName)
        )
        {
            throw new ArgumentException(
                "A parent validation mode requires a parent namespace to validate against.",
                nameof(namespaceToCreate)
            );
        }

        if (!string.IsNullOrEmpty(namespaceToCreate.ParentName))
        {
            // Can't happen today - the parent has to exist already, and this namespace doesn't
            // yet - but checked explicitly so a future relaxation of the set-once-at-creation
            // rule can't silently introduce a one-node cycle.
            if (namespaceToCreate.ParentName == namespaceToCreate.Name)
            {
                throw new ArgumentException(
                    "A namespace cannot be its own parent.",
                    nameof(namespaceToCreate)
                );
            }

            if (
                await namespaceRepository.FindAsync(namespaceToCreate.ParentName, cancellationToken)
                is null
            )
            {
                throw new NamespaceNotFoundException(namespaceToCreate.ParentName);
            }
        }

        var now = DateTimeOffset.UtcNow;
        namespaceToCreate.CreatedAt = now;
        namespaceToCreate.LastUpdatedAt = now;

        try
        {
            await namespaceRepository.CreateAsync(namespaceToCreate, cancellationToken);
        }
        catch (UniqueConstraintException)
        {
            throw new NamespaceAlreadyExistsException(namespaceToCreate.Name);
        }

        return namespaceToCreate;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Namespace>> GetAllAsync(
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var namespaces = await namespaceRepository.GetAllAsync(cancellationToken);

        // No single target namespace to gate GetAll on - filter the result set per-row against
        // the caller's resolved grants instead. Resolved once up front rather than per row: every
        // row would otherwise re-resolve the same caller against the same grant set.
        var permissions = await permissionChecker.ResolveAsync(user, cancellationToken);
        return [.. namespaces.Where(n => permissions.HasReadAccess(n.Name))];
    }

    /// <inheritdoc/>
    public async Task<Namespace> GetAsync(
        string namespaceName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var @namespace =
            await namespaceRepository.FindAsync(namespaceName, cancellationToken)
            ?? throw new NamespaceNotFoundException(namespaceName);

        if (!await permissionChecker.HasReadAccessAsync(user, namespaceName, cancellationToken))
        {
            throw new ForbiddenException(
                $"Read access to namespace '{namespaceName}' is required."
            );
        }

        return @namespace;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(
        string namespaceName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // Same reasoning as CreateAsync - once the namespace is gone there's no per-namespace
        // write grant left to check, so this is gated by admin access only.
        if (!permissionChecker.IsAdmin(user))
        {
            throw new ForbiddenException("Deleting a namespace requires admin access.");
        }

        if (await namespaceRepository.FindAsync(namespaceName, cancellationToken) is null)
        {
            throw new NamespaceNotFoundException(namespaceName);
        }

        // Refused rather than cascaded: deleting a parent would otherwise destroy every
        // pseudonymization level built on top of it. The self-referencing foreign key is
        // ON DELETE RESTRICT too, so this check is about returning a clear error rather than a
        // raw constraint violation - the database is what actually holds the line under a race.
        if (await namespaceRepository.HasChildrenAsync(namespaceName, cancellationToken))
        {
            throw new NamespaceHasChildrenException(namespaceName);
        }

        await namespaceRepository.DeleteAsync(namespaceName, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Namespace>> ListChildrenAsync(
        string namespaceName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // A fresh, pooled DbContext rather than the scoped namespaceRepository field, for the same
        // reason PseudonymAppService's trusted methods use one: the Blazor pseudonym page calls
        // this from OnInitializedAsync while its data grid's ItemsProvider is concurrently calling
        // PseudonymAppService.SearchAsync, and both would otherwise share the one circuit-scoped
        // PseudonymContext - which isn't safe for concurrent use and throws "A second operation
        // was started on this context instance".
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var repository = new NamespaceRepository(context);

        if (await repository.FindAsync(namespaceName, cancellationToken) is null)
        {
            throw new NamespaceNotFoundException(namespaceName);
        }

        var permissions = await permissionChecker.ResolveAsync(user, cancellationToken);
        if (!permissions.HasReadAccess(namespaceName))
        {
            throw new ForbiddenException(
                $"Read access to namespace '{namespaceName}' is required."
            );
        }

        var children = await repository.ListChildrenAsync(namespaceName, cancellationToken);

        // Filtered per-row, exactly as GetAllAsync does - a caller who can read the parent but
        // not a given child simply doesn't see that child.
        return [.. children.Where(n => permissions.HasReadAccess(n.Name))];
    }
}
