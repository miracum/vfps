using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Reflection.V1Alpha;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Task = System.Threading.Tasks.Task;

namespace Vfps.Tests.WebAppTests;

/// <summary>
/// Boots the app the way a deployment with authorization switched on does. The authority is never
/// reached: a request carrying no bearer token is refused by the JWT handler before it looks at
/// any discovery document, which is exactly the path under test here.
/// </summary>
[ExcludeFromCodeCoverage]
public class AuthorizationEnabledTestFactory : IntegrationTestFactory<Program, PseudonymContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Authorization:IsEnabled", "true");
        // Must be HTTPS: RequireHttpsMetadata is only relaxed for the Development environment, and
        // the test host runs as "Test".
        builder.UseSetting("Authorization:Authority", "https://localhost:1/realms/vfps");
        builder.UseSetting("Authorization:Audience", "vfps-api");
        builder.UseSetting("Authorization:ClientId", "vfps-web");

        base.ConfigureWebHost(builder);
    }
}

public class ApiAuthorizationTests(AuthorizationEnabledTestFactory factory)
    : IClassFixture<AuthorizationEnabledTestFactory>
{
    // The JSON-transcoded routes of the two gRPC services, which hang off the same endpoint
    // builders MapGrpcService returns - so the policy applied there covers both surfaces, and
    // asserting over HTTP asserts about the gRPC endpoints too - plus the FHIR operation, which
    // is gated separately (it is an MVC controller, not a gRPC service) but must answer alike.
    public static TheoryData<HttpMethod, string> ApiEndpoints =>
        new()
        {
            { HttpMethod.Get, "/v1/namespaces" },
            { HttpMethod.Get, "/v1/namespaces/existingNamespace" },
            { HttpMethod.Get, "/v1/namespaces/existingNamespace/children" },
            { HttpMethod.Get, "/v1/namespaces/existingNamespace/pseudonyms" },
            { HttpMethod.Post, "/v1/namespaces/existingNamespace/pseudonyms" },
            { HttpMethod.Post, "/v1/namespaces/existingNamespace/pseudonyms:resolve" },
            { HttpMethod.Post, "/v1/fhir/$create-pseudonym" },
            { HttpMethod.Post, "/v1/fhir/$resolve-pseudonym" },
        };

    [Theory]
    [MemberData(nameof(ApiEndpoints))]
    public async Task ApiEndpoint_WithoutToken_ShouldBeRefusedBeforeReachingTheService(
        HttpMethod method,
        string endpoint
    )
    {
        // Redirects must not be followed, or a 302 into the login flow would be indistinguishable
        // from a 401 by the time the assertion sees it - and telling those two apart is the point.
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        using var request = new HttpRequestMessage(method, endpoint);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { originalValue = "test" });
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The challenge has to come from the bearer handler, not the cookie one. A cookie
        // challenge answers with a 302 to the login page, which a gRPC client reports as
        // "Bad gRPC response. HTTP status code: 302" rather than as Unauthenticated.
        response.Headers.WwwAuthenticate.Should().Contain(header => header.Scheme == "Bearer");
        response.Headers.Location.Should().BeNull();
    }

    /// <summary>
    /// The published contract has to say the API takes a bearer token, or Swagger UI renders no
    /// Authorize button and its "Try it out" can only ever answer 401.
    /// </summary>
    [Fact]
    public async Task SwaggerDocument_WithAuthorizationEnabled_ShouldDescribeTheBearerScheme()
    {
        var client = factory.CreateClient();

        var document = await client.GetStringAsync(
            "/swagger/v1/swagger.json",
            TestContext.Current.CancellationToken
        );

        using var parsed = JsonDocument.Parse(document);

        var scheme = parsed
            .RootElement.GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");
        scheme.GetProperty("type").GetString().Should().Be("http");
        scheme.GetProperty("scheme").GetString().Should().Be("bearer");

        parsed.RootElement.TryGetProperty("security", out _).Should().BeTrue();
    }

    /// <summary>
    /// Reflection describes the API, so it answers to the same token the API does - a caller
    /// without one can't even list the services. AccessTokenApiTests has the other half: with a
    /// token, the same call succeeds.
    /// </summary>
    [Fact]
    public async Task GrpcReflection_WithoutToken_ShouldBeRefused()
    {
        var act = () =>
            GrpcReflection.ListServicesAsync(factory, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.Unauthenticated);
    }

    [Theory]
    [InlineData("/readyz")]
    [InlineData("/livez")]
    [InlineData("/healthz")]
    public async Task HealthEndpoints_WithAuthorizationEnabled_ShouldStayAnonymous(string endpoint)
    {
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        var response = await client.GetAsync(endpoint, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }
}

public class ApiAuthorizationDisabledTests(
    IntegrationTestFactory<Program, PseudonymContext> factory
) : IClassFixture<IntegrationTestFactory<Program, PseudonymContext>>
{
    /// <summary>
    /// The gate is conditional on Authorization:IsEnabled, and has to stay that way: the policy is
    /// only registered inside that same condition, so applying it unconditionally would throw on
    /// every API request of an unauthenticated deployment rather than refuse it.
    /// </summary>
    [Fact]
    public async Task ApiEndpoint_WithAuthorizationDisabled_ShouldRemainOpen()
    {
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        var response = await client.GetAsync(
            "/v1/namespaces",
            TestContext.Current.CancellationToken
        );

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The same for the FHIR operation, gated through MapControllers rather than with the gRPC
    /// services. A malformed body is fine here - the point is that the request reaches the
    /// controller and is answered on its merits rather than refused for carrying no token.
    /// </summary>
    [Fact]
    public async Task FhirOperation_WithAuthorizationDisabled_ShouldRemainOpen()
    {
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );

        var response = await client.PostAsync(
            "/v1/fhir/$create-pseudonym",
            JsonContent.Create(new { resourceType = "Parameters" }),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Served outside Development too - the test host runs as "Test" - so that clients like
    /// grpcurl need no copy of the .proto files.
    /// </summary>
    [Fact]
    public async Task GrpcReflection_ShouldListTheApiServices()
    {
        var services = await GrpcReflection.ListServicesAsync(
            factory,
            TestContext.Current.CancellationToken
        );

        services.Should().Contain(["vfps.api.v1.PseudonymService", "vfps.api.v1.NamespaceService"]);
    }

    /// <summary>
    /// And the document must not advertise a credential the deployment ignores.
    /// </summary>
    [Fact]
    public async Task SwaggerDocument_WithAuthorizationDisabled_ShouldDescribeNoSecurityScheme()
    {
        var client = factory.CreateClient();

        var document = await client.GetStringAsync(
            "/swagger/v1/swagger.json",
            TestContext.Current.CancellationToken
        );

        using var parsed = JsonDocument.Parse(document);

        parsed
            .RootElement.GetProperty("components")
            .TryGetProperty("securitySchemes", out _)
            .Should()
            .BeFalse();
        parsed.RootElement.TryGetProperty("security", out _).Should().BeFalse();
    }
}

/// <summary>
/// Lists the services a test host offers over gRPC server reflection, the way grpcurl's "list"
/// does: v1alpha, which is what the grpcurl in the grpc-utils image speaks.
/// </summary>
[ExcludeFromCodeCoverage]
internal static class GrpcReflection
{
    public static async Task<IReadOnlyList<string>> ListServicesAsync(
        WebApplicationFactory<Program> factory,
        CancellationToken cancellationToken,
        string? bearerToken = null
    )
    {
        using var channel = GrpcChannel.ForAddress(
            factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() }
        );
        var client = new ServerReflection.ServerReflectionClient(channel);

        var headers = new Metadata();
        if (bearerToken is not null)
        {
            headers.Add("authorization", $"Bearer {bearerToken}");
        }

        using var call = client.ServerReflectionInfo(headers, cancellationToken: cancellationToken);
        await call.RequestStream.WriteAsync(
            new ServerReflectionRequest { ListServices = string.Empty },
            cancellationToken
        );
        await call.RequestStream.CompleteAsync();

        var services = new List<string>();
        await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            services.AddRange(response.ListServicesResponse.Service.Select(s => s.Name));
        }

        return services;
    }
}
