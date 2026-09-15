using System.Diagnostics.CodeAnalysis;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Vfps.Voprf.Protos;

namespace Vfps.Voprf.Server.Tests;

/// <summary>
/// The whole exchange over real gRPC: a client blinds, the server evaluates and proves, the
/// client verifies and unblinds. What this catches that the protocol tests cannot is the wiring
/// - serialisation, the service contract, and the hardening switches actually taking effect.
/// </summary>
public class VoprfServerEndToEndTests
{
    private static readonly byte[] Identifier = Encoding.UTF8.GetBytes("alice@example.com");

    /// <summary>
    /// A fixed key - RFC 9497's own skSm - so the expected outputs can be computed locally.
    /// </summary>
    private const string PrivateKeyBase64 = "5vc/NEt5s3nxoN034H/2LjjZ9xNFzmKuOpvGCwTM2Qk=";

    [ExcludeFromCodeCoverage]
    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly List<string> variables = [];

        /// <remarks>
        /// Settings arrive as environment variables rather than through
        /// <c>ConfigureAppConfiguration</c>, which would be the obvious choice and does not work
        /// here. Program.cs decides what to register - whether to enforce TLS, which
        /// authentication scheme to add, whether to install the rate limiter - before
        /// <c>builder.Build()</c>, and WebApplicationFactory applies its own configuration
        /// callbacks during that Build. Anything it contributes therefore reaches
        /// <c>IOptions&lt;VoprfServerConfig&gt;</c> but arrives far too late for the startup
        /// decisions. Environment variables are read when the builder is constructed, so they
        /// are the one source a test can reach those with.
        ///
        /// They are process-global, which is why this assembly disables test parallelisation.
        /// </remarks>
        public Factory(Dictionary<string, string?> settings)
        {
            foreach (var (key, value) in settings)
            {
                var name = key.Replace(":", "__", StringComparison.Ordinal);
                Environment.SetEnvironmentVariable(name, value);
                variables.Add(name);
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseEnvironment("Development");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            foreach (var name in variables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    private static Dictionary<string, string?> Settings(
        bool hardened,
        bool requireAuthentication
    ) =>
        new()
        {
            ["VoprfServer:Key:Source"] = "Base64",
            ["VoprfServer:Key:Base64"] = PrivateKeyBase64,
            ["VoprfServer:Key:KeyId"] = "test-key",
            ["VoprfServer:MaxBatchSize"] = "4",
            ["VoprfServer:Hardening:IsEnabled"] = hardened ? "true" : "false",
            // TestServer never speaks TLS, so the transport check has to be off for the test
            // host regardless of what else is being exercised.
            ["VoprfServer:Hardening:RequireTls"] = "false",
            ["VoprfServer:Hardening:RequireAuthentication"] = requireAuthentication
                ? "true"
                : "false",
            ["VoprfServer:Hardening:RateLimit:IsEnabled"] = "false",
        };

    private static VoprfService.VoprfServiceClient ClientFor(Factory factory)
    {
        var channel = GrpcChannel.ForAddress(
            factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() }
        );

        return new VoprfService.VoprfServiceClient(channel);
    }

    [Fact]
    public async Task The_exchange_produces_the_same_output_the_key_holder_would_compute()
    {
        using var factory = new Factory(Settings(hardened: false, requireAuthentication: false));
        var client = ClientFor(factory);

        var publicKey = await client.GetPublicKeyAsync(new VoprfServiceGetPublicKeyRequest());
        publicKey.KeyId.Should().Be("test-key");

        using var request = VoprfClient.Blind(Identifier);

        var blindEvaluateRequest = new VoprfServiceBlindEvaluateRequest();
        blindEvaluateRequest.BlindedElements.Add(ByteString.CopyFrom(request.BlindedElement));

        var response = await client.BlindEvaluateAsync(blindEvaluateRequest);
        response.KeyId.Should().Be("test-key");

        var output = VoprfClient.Finalize(
            request,
            response.EvaluatedElements[0].Span,
            VoprfProof.Parse(response.Proof.Span),
            publicKey.PublicKey.Span
        );

        // The key is fixed, so the same computation done locally must agree.
        using var keyPair = VoprfKeyPair.Import(Convert.FromBase64String(PrivateKeyBase64));
        output.Should().Equal(VoprfServer.Evaluate(keyPair, Identifier));
    }

    [Fact]
    public async Task A_batch_round_trips_under_one_proof()
    {
        using var factory = new Factory(Settings(hardened: false, requireAuthentication: false));
        var client = ClientFor(factory);

        var publicKey = await client.GetPublicKeyAsync(new VoprfServiceGetPublicKeyRequest());

        var inputs = new[] { "one", "two", "three" }.Select(Encoding.UTF8.GetBytes).ToArray();
        var requests = inputs.Select(input => VoprfClient.Blind(input)).ToArray();

        try
        {
            var blindEvaluateRequest = new VoprfServiceBlindEvaluateRequest();
            blindEvaluateRequest.BlindedElements.AddRange(
                requests.Select(r => ByteString.CopyFrom(r.BlindedElement))
            );

            var response = await client.BlindEvaluateAsync(blindEvaluateRequest);

            var outputs = VoprfClient.Finalize(
                requests,
                [.. response.EvaluatedElements.Select(e => e.ToByteArray())],
                VoprfProof.Parse(response.Proof.Span),
                publicKey.PublicKey.Span
            );

            using var keyPair = VoprfKeyPair.Import(Convert.FromBase64String(PrivateKeyBase64));
            for (var i = 0; i < inputs.Length; i++)
            {
                outputs[i].Should().Equal(VoprfServer.Evaluate(keyPair, inputs[i]));
            }
        }
        finally
        {
            foreach (var request in requests)
            {
                request.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_batch_over_the_limit_is_rejected()
    {
        using var factory = new Factory(Settings(hardened: false, requireAuthentication: false));
        var client = ClientFor(factory);

        var blindEvaluateRequest = new VoprfServiceBlindEvaluateRequest();
        for (var i = 0; i < 5; i++) // MaxBatchSize is 4 above
        {
            using var request = VoprfClient.Blind(Encoding.UTF8.GetBytes($"subject-{i}"));
            blindEvaluateRequest.BlindedElements.Add(ByteString.CopyFrom(request.BlindedElement));
        }

        var call = async () => await client.BlindEvaluateAsync(blindEvaluateRequest);

        (await call.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task An_empty_batch_is_rejected()
    {
        using var factory = new Factory(Settings(hardened: false, requireAuthentication: false));
        var client = ClientFor(factory);

        var call = async () =>
            await client.BlindEvaluateAsync(new VoprfServiceBlindEvaluateRequest());

        (await call.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task An_element_of_the_wrong_length_is_rejected()
    {
        using var factory = new Factory(Settings(hardened: false, requireAuthentication: false));
        var client = ClientFor(factory);

        var blindEvaluateRequest = new VoprfServiceBlindEvaluateRequest();
        blindEvaluateRequest.BlindedElements.Add(ByteString.CopyFrom(new byte[16]));

        var call = async () => await client.BlindEvaluateAsync(blindEvaluateRequest);

        (await call.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task The_identity_element_is_rejected_rather_than_evaluated()
    {
        using var factory = new Factory(Settings(hardened: false, requireAuthentication: false));
        var client = ClientFor(factory);

        // A canonical encoding, but of the identity - whose "evaluation" carries no key at all.
        var blindEvaluateRequest = new VoprfServiceBlindEvaluateRequest();
        blindEvaluateRequest.BlindedElements.Add(ByteString.CopyFrom(new byte[32]));

        var call = async () => await client.BlindEvaluateAsync(blindEvaluateRequest);

        (await call.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_rejected_when_authentication_is_required()
    {
        using var factory = new Factory(Settings(hardened: true, requireAuthentication: true));
        var client = ClientFor(factory);

        using var request = VoprfClient.Blind(Identifier);
        var blindEvaluateRequest = new VoprfServiceBlindEvaluateRequest();
        blindEvaluateRequest.BlindedElements.Add(ByteString.CopyFrom(request.BlindedElement));

        var call = async () => await client.BlindEvaluateAsync(blindEvaluateRequest);

        // Which of the two depends on the scheme: a bearer challenge is answerable, so it comes
        // back Unauthenticated, while a connection that arrived without a client certificate
        // cannot acquire one mid-request and is simply refused.
        (await call.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .BeOneOf(StatusCode.Unauthenticated, StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task Health_checks_answer_without_credentials()
    {
        using var factory = new Factory(Settings(hardened: true, requireAuthentication: true));
        using var http = factory.CreateClient();

        var response = await http.GetAsync("/healthz", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }
}
