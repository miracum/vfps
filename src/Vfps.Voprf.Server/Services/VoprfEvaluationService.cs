using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Vfps.Voprf.Protos;
using Vfps.Voprf.Server.Config;
using Vfps.Voprf.Server.Keys;

namespace Vfps.Voprf.Server.Services;

/// <summary>
/// The key-holding half of the exchange.
/// </summary>
/// <remarks>
/// <para>
/// Every call here is a scalar multiplication per element plus one proof. Nothing is persisted
/// and nothing is derived from the caller, so the service is stateless beyond the key and scales
/// horizontally by running more copies of it against the same key.
/// </para>
/// <para>
/// Note what is absent: there is no method taking an identifier. The server cannot produce a
/// pseudonym, because it never learns what it is pseudonymizing - that is the property the whole
/// arrangement exists to provide, and a convenience endpoint that accepted raw values would
/// quietly discard it.
/// </para>
/// </remarks>
public class VoprfEvaluationService(
    IVoprfKeyProvider keys,
    IOptions<VoprfServerConfig> options,
    ILogger<VoprfEvaluationService> logger
) : Protos.VoprfService.VoprfServiceBase
{
    private readonly VoprfServerConfig config = options.Value;

    /// <inheritdoc/>
    public override Task<VoprfServiceGetPublicKeyResponse> GetPublicKey(
        VoprfServiceGetPublicKeyRequest request,
        ServerCallContext context
    ) =>
        Task.FromResult(
            new VoprfServiceGetPublicKeyResponse
            {
                PublicKey = ByteString.CopyFrom(keys.KeyPair.PublicKey),
                KeyId = keys.KeyId,
            }
        );

    /// <inheritdoc/>
    public override Task<VoprfServiceBlindEvaluateResponse> BlindEvaluate(
        VoprfServiceBlindEvaluateRequest request,
        ServerCallContext context
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        var elements = ReadElements(request);

        byte[][] evaluated;
        VoprfProof proof;
        try
        {
            (evaluated, proof) = VoprfServer.BlindEvaluate(keys.KeyPair, elements);
        }
        catch (VoprfException exception)
        {
            // Deliberately not surfacing which element failed or why. The distinction between
            // "not a canonical encoding", "not on the main subgroup" and "the identity" is
            // exactly the feedback someone probing the group's edges is looking for.
            logger.LogWarning(
                exception,
                "Rejected a batch of {Count} elements from {Caller}: invalid group element",
                elements.Count,
                Describe(context)
            );

            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "One or more blinded elements are invalid.")
            );
        }

        // The audit trail this service can honestly produce: who asked, when, how much, under
        // which key. Not the elements - they are unlinkable by construction, and writing them
        // down is the one way to make a record that ties requests together.
        logger.LogInformation(
            "Evaluated {Count} element(s) for {Caller} under key {KeyId}",
            elements.Count,
            Describe(context),
            keys.KeyId
        );

        var response = new VoprfServiceBlindEvaluateResponse
        {
            Proof = ByteString.CopyFrom(proof.ToBytes()),
            KeyId = keys.KeyId,
        };
        response.EvaluatedElements.AddRange(evaluated.Select(ByteString.CopyFrom));

        return Task.FromResult(response);
    }

    private List<byte[]> ReadElements(VoprfServiceBlindEvaluateRequest request)
    {
        if (request.BlindedElements.Count == 0)
        {
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "No blinded elements were supplied.")
            );
        }

        if (request.BlindedElements.Count > config.MaxBatchSize)
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"A batch carries at most {config.MaxBatchSize} elements; "
                        + $"{request.BlindedElements.Count} were supplied."
                )
            );
        }

        var elements = new List<byte[]>(request.BlindedElements.Count);
        foreach (var element in request.BlindedElements)
        {
            if (element.Length != VoprfSuite.ElementLength)
            {
                throw new RpcException(
                    new Status(
                        StatusCode.InvalidArgument,
                        $"Each blinded element is {VoprfSuite.ElementLength} bytes."
                    )
                );
            }

            elements.Add(element.ToByteArray());
        }

        return elements;
    }

    /// <summary>
    /// Names the caller for the audit log, falling back to the peer address when the connection
    /// carries no identity - which only happens with authentication switched off.
    /// </summary>
    private static string Describe(ServerCallContext context)
    {
        var name = context.GetHttpContext().User.Identity?.Name;
        return string.IsNullOrEmpty(name) ? $"anonymous ({context.Peer})" : name;
    }
}
