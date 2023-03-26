using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Vfps.Data;
using Vfps.Protos;
using Vfps.PseudonymGenerators;

namespace Vfps.Services;

/// <inheritdoc/>
public class PseudonymService : Protos.PseudonymService.PseudonymServiceBase
{
    /// <inheritdoc/>
    public PseudonymService(
        PseudonymContext context,
        PseudonymizationMethodsLookup lookup,
        INamespaceRepository namespaceRepository,
        IPseudonymRepository pseudonymRepository
    )
    {
        Context = context;
        Lookup = lookup;
        NamespaceRepository = namespaceRepository;
        PseudonymRepository = pseudonymRepository;
    }

    private PseudonymContext Context { get; }
    private PseudonymizationMethodsLookup Lookup { get; }
    private INamespaceRepository NamespaceRepository { get; }
    private IPseudonymRepository PseudonymRepository { get; }

    /// <inheritdoc/>
    public override async Task<PseudonymServiceCreateResponse> Create(
        PseudonymServiceCreateRequest request,
        ServerCallContext context
    )
    {
        var now = DateTimeOffset.UtcNow;

        var @namespace = await NamespaceRepository.FindAsync(
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

        var generator = Lookup[@namespace.PseudonymGenerationMethod];
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

        Data.Models.Pseudonym? upsertedPseudonym = await PseudonymRepository.CreateIfNotExist(
            pseudonym
        );

        if (upsertedPseudonym is null)
        {
            var metadata = new Metadata { { "Namespace", request.Namespace }, };

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
            }
        };
    }

    /// <inheritdoc/>
    public override async Task<PseudonymServiceGetResponse> Get(
        PseudonymServiceGetRequest request,
        ServerCallContext context
    )
    {
        var pseudonym = await Context.Pseudonyms
            .Where(
                p =>
                    p.NamespaceName == request.Namespace
                    && p.PseudonymValue == request.PseudonymValue
            )
            .FirstOrDefaultAsync();
        if (pseudonym is null)
        {
            var metadata = new Metadata
            {
                { "Namespace", request.Namespace },
                { "Pseudonym", request.PseudonymValue }
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
            }
        };
    }

    /// <inheritdoc/>
    public override async Task<PseudonymServiceListResponse> List(
        PseudonymServiceListRequest request,
        ServerCallContext context
    )
    {
        if (!Context.Namespaces.Where(n => n.Name == request.Namespace).Any())
        {
            var metadata = new Metadata { { "Namespace", request.Namespace }, };

            throw new RpcException(
                new Status(
                    StatusCode.NotFound,
                    "The requested pseudonym namespace does not exist."
                ),
                metadata
            );
        }

        var requestPaginationToken = new PseudonymListPaginationToken();
        if (!string.IsNullOrEmpty(request.PageToken))
        {
            var decoded = WebEncoders.Base64UrlDecode(request.PageToken);
            requestPaginationToken.MergeFrom(decoded);
        }

        var createdOnOrBefore =
            requestPaginationToken.PseudonymsCreatedOnOrBefore?.ToDateTimeOffset()
            ?? DateTimeOffset.UtcNow;
        var pageSize = request.PageSize <= 0 ? 25 : request.PageSize;
        var offset = requestPaginationToken.Offset;

        var pseudonyms = await Context.Pseudonyms
            .Where(pseudonym => pseudonym.NamespaceName == request.Namespace)
            .Where(pseudonym => pseudonym.CreatedAt <= createdOnOrBefore)
            .OrderByDescending(pseudonym => pseudonym.CreatedAt)
            .Skip(offset)
            .Take(pageSize)
            .Select(
                pseudonym =>
                    new Pseudonym
                    {
                        Namespace = pseudonym.NamespaceName,
                        OriginalValue = pseudonym.OriginalValue,
                        PseudonymValue = pseudonym.PseudonymValue,
                        Meta = new Meta
                        {
                            CreatedAt = Timestamp.FromDateTimeOffset(pseudonym.CreatedAt),
                            LastUpdatedAt = Timestamp.FromDateTimeOffset(pseudonym.LastUpdatedAt),
                        },
                    }
            )
            .ToListAsync();

        var paginationToken = new PseudonymListPaginationToken
        {
            Offset = offset + pageSize,
            PseudonymsCreatedOnOrBefore = Timestamp.FromDateTimeOffset(createdOnOrBefore),
        };
        var opaqueToken = WebEncoders.Base64UrlEncode(paginationToken.ToByteArray());

        if (pseudonyms.Count < pageSize)
        {
            opaqueToken = string.Empty;
        }

        var response = new PseudonymServiceListResponse
        {
            Namespace = request.Namespace,
            NextPageToken = opaqueToken,
        };

        response.Pseudonyms.AddRange(pseudonyms);

        if (request.IncludeTotalSize)
        {
            response.TotalSize = await Context.Pseudonyms
                .Where(n => n.NamespaceName == request.Namespace)
                .LongCountAsync();
        }

        return response;
    }
}
