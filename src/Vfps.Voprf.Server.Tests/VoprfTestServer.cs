using System.Diagnostics.CodeAnalysis;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.Voprf.Client;
using Vfps.Voprf.Protos;

namespace Vfps.Voprf.Server.Tests;

/// <summary>
/// A running server holding RFC 9497's own key, plus clients pointed at it.
/// </summary>
/// <remarks>
/// <para>
/// The key is the specification's <c>skSm</c>, so any test can compute what the key holder
/// would have produced - see <see cref="Locally"/> - and compare the client's answer against it
/// rather than against the client's own earlier answer.
/// </para>
/// <para>
/// Configuration arrives as environment variables because the host decides its posture before
/// <c>WebApplicationFactory</c> can reach it; see the remarks on
/// <see cref="VoprfServerEndToEndTests"/> for the whole of that story. Those variables are
/// process-global, which is why <c>xunit.runner.json</c> disables parallelization.
/// </para>
/// </remarks>
[ExcludeFromCodeCoverage]
internal sealed class VoprfTestServer : WebApplicationFactory<Program>
{
    /// <summary>RFC 9497's skSm, so expected outputs can be computed locally.</summary>
    public const string PrivateKeyBase64 = "5vc/NEt5s3nxoN034H/2LjjZ9xNFzmKuOpvGCwTM2Qk="; // gitleaks:allow - RFC 9497 test vector

    public const string PublicKeyHex =
        "c803e2cc6b05fc15064549b5920659ca4a77b2cca6f04f6b357009335476ad4e";

    /// <summary>The generation this server answers under.</summary>
    public const string KeyId = "test-key";

    private readonly List<string> variables = [];

    public VoprfTestServer()
    {
        var settings = new Dictionary<string, string?>
        {
            ["VoprfServer:Key:Source"] = "Base64",
            ["VoprfServer:Key:Base64"] = PrivateKeyBase64,
            ["VoprfServer:Key:KeyId"] = KeyId,
            ["VoprfServer:Hardening:IsEnabled"] = "false",
        };

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

    /// <summary>A pseudonymizer talking to this server, with the public key pinned.</summary>
    public IVoprfPseudonymizer CreatePseudonymizer(Action<VoprfClientOptions>? configure = null)
    {
        var channel = GrpcChannel.ForAddress(
            Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = Server.CreateHandler() }
        );

        var options = new VoprfClientOptions
        {
            Address = Server.BaseAddress.ToString(),
            PublicKey = PublicKeyHex,
        };
        configure?.Invoke(options);

        return new VoprfPseudonymizer(
            new VoprfService.VoprfServiceClient(channel),
            Options.Create(options),
            NullLogger<VoprfPseudonymizer>.Instance
        );
    }

    /// <summary>What the key holder would compute for the same value, for comparison.</summary>
    public static byte[] Locally(string value)
    {
        using var keyPair = VoprfKeyPair.Import(Convert.FromBase64String(PrivateKeyBase64));
        return VoprfServer.Evaluate(keyPair, System.Text.Encoding.UTF8.GetBytes(value));
    }
}
