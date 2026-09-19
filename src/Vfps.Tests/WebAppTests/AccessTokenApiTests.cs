using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vfps.Data.Models;
using Task = System.Threading.Tasks.Task;

namespace Vfps.Tests.WebAppTests;

/// <summary>
/// The app as a deployment that has switched vfps-issued access tokens on. The OIDC authority is
/// deliberately unreachable: a vfps token must authenticate without it, which is a good part of
/// why a deployment issues one.
/// </summary>
[ExcludeFromCodeCoverage]
public class AccessTokensEnabledTestFactory : IntegrationTestFactory<Program, PseudonymContext>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Authorization:IsEnabled", "true");
        builder.UseSetting("Authorization:Authority", "https://localhost:1/realms/vfps");
        builder.UseSetting("Authorization:Audience", "vfps-api");
        builder.UseSetting("Authorization:ClientId", "vfps-web");
        builder.UseSetting("Authorization:AccessTokens:IsEnabled", "true");
        builder.UseSetting("Authorization:AdminRoles:0", "vfps-admin");

        base.ConfigureWebHost(builder);
    }
}

public class AccessTokenApiTests(AccessTokensEnabledTestFactory factory)
    : IClassFixture<AccessTokensEnabledTestFactory>
{
    private const string Namespace = "existingNamespace";

    private static HttpClient CreateClient(AccessTokensEnabledTestFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Writes a token (and, for a service-account one, its account and grant) straight into the
    /// app's own database, then drops the caches so the running host sees it - the same thing the
    /// app services do after a write, and what makes this exercise the real request pipeline
    /// rather than a stubbed one.
    /// </summary>
    private async Task<string> SeedAsync(AccessToken token, NamespaceAccessGrant? grant = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PseudonymContext>();

        // This host starts on an empty database, unlike the unit tests' seeded one.
        if (!await context.Namespaces.AnyAsync(n => n.Name == Namespace))
        {
            context.Namespaces.Add(
                new Data.Models.Namespace
                {
                    Name = Namespace,
                    Description = "seeded by AccessTokenApiTests",
                    PseudonymLength = 32,
                    PseudonymGenerationMethod =
                        PseudonymGenerationMethod.SecureRandomBase64UrlEncoded,
                    PseudonymPrefix = string.Empty,
                    PseudonymSuffix = string.Empty,
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                }
            );
            await context.SaveChangesAsync();
        }

        if (token.ServiceAccountName is not null)
        {
            var accountExists = await context.ServiceAccounts.AnyAsync(a =>
                a.Name == token.ServiceAccountName
            );
            if (!accountExists)
            {
                context.ServiceAccounts.Add(
                    new ServiceAccount
                    {
                        Name = token.ServiceAccountName,
                        CreatedBy = "test",
                        CreatedAt = DateTimeOffset.UtcNow,
                        LastUpdatedAt = DateTimeOffset.UtcNow,
                    }
                );
                await context.SaveChangesAsync();
            }
        }

        context.AccessTokens.Add(token);

        if (grant is not null)
        {
            context.NamespaceAccessGrants.Add(grant);
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        factory.Services.GetRequiredService<IAccessTokenCache>().Invalidate();
        factory.Services.GetRequiredService<INamespaceAccessGrantCache>().Invalidate();

        return token.TokenId;
    }

    [Fact]
    public async Task ServiceAccountToken_WithAGrant_ShouldReachTheApi()
    {
        var (token, secret) = Tokens.ForServiceAccount("etl-pipeline");
        await SeedAsync(token, Grants.ForServiceAccount(Namespace, "etl-pipeline", read: true));

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var response = await client.GetAsync(
            $"/v1/namespaces/{Namespace}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ServiceAccountToken_WithoutAGrant_ShouldBeAuthenticatedButRefused()
    {
        // Authentication succeeds - the credential is real - and the app-service layer then
        // refuses it for holding no grant on the namespace. That distinction is the whole point
        // of a service account: the token says who you are, the grants say what you may do.
        var (token, secret) = Tokens.ForServiceAccount("ungranted-account");
        await SeedAsync(token);

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var response = await client.GetAsync(
            $"/v1/namespaces/{Namespace}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PersonalToken_ShouldResolveAgainstItsOwnersRoleGrants()
    {
        var (token, secret) = Tokens.Personal(subject: "subject-1", roles: ["namespace-reader"]);
        await SeedAsync(token, Grants.ForRole(Namespace, "namespace-reader", read: true));

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var response = await client.GetAsync(
            $"/v1/namespaces/{Namespace}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PersonalToken_ShouldResolveAgainstItsOwnersEmailGrants()
    {
        var (token, secret) = Tokens.Personal(subject: "subject-2", email: "user@example.org");
        await SeedAsync(token, Grants.ForEmail(Namespace, "user@example.org", read: true));

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var response = await client.GetAsync(
            $"/v1/namespaces/{Namespace}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PersonalToken_ShouldCreateAPseudonymWhereItsOwnerMay()
    {
        // The write path, not just a read: this is the call a machine client actually makes, and
        // the one whose permission check is resolved per request.
        var (token, secret) = Tokens.Personal(subject: "subject-3", roles: ["namespace-writer"]);
        await SeedAsync(token, Grants.ForRole(Namespace, "namespace-writer", write: true));

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var response = await client.PostAsync(
            $"/v1/namespaces/{Namespace}/pseudonyms",
            JsonContent.Create(new { originalValue = "a value pseudonymized by a token" }),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExpiredToken_ShouldBeRefusedAsUnauthenticated()
    {
        var (token, secret) = Tokens.Personal(
            subject: "subject-4",
            roles: ["namespace-reader"],
            expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)
        );
        await SeedAsync(token);

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var response = await client.GetAsync(
            $"/v1/namespaces/{Namespace}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().Contain(header => header.Scheme == "Bearer");
        // Never a redirect into the login flow: a gRPC client reports that as a transport error
        // rather than as Unauthenticated.
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task RevokedToken_ShouldBeRefusedAsUnauthenticated()
    {
        var (token, secret) = Tokens.Personal(
            subject: "subject-5",
            revokedAt: DateTimeOffset.UtcNow.AddSeconds(-1)
        );
        await SeedAsync(token);

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var response = await client.GetAsync(
            $"/v1/namespaces/{Namespace}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownToken_ShouldBeRefusedAsUnauthenticated()
    {
        var unknown = AccessTokenSecret.Generate(AccessTokenType.Personal);

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", unknown.Presented);

        var response = await client.GetAsync(
            $"/v1/namespaces/{Namespace}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AServiceAccountToken_ShouldNotBeAbleToCreateANamespace()
    {
        // Namespace create/delete is admin-only, and a service account is never an admin - so
        // even a token granted write access everywhere cannot add or remove one.
        var (token, secret) = Tokens.ForServiceAccount("powerful-account");
        await SeedAsync(
            token,
            Grants.ForServiceAccount(null, "powerful-account", read: true, write: true)
        );

        var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);

        var response = await client.PostAsync(
            "/v1/namespaces",
            JsonContent.Create(new { name = "namespace-from-a-service-account" }),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HealthEndpoints_ShouldStayAnonymous()
    {
        var client = CreateClient(factory);

        var response = await client.GetAsync("/readyz", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
