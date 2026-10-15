using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vfps.AppServices;
using Vfps.Authorization;
using Vfps.Protos;
using Vfps.PseudonymGenerators;

namespace Vfps.Services;

/// <inheritdoc/>
public class NamespaceService(INamespaceAppService namespaceAppService)
    : Protos.NamespaceService.NamespaceServiceBase
{
    /// <inheritdoc/>
    public override async Task<NamespaceServiceCreateResponse> Create(
        NamespaceServiceCreateRequest request,
        ServerCallContext context
    )
    {
        var namespaceToCreate = new Data.Models.Namespace
        {
            Name = request.Name,
            // Absent, not empty: a caller that omits the description gets a namespace with none,
            // which is what the admin UI's create form already produces and what reads back as an
            // absent field. Sending "" explicitly stores an empty description instead.
            Description = request.HasDescription ? request.Description : null,
            PseudonymLength = request.PseudonymLength,
            PseudonymPrefix = request.PseudonymPrefix,
            PseudonymSuffix = request.PseudonymSuffix,
            PseudonymGenerationMethod = request.PseudonymGenerationMethod,
            OriginalValueValidationRegex = request.OriginalValueValidationRegex,
            AllowsMultiplePseudonyms = request.AllowsMultiplePseudonyms,
            ParentName = request.HasParentName ? request.ParentName : null,
            ParentValidationMode = request.ParentValidationMode,
        };

        Data.Models.Namespace created;
        try
        {
            created = await namespaceAppService.CreateAsync(
                namespaceToCreate,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new RpcException(new Status(StatusCode.OutOfRange, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (NamespaceNotFoundException ex)
        {
            // The parent namespace named in the request doesn't exist.
            var metadata = new Metadata { { "Namespace", ex.NamespaceName } };

            throw new RpcException(new Status(StatusCode.NotFound, ex.Message), metadata);
        }
        catch (NamespaceAlreadyExistsException)
        {
            var metadata = new Metadata { { "Namespace", request.Name } };

            throw new RpcException(
                new Status(
                    StatusCode.AlreadyExists,
                    "A namespace with the same name already exists. Apart from their description and validation regex, namespaces are immutable, for changes please delete and re-create it."
                ),
                metadata
            );
        }
        catch (ForbiddenException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (PseudonymGenerationMethodNotSupportedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }

        return new NamespaceServiceCreateResponse { Namespace = ToProto(created) };
    }

    /// <inheritdoc/>
    public override async Task<NamespaceServiceGetResponse> Get(
        NamespaceServiceGetRequest request,
        ServerCallContext context
    )
    {
        Data.Models.Namespace @namespace;
        try
        {
            @namespace = await namespaceAppService.GetAsync(
                request.Name,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (NamespaceNotFoundException)
        {
            var metadata = new Metadata { { "Namespace", request.Name } };

            throw new RpcException(
                new Status(
                    StatusCode.NotFound,
                    "The requested pseudonym namespace does not exist."
                ),
                metadata
            );
        }
        catch (ForbiddenException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }

        return new NamespaceServiceGetResponse { Namespace = ToProto(@namespace) };
    }

    /// <inheritdoc/>
    public override async Task<NamespaceServiceDeleteResponse> Delete(
        NamespaceServiceDeleteRequest request,
        ServerCallContext context
    )
    {
        try
        {
            await namespaceAppService.DeleteAsync(
                request.Name,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (NamespaceNotFoundException)
        {
            var metadata = new Metadata { { "Namespace", request.Name } };

            throw new RpcException(
                new Status(
                    StatusCode.NotFound,
                    "The requested pseudonym namespace does not exist."
                ),
                metadata
            );
        }
        catch (ForbiddenException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (NamespaceHasChildrenException ex)
        {
            var metadata = new Metadata { { "Namespace", request.Name } };

            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message), metadata);
        }

        return new NamespaceServiceDeleteResponse();
    }

    /// <inheritdoc/>
    public override async Task<NamespaceServiceListChildrenResponse> ListChildren(
        NamespaceServiceListChildrenRequest request,
        ServerCallContext context
    )
    {
        IReadOnlyList<Data.Models.Namespace> children;
        try
        {
            children = await namespaceAppService.ListChildrenAsync(
                request.Name,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (NamespaceNotFoundException)
        {
            var metadata = new Metadata { { "Namespace", request.Name } };

            throw new RpcException(
                new Status(
                    StatusCode.NotFound,
                    "The requested pseudonym namespace does not exist."
                ),
                metadata
            );
        }
        catch (ForbiddenException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }

        var response = new NamespaceServiceListChildrenResponse();
        response.Namespaces.AddRange(children.Select(ToProto));

        return response;
    }

    /// <inheritdoc/>
    public override async Task<NamespaceServiceListResponse> List(
        NamespaceServiceListRequest request,
        ServerCallContext context
    )
    {
        var namespaces = await namespaceAppService.GetAllAsync(
            context.GetUser(),
            context.CancellationToken
        );

        var response = new NamespaceServiceListResponse();
        response.Namespaces.AddRange(namespaces.Select(ToProto));

        return response;
    }

    /// <inheritdoc/>
    [Obsolete("Superseded by List, which returns the same namespaces.")]
    public override async Task<NamespaceServiceGetAllResponse> GetAll(
        NamespaceServiceGetAllRequest request,
        ServerCallContext context
    )
    {
        var listed = await List(new NamespaceServiceListRequest(), context);

        var response = new NamespaceServiceGetAllResponse();
        response.Namespaces.AddRange(listed.Namespaces);

        return response;
    }

    // What Update can change - see UpdateMasks. Everything else is fixed at creation.
    private static readonly string[] UpdatableFields =
    [
        "description",
        "original_value_validation_regex",
    ];

    /// <inheritdoc/>
    public override async Task<NamespaceServiceUpdateResponse> Update(
        NamespaceServiceUpdateRequest request,
        ServerCallContext context
    )
    {
        var changes = request.Namespace ?? new Namespace();
        if (string.IsNullOrEmpty(changes.Name))
        {
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "namespace.name is required.")
            );
        }

        var paths = UpdateMasks.Resolve(request.UpdateMask, changes, UpdatableFields);

        Data.Models.Namespace updated;
        try
        {
            // Whatever the mask leaves out keeps its stored value - the app service replaces both
            // fields, as the admin UI's edit form does. Read first even when the mask covers
            // everything, so a missing namespace is NOT_FOUND before anything else.
            var current = await namespaceAppService.GetAsync(
                changes.Name,
                context.GetUser(),
                context.CancellationToken
            );

            updated = await namespaceAppService.UpdateAsync(
                changes.Name,
                paths.Contains("description")
                    ? (changes.HasDescription ? changes.Description : null)
                    : current.Description,
                paths.Contains("original_value_validation_regex")
                    ? (
                        changes.HasOriginalValueValidationRegex
                            ? changes.OriginalValueValidationRegex
                            : null
                    )
                    : current.OriginalValueValidationRegex,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (NamespaceNotFoundException)
        {
            var metadata = new Metadata { { "Namespace", changes.Name } };

            throw new RpcException(
                new Status(
                    StatusCode.NotFound,
                    "The requested pseudonym namespace does not exist."
                ),
                metadata
            );
        }
        catch (ForbiddenException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (ArgumentException ex)
        {
            // An invalid validation regex.
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }

        return new NamespaceServiceUpdateResponse { Namespace = ToProto(updated) };
    }

    /// <summary>
    /// Every string field of the generated proto type rejects null - Google.Protobuf runs each
    /// setter through ProtoPreconditions.CheckNotNull - while five of the columns behind them are
    /// nullable. Mapping one of those straight across throws ArgumentNullException out of the
    /// projection, and because GetAll and ListChildren project a whole list, a single row with a
    /// NULL in it fails the entire call rather than just its own entry: one namespace created
    /// without a description (what the admin UI's create form stores when the field is left
    /// untouched) made GetAll answer 500 for every caller, for every namespace.
    ///
    /// So each of the five is handled explicitly below, and which of the two treatments a field
    /// gets follows the proto, not a preference: <c>description</c> is a plain proto3 string with
    /// no way to express absence, so a missing one can only come back empty, while the rest are
    /// <c>optional</c> and are left absent instead - which is the more faithful reading of a NULL
    /// column, and distinguishes "never set" from "deliberately empty" for a client that cares.
    /// </summary>
    private static Namespace ToProto(Data.Models.Namespace @namespace)
    {
        var proto = new Namespace
        {
            Name = @namespace.Name,
            PseudonymGenerationMethod = @namespace.PseudonymGenerationMethod,
            PseudonymLength = @namespace.PseudonymLength,
            AllowsMultiplePseudonyms = @namespace.AllowsMultiplePseudonyms,
            ParentValidationMode = @namespace.ParentValidationMode,
            Meta = new Meta
            {
                CreatedAt = Timestamp.FromDateTimeOffset(@namespace.CreatedAt),
                LastUpdatedAt = Timestamp.FromDateTimeOffset(@namespace.LastUpdatedAt),
            },
        };

        // The `optional` fields. A stored empty string is still assigned - it is not null - so
        // this changes nothing for a namespace that has one; only a NULL column, which used to
        // throw, now comes back absent.
        if (@namespace.Description is not null)
        {
            proto.Description = @namespace.Description;
        }

        if (@namespace.PseudonymPrefix is not null)
        {
            proto.PseudonymPrefix = @namespace.PseudonymPrefix;
        }

        if (@namespace.PseudonymSuffix is not null)
        {
            proto.PseudonymSuffix = @namespace.PseudonymSuffix;
        }

        if (@namespace.OriginalValueValidationRegex is not null)
        {
            proto.OriginalValueValidationRegex = @namespace.OriginalValueValidationRegex;
        }

        // Absent here carries meaning beyond "unset", which is why it predates the rest: it is
        // what tells a client this namespace is a pseudonymization root.
        if (@namespace.ParentName is not null)
        {
            proto.ParentName = @namespace.ParentName;
        }

        return proto;
    }
}
