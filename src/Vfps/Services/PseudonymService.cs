using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Vfps.AppServices;
using Vfps.Data;
using Vfps.Protos;
using Vfps.PseudonymGenerators;

namespace Vfps.Services;

/// <inheritdoc/>
/// <inheritdoc/>
public class PseudonymService(
    PseudonymContext context,
    PseudonymizationMethodsLookup lookup,
    INamespaceRepository namespaceRepository,
    IPseudonymRepository pseudonymRepository,
    IPseudonymAppService pseudonymAppService
) : Protos.PseudonymService.PseudonymServiceBase
{
    private PseudonymContext Context { get; } = context;

    /// <inheritdoc/>
    public override async Task<PseudonymServiceCreateResponse> Create(
        PseudonymServiceCreateRequest request,
        ServerCallContext context
    )
    {
        var now = DateTimeOffset.UtcNow;

        var @namespace = await namespaceRepository.FindAsync(
            request.Namespace,
            context.CancellationToken
        );
        if (@namespace is null)
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

        var generator = lookup[@namespace.PseudonymGenerationMethod];
        var pseudonymValue = string.Empty;

        using (var activity = Program.ActivitySource.StartActivity("GeneratePseudonym"))
        {
            activity?.SetTag("Method", generator.GetType().Name);

            pseudonymValue = generator.GeneratePseudonym(
                request.OriginalValue,
                @namespace.PseudonymLength
            );
            pseudonymValue =
                @namespace.PseudonymPrefix + pseudonymValue + @namespace.PseudonymSuffix;
        }

        var pseudonym = new Data.Models.Pseudonym()
        {
            CreatedAt = now,
            LastUpdatedAt = now,
            NamespaceName = @namespace.Name,
            OriginalValue = request.OriginalValue,
            PseudonymValue = pseudonymValue,
        };

        Data.Models.Pseudonym? upsertedPseudonym = await pseudonymRepository.CreateIfNotExist(
            pseudonym
        );

        if (upsertedPseudonym is null)
        {
            var metadata = new Metadata { { "Namespace", request.Namespace } };

            throw new RpcException(
                new Status(
                    StatusCode.Internal,
                    "Failed to upsert the pseudonym after several retries."
                ),
                metadata
            );
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
        var pseudonym = await Context
            .Pseudonyms.Where(p =>
                p.NamespaceName == request.Namespace && p.PseudonymValue == request.PseudonymValue
            )
            .FirstOrDefaultAsync();
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
