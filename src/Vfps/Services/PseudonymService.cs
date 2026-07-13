using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vfps.AppServices;
using Vfps.Authorization;
using Vfps.Protos;

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
        Data.Models.Pseudonym upsertedPseudonym;
        try
        {
            upsertedPseudonym = await pseudonymAppService.CreateAsync(
                request.Namespace,
                request.OriginalValue,
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
        catch (PseudonymUpsertFailedException ex)
        {
            var metadata = new Metadata { { "Namespace", request.Namespace } };

            throw new RpcException(new Status(StatusCode.Internal, ex.Message), metadata);
        }

        return new PseudonymServiceCreateResponse
        {
            Pseudonym = new Pseudonym
            {
                Namespace = upsertedPseudonym.NamespaceName,
                OriginalValue = upsertedPseudonym.OriginalValue,
                PseudonymValue = upsertedPseudonym.PseudonymValue,
                Meta = new Meta
                {
                    CreatedAt = Timestamp.FromDateTimeOffset(upsertedPseudonym.CreatedAt),
                    LastUpdatedAt = Timestamp.FromDateTimeOffset(upsertedPseudonym.LastUpdatedAt),
                },
            },
        };
    }

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

        return new PseudonymServiceGetResponse
        {
            Pseudonym = new Pseudonym
            {
                Namespace = pseudonym.NamespaceName,
                OriginalValue = pseudonym.OriginalValue,
                PseudonymValue = pseudonym.PseudonymValue,
                Meta = new Meta
                {
                    CreatedAt = Timestamp.FromDateTimeOffset(pseudonym.CreatedAt),
                    LastUpdatedAt = Timestamp.FromDateTimeOffset(pseudonym.LastUpdatedAt),
                },
            },
        };
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
