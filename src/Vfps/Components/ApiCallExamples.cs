using Microsoft.AspNetCore.Http;

namespace Vfps.Components;

/// <summary>
/// Ready-to-paste API calls authenticated with a vfps access token, for the Access Tokens page:
/// one pseudonym creation over REST with curl, and the same call over gRPC with grpcurl. The same
/// commands the documentation shows (website/access-control.md), with this deployment's address
/// filled in. The token and the namespace are left to the reader, as shell variables both
/// commands share.
/// </summary>
public static class ApiCallExamples
{
    /// <summary>
    /// The plaintext gRPC port when the HttpGrpc endpoint isn't configured - appsettings.json's own
    /// default, and what compose.yaml and the Helm chart publish.
    /// </summary>
    public const int DefaultGrpcPort = 8081;

    private const string Exports = """
        export VFPS_TOKEN="vfps_pat_..."
        export VFPS_NAMESPACE="..."
        """;

    /// <param name="apiBaseUrl">
    /// Any URL on this deployment - only its scheme, host and port are used. The API is served
    /// from the root, not from under the UI's /ui path base.
    /// </param>
    public static string Curl(Uri apiBaseUrl)
    {
        var origin = apiBaseUrl.GetLeftPart(UriPartial.Authority);

        return $$"""
            {{Exports}}

            curl -X POST \
              -H "Authorization: Bearer $VFPS_TOKEN" \
              -H "Content-Type: application/json" \
              -d '{"originalValue": "to be pseudonymized"}' \
              "{{origin}}/v1/namespaces/$VFPS_NAMESPACE/pseudonyms"
            """;
    }

    /// <param name="grpcAddress">host:port of the plaintext gRPC endpoint.</param>
    public static string Grpcurl(string grpcAddress) =>
            // No -proto: the server offers reflection (see Program.cs), and grpcurl sends the -H
            // header with its reflection requests too, which take the same token as the call. The
            // request body is double-quoted so the shell expands $VFPS_NAMESPACE inside it.
            $$"""
            {{Exports}}

            grpcurl -plaintext \
              -H "authorization: Bearer $VFPS_TOKEN" \
              -d "{\"namespace\": \"$VFPS_NAMESPACE\", \"originalValue\": \"to be pseudonymized\"}" \
              {{grpcAddress}} \
              vfps.api.v1.PseudonymService/Create
            """;

    /// <summary>
    /// The port of the plaintext gRPC endpoint, read from its configured Kestrel URL
    /// (Kestrel:Endpoints:HttpGrpc:Url). Kestrel's own parser, so "http://*:8081" and
    /// "http://+:8081" work as well as an actual host.
    /// </summary>
    public static int GrpcPort(string? kestrelUrl)
    {
        if (string.IsNullOrWhiteSpace(kestrelUrl))
        {
            return DefaultGrpcPort;
        }

        try
        {
            return BindingAddress.Parse(kestrelUrl).Port;
        }
        catch (FormatException)
        {
            return DefaultGrpcPort;
        }
    }
}
