using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vfps.AppServices;
using Vfps.Authorization;
using Vfps.Protos;
using Vfps.PseudonymGenerators;

namespace Vfps.Services;

/// <inheritdoc/>
public class PseudonymService(IPseudonymAppService pseudonymAppService)
    : Protos.PseudonymService.PseudonymServiceBase
{
    /// <inheritdoc/>
    public override async Task<PseudonymServiceCreateResponse> Create(
        PseudonymServiceCreateRequest request,
        ServerCallContext context
    )
    {
        var count = request.HasCount ? request.Count : 1;

        IReadOnlyList<Data.Models.Pseudonym> upsertedPseudonyms;
        try
        {
            upsertedPseudonyms = await pseudonymAppService.CreateAsync(
                request.Namespace,
                request.OriginalValue,
                count,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (NamespaceNotFoundException)
        {
            var metadata = new Metadata { { "Namespace", request.Namespace } };

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
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (OriginalValueValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (MultiplePseudonymsNotAllowedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (PseudonymGenerationMethodNotSupportedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
        catch (PseudonymUpsertFailedException ex)
        {
            var metadata = new Metadata { { "Namespace", request.Namespace } };

            throw new RpcException(new Status(StatusCode.Internal, ex.Message), metadata);
        }

        var response = new PseudonymServiceCreateResponse
        {
            Pseudonym = ToProto(upsertedPseudonyms[0]),
        };
        response.Pseudonyms.AddRange(upsertedPseudonyms.Select(ToProto));
        return response;
    }

    private static Pseudonym ToProto(Data.Models.Pseudonym pseudonym) =>
        new()
        {
            Namespace = pseudonym.NamespaceName,
            OriginalValue = pseudonym.OriginalValue,
            PseudonymValue = pseudonym.PseudonymValue,
            SequenceNumber = pseudonym.SequenceNumber,
            Meta = new Meta
            {
                CreatedAt = Timestamp.FromDateTimeOffset(pseudonym.CreatedAt),
                LastUpdatedAt = Timestamp.FromDateTimeOffset(pseudonym.LastUpdatedAt),
            },
        };

    /// <inheritdoc/>
    public override async Task<PseudonymServiceGetResponse> Get(
        PseudonymServiceGetRequest request,
        ServerCallContext context
    )
    {
        Data.Models.Pseudonym? pseudonym;
        try
        {
            pseudonym = await pseudonymAppService.ReverseLookupAsync(
                request.Namespace,
                request.PseudonymValue,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (ForbiddenException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }

        if (pseudonym is null)
        {
            var metadata = new Metadata
            {
                { "Namespace", request.Namespace },
                { "Pseudonym", request.PseudonymValue },
            };

            throw new RpcException(
                new Status(
                    StatusCode.NotFound,
                    "The requested pseudonym does not exist in the namespace."
                ),
                metadata
            );
        }

        return new PseudonymServiceGetResponse { Pseudonym = ToProto(pseudonym) };
    }

    /// <inheritdoc/>
    public override async Task<PseudonymServiceListResponse> List(
        PseudonymServiceListRequest request,
        ServerCallContext context
    )
    {
        PseudonymPageDto page;
        try
        {
            page = await pseudonymAppService.ListAsync(
                request.Namespace,
                request.PageSize,
                request.PageToken,
                request.IncludeTotalSize,
                context.GetUser(),
                context.CancellationToken
            );
        }
        catch (NamespaceNotFoundException)
        {
            var metadata = new Metadata { { "Namespace", request.Namespace } };

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

        var response = new PseudonymServiceListResponse
        {
            Namespace = request.Namespace,
            NextPageToken = page.NextPageToken ?? string.Empty,
        };

        // OriginalValue is intentionally never set here - List never exposes it, by design.
        // See PseudonymAppService.ListAsync / PseudonymSummaryDto.
        response.Pseudonyms.AddRange(
            page.Items.Select(p => new Pseudonym
            {
                Namespace = p.NamespaceName,
                PseudonymValue = p.PseudonymValue,
                Meta = new Meta
                {
                    CreatedAt = Timestamp.FromDateTimeOffset(p.CreatedAt),
                    LastUpdatedAt = Timestamp.FromDateTimeOffset(p.LastUpdatedAt),
                },
            })
        );

        if (page.TotalSize.HasValue)
        {
            response.TotalSize = page.TotalSize.Value;
        }

        return response;
    }
}
