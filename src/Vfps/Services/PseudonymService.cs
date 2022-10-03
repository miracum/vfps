using EntityFramework.Exceptions.Common;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Vfps.Data;
using Vfps.Protos;
using Vfps.PseudonymGenerators;

namespace Vfps.Services;

/// <inheritdoc/>
public class PseudonymService : Protos.PseudonymService.PseudonymServiceBase
{
    /// <inheritdoc/>
    public PseudonymService(PseudonymContext context, PseudonymizationMethodsLookup lookup, INamespaceRepository namespaceRepository, IPseudonymRepository pseudonymRepository)
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
    public override async Task<PseudonymServiceCreateResponse> Create(PseudonymServiceCreateRequest request, ServerCallContext context)
    {
        var now = DateTimeOffset.UtcNow;

        var @namespace = await NamespaceRepository.FindAsync(request.Namespace);
        if (@namespace is null)
        {
            var metadata = new Metadata
                {
                    { "Namespace", request.Namespace }
                };

            throw new RpcException(new Status(StatusCode.NotFound, "The requested pseudonym namespace does not exist."), metadata);
        }

        var generator = Lookup[@namespace.PseudonymGenerationMethod];
        var pseudonymValue = string.Empty;

        using (var activity = Program.ActivitySource.StartActivity("GeneratePseudonym"))
        {
            activity?.SetTag("Method", generator.GetType().Name);

            pseudonymValue = generator.GeneratePseudonym(request.OriginalValue, @namespace.PseudonymLength);
            pseudonymValue = $"{@namespace.PseudonymPrefix}{pseudonymValue}{@namespace.PseudonymSuffix}";
        }

        var pseudonym = new Data.Models.Pseudonym()
        {
            CreatedAt = now,
            LastUpdatedAt = now,
            NamespaceName = @namespace.Name,
            OriginalValue = request.OriginalValue,
            PseudonymValue = pseudonymValue,
        };

        Data.Models.Pseudonym? upsertedPseudonym = await PseudonymRepository.CreateIfNotExist(pseudonym);

        if (upsertedPseudonym is null)
        {
            var metadata = new Metadata
            {
                { "Namespace", request.Namespace },
            };

            throw new RpcException(new Status(StatusCode.Internal, "Failed to upsert the pseudonym after several retries."));
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
    public override async Task<PseudonymServiceGetResponse> Get(PseudonymServiceGetRequest request, ServerCallContext context)
    {
        var pseudonym = await Context.Pseudonyms
            .Where(p => p.NamespaceName == request.Namespace && p.PseudonymValue == request.PseudonymValue)
            .FirstOrDefaultAsync();
        if (pseudonym is null)
        {
            var metadata = new Metadata
                {
                    { "Namespace", request.Namespace },
                    { "Pseudonym", request.PseudonymValue }
                };

            throw new RpcException(new Status(StatusCode.NotFound, "The requested pseudonym does not exist in the namespace."), metadata);
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
}
