using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vfps.AppServices;
using Vfps.Data;
using Vfps.Protos;

namespace Vfps.Services;

/// <inheritdoc/>
/// <inheritdoc/>
public class NamespaceService(
    PseudonymContext context,
    INamespaceRepository namespaceRepository,
    INamespaceAppService namespaceAppService
) : Protos.NamespaceService.NamespaceServiceBase
{
    private PseudonymContext Context { get; } = context;

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
        };

        Data.Models.Namespace created;
        try
        {
            created = await namespaceAppService.CreateAsync(
                namespaceToCreate,
                context.CancellationToken
            );
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new RpcException(new Status(StatusCode.OutOfRange, ex.Message));
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

        return new NamespaceServiceCreateResponse { Namespace = ToProto(created) };
    }

    /// <inheritdoc/>
    public override async Task<NamespaceServiceGetResponse> Get(
        NamespaceServiceGetRequest request,
        ServerCallContext context
    )
    {
        var @namespace = await namespaceRepository.FindAsync(
            request.Name,
            context.CancellationToken
        );
        if (@namespace is null)
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

        return new NamespaceServiceGetResponse { Namespace = ToProto(@namespace) };
    }

    /// <inheritdoc/>
    public override async Task<NamespaceServiceDeleteResponse> Delete(
        NamespaceServiceDeleteRequest request,
        ServerCallContext context
    )
    {
        var @namespace = await Context.Namespaces.FindAsync(
            request.Name,
            context.CancellationToken
        );
        if (@namespace is null)
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

        // the onDelete-behavior for the Namespace-Pseudonym relationship is set to cascade, so
        // deleting the namespace will automatically delete all pseudonyms contained within.
        Context.Namespaces.Remove(@namespace);
        await Context.SaveChangesAsync(context.CancellationToken);

        return new NamespaceServiceDeleteResponse();
    }

    /// <inheritdoc/>
    public override async Task<NamespaceServiceGetAllResponse> GetAll(
        NamespaceServiceGetAllRequest request,
        ServerCallContext context
    )
    {
        var namespaces = await namespaceAppService.GetAllAsync(context.CancellationToken);

        var response = new NamespaceServiceGetAllResponse();
        response.Namespaces.AddRange(namespaces.Select(ToProto));

        return response;
    }

    private static Namespace ToProto(Data.Models.Namespace @namespace) =>
        new()
        {
            Name = @namespace.Name,
            Description = @namespace.Description,
            PseudonymGenerationMethod = @namespace.PseudonymGenerationMethod,
            PseudonymLength = @namespace.PseudonymLength,
            PseudonymPrefix = @namespace.PseudonymPrefix,
            PseudonymSuffix = @namespace.PseudonymSuffix,
            Meta = new Meta
            {
                CreatedAt = Timestamp.FromDateTimeOffset(@namespace.CreatedAt),
                LastUpdatedAt = Timestamp.FromDateTimeOffset(@namespace.LastUpdatedAt),
            },
        };
}
