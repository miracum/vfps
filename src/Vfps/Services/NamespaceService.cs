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
            Description = request.Description,
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
                    "A namespace with the same name already exists. Namespaces are immutable, for changes please delete and re-create it."
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
    public override async Task<NamespaceServiceGetAllResponse> GetAll(
        NamespaceServiceGetAllRequest request,
        ServerCallContext context
    )
    {
        var namespaces = await namespaceAppService.GetAllAsync(
            context.GetUser(),
            context.CancellationToken
        );

        var response = new NamespaceServiceGetAllResponse();
        response.Namespaces.AddRange(namespaces.Select(ToProto));

        return response;
    }

    private static Namespace ToProto(Data.Models.Namespace @namespace)
    {
        var proto = new Namespace
        {
            Name = @namespace.Name,
            Description = @namespace.Description,
            PseudonymGenerationMethod = @namespace.PseudonymGenerationMethod,
            PseudonymLength = @namespace.PseudonymLength,
            PseudonymPrefix = @namespace.PseudonymPrefix,
            PseudonymSuffix = @namespace.PseudonymSuffix,
            OriginalValueValidationRegex = @namespace.OriginalValueValidationRegex,
            AllowsMultiplePseudonyms = @namespace.AllowsMultiplePseudonyms,
            ParentValidationMode = @namespace.ParentValidationMode,
            Meta = new Meta
            {
                CreatedAt = Timestamp.FromDateTimeOffset(@namespace.CreatedAt),
                LastUpdatedAt = Timestamp.FromDateTimeOffset(@namespace.LastUpdatedAt),
            },
        };

        // Assigned only when set: parent_name is an `optional` proto field, whose generated
        // setter rejects null outright, and leaving it absent (rather than present-but-empty) is
        // what tells a client this namespace is a root.
        if (@namespace.ParentName is not null)
        {
            proto.ParentName = @namespace.ParentName;
        }

        return proto;
    }
}
