using System.Diagnostics;
using System.Security.Claims;
using Amazon.S3;
using BlazorBlueprint.Components;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi.Models;
using Prometheus;
using StackExchange.Redis;
using Vfps;
using Vfps.AppServices;
using Vfps.Authorization;
using Vfps.Components;
using Vfps.Config;
using Vfps.CsvProcessing;
using Vfps.Data;
using Vfps.Fhir;
using Vfps.PseudonymGenerators;
using Vfps.Services;
using Vfps.Tracing;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddBlazorBlueprintComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

builder.Services.AddGrpc().AddJsonTranscoding();
builder.Services.AddGrpcSwagger();
builder.Services.AddGrpcHealthChecks();
builder.Services.AddGrpcReflection();
builder.Services.AddHealthChecks().AddDbContextCheck<PseudonymContext>().ForwardToPrometheus();

builder.Services.AddMetricServer(options =>
    options.Port = builder.Configuration.GetValue<ushort>("MetricsPort", 8082)
);

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc(
        "v1",
        new OpenApiInfo
        {
            Title = "VFPS FHIR and gRPC JSON-transcoded API",
            Version = "v1",
            Description = "A very fast and resource-efficient pseudonym service.",
            License = new OpenApiLicense
            {
                Name = "Apache-2.0",
#pragma warning disable S1075 // URIs should not be hardcoded
                Url = new Uri("https://www.apache.org/licenses/LICENSE-2.0")
#pragma warning restore S1075 // URIs should not be hardcoded
            },
        }
    );

    var filePath = Path.Combine(AppContext.BaseDirectory, "Vfps.xml");
    c.IncludeXmlComments(filePath);
    c.IncludeGrpcXmlComments(filePath, includeControllerXmlComments: true);
    c.UseInlineDefinitionsForEnums();

    // Vfps.Protos and Hl7.Fhir.Model both define a type named "Meta", which otherwise
    // collide under Swashbuckle's default (short-name-only) schemaId generation.
    c.CustomSchemaIds(type => type.FullName?.Replace('+', '.'));
});

builder.Services.AddDbContext<PseudonymContext>(
    (isp, options) =>
    {
        var config = isp.GetService<IConfiguration>()!;

        var backingStore =
            config.GetValue<string>("Pseudonymization:BackingStore")
            ?? throw new InvalidOperationException(
                "Failed to get backing store config. Make sure Pseudonymization:BackingStore is set"
            );

        var connString =
            config.GetConnectionString(backingStore)
            ?? throw new InvalidOperationException(
                $"Failed to get connection string for '{backingStore}' backing store"
            );

        switch (backingStore.ToLowerInvariant())
        {
            case "postgresql":
                options.UseNpgsql(connString);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported backing store specified: {backingStore}"
                );
        }
    }
);

builder.Services.AddSingleton<PseudonymizationMethodsLookup>();

var cacheConfig = new CacheConfig();
builder.Configuration.GetSection("Pseudonymization:Caching").Bind(cacheConfig);

var isNamespaceCachingEnabled = builder.Configuration.GetValue(
    "Pseudonymization:Caching:Namespaces:IsEnabled",
    false
);
if (isNamespaceCachingEnabled)
{
    builder.Services.AddSingleton<IMemoryCache>(_ => new MemoryCache(
        new MemoryCacheOptions { TrackStatistics = true, SizeLimit = cacheConfig.SizeLimit }
    ));
    builder.Services.AddSingleton(_ => cacheConfig);
    builder.Services.AddScoped<INamespaceRepository, CachingNamespaceRepository>();
}
else
{
    builder.Services.AddScoped<INamespaceRepository, NamespaceRepository>();
}

var isPseudonymCachingEnabled = builder.Configuration.GetValue(
    "Pseudonymization:Caching:Pseudonyms:IsEnabled",
    false
);
if (isPseudonymCachingEnabled)
{
    builder.Services.TryAddSingleton<IMemoryCache>(_ => new MemoryCache(
        new MemoryCacheOptions { TrackStatistics = true, SizeLimit = cacheConfig.SizeLimit }
    ));
    builder.Services.TryAddSingleton(_ => cacheConfig);
    builder.Services.AddScoped<IPseudonymRepository, CachingPseudonymRepository>();
}
else
{
    builder.Services.AddScoped<IPseudonymRepository, PseudonymRepository>();
}

// add a service to regularly query the cache statistics
if (isNamespaceCachingEnabled || isPseudonymCachingEnabled)
{
    builder.Services.AddHostedService<MemoryCacheMetricsBackgroundService>();
}

builder.Services.AddScoped<INamespaceAppService, NamespaceAppService>();
builder.Services.AddScoped<IPseudonymAppService, PseudonymAppService>();

builder.Services.AddHostedService<InitNamespacesBackgroundService>();

// Authorization: fully declarative, off by default (see Config/AuthorizationConfig.cs) - matches
// the existing Tracing/Pseudonymization:Caching:*:IsEnabled idiom in this codebase.
builder.Services.Configure<AuthorizationConfig>(builder.Configuration.GetSection("Authorization"));
builder.Services.AddSingleton<INamespacePermissionChecker, NamespacePermissionChecker>();

var authConfig = new AuthorizationConfig();
builder.Configuration.GetSection("Authorization").Bind(authConfig);

if (authConfig.IsEnabled)
{
    builder
        .Services.AddAuthentication(options =>
        {
            // Route browser requests (Blazor UI) to the cookie scheme and everything else
            // (gRPC/REST callers presenting a bearer token) to JWT bearer.
            options.DefaultScheme = "smart";
            options.DefaultChallengeScheme = "smart";
        })
        .AddPolicyScheme(
            "smart",
            "Cookie or Bearer",
            options =>
            {
                options.ForwardDefaultSelector = context =>
                    context
                        .Request.Headers.Authorization.ToString()
                        .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? JwtBearerDefaults.AuthenticationScheme
                        : CookieAuthenticationDefaults.AuthenticationScheme;
            }
        )
        .AddCookie()
        .AddOpenIdConnect(options =>
        {
            options.Authority = authConfig.Authority;
            options.ClientId = authConfig.ClientId;
            options.ClientSecret = authConfig.ClientSecret;
            options.ResponseType = "code";
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.Scope.Add("roles");
            // Only relax to plain-HTTP metadata/issuer for local development (e.g. a local
            // Keycloak without TLS) - production authorities must always be HTTPS.
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            // Without this, the .NET JWT stack silently rewrites well-known-sounding claim
            // types (e.g. a "roles" claim becomes the long ClaimTypes.Role URI), which would
            // otherwise silently break AuthorizationConfig.RoleClaimType matching exactly what
            // the IdP was configured to emit.
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "preferred_username";
            options.TokenValidationParameters.RoleClaimType = authConfig.RoleClaimType;
        })
        .AddJwtBearer(options =>
        {
            options.Authority = authConfig.Authority;
            options.Audience = authConfig.Audience;
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "preferred_username";
            options.TokenValidationParameters.RoleClaimType = authConfig.RoleClaimType;
        });

    // Blazor Server keeps one process per replica but many circuits/cookies across replicas -
    // the Data Protection key ring that encrypts auth cookies/antiforgery tokens needs to be
    // shared so a request landing on a different replica than the one that issued the cookie
    // can still decrypt it (relevant even with ingress session affinity, e.g. right after a
    // scaling event). This is the actual reason Redis is needed here - sticky sessions
    // themselves are an ingress-level concern, documented in the README, not implemented here.
    var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
    if (!string.IsNullOrEmpty(redisConnectionString))
    {
        var redis = ConnectionMultiplexer.Connect(redisConnectionString);
        builder
            .Services.AddDataProtection()
            .PersistKeysToStackExchangeRedis(redis, "vfps-DataProtection-Keys");
    }
}

// CSV pseudonymization jobs: off by default, matching this codebase's optional-feature idiom.
// Input/output files live in S3-compatible object storage - see Config/S3Config.cs.
builder.Services.Configure<S3Config>(builder.Configuration.GetSection("S3"));
var s3Config = new S3Config();
builder.Configuration.GetSection("S3").Bind(s3Config);

if (s3Config.IsEnabled)
{
    builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
        s3Config.AccessKey,
        s3Config.SecretKey,
        new AmazonS3Config
        {
            ServiceURL = s3Config.ServiceUrl,
            ForcePathStyle = s3Config.ForcePathStyle,
            // AWSSDK doesn't infer the scheme from ServiceURL for presigned URLs - without this,
            // a plain-HTTP endpoint (e.g. local MinIO) still gets signed as "https://", which the
            // browser then fails to load against a server not actually listening for TLS there.
            UseHttp = s3Config.ServiceUrl.StartsWith("http://", StringComparison.Ordinal),
        }
    ));

    builder.Services.AddScoped<IPseudonymizationJobRepository, PseudonymizationJobRepository>();
    builder.Services.AddScoped<IPseudonymizationJobAppService, PseudonymizationJobAppService>();
    builder.Services.AddScoped<ICsvPseudonymizationJobRunner, CsvPseudonymizationJobRunner>();

    var postgresConnectionString =
        builder.Configuration.GetConnectionString("PostgreSQL")
        ?? throw new InvalidOperationException(
            "S3:IsEnabled requires ConnectionStrings:PostgreSQL to also be set - Hangfire reuses "
                + "the same database, no separate storage is provisioned for it."
        );

    // Hangfire's Postgres storage handles distributed locking itself, so every horizontally
    // scaled replica can safely run AddHangfireServer() and pick up jobs with no extra
    // coordination work needed - consistent with "no new service" for this feature.
    builder.Services.AddHangfire(config =>
        config
            .UsePostgreSqlStorage(options =>
                options.UseNpgsqlConnection(postgresConnectionString)
            )
            // CsvPseudonymizationJobRunner already handles its own failures (marks the job
            // Failed with a sanitized message, logs the full exception server-side) - Hangfire's
            // default of 10 automatic retries would silently re-run the whole job (tying up a
            // worker slot each time) even though most failures here are deterministic (a bad
            // column name, malformed input) and will just fail again identically.
            .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    );
    builder.Services.AddHangfireServer();
}

builder.Services.AddControllers(options =>
{
    options.InputFormatters.Insert(0, new FhirInputFormatter());
    options.OutputFormatters.Insert(0, new FhirOutputFormatter());
});

// Tracing
var isTracingEnabled = builder.Configuration.GetValue("Tracing:IsEnabled", false);
if (isTracingEnabled)
{
    builder.AddTracing();
}

var app = builder.Build();

if (!authConfig.IsEnabled)
{
    app.Logger.LogWarning(
        "Authorization is disabled (Authorization:IsEnabled=false). The API and admin UI are "
            + "reachable without authentication and every namespace is fully accessible. This is "
            + "not recommended for deployments handling real data."
    );
}

// Configure the HTTP request pipeline.
app.MapGrpcService<PseudonymService>();
app.MapGrpcService<NamespaceService>();
app.MapGrpcHealthChecksService();
app.MapGet("/", () => Results.Redirect("/swagger"));
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "VFPS API v1"));
app.UseHttpMetrics();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

if (authConfig.IsEnabled)
{
    // Standard ASP.NET Core Blazor Web App + OIDC login/logout pattern: Blazor components
    // can't issue the redirect-based OIDC challenge themselves, so a plain link to these
    // endpoints (not Blazor-routed navigation) triggers the actual sign-in/sign-out flow.
    app.MapGet(
            "/authentication/login",
            (string? returnUrl) =>
                Results.Challenge(
                    new AuthenticationProperties { RedirectUri = returnUrl ?? "/ui" },
                    [OpenIdConnectDefaults.AuthenticationScheme]
                )
        )
        .AllowAnonymous();

    app.MapPost(
        "/authentication/logout",
        async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/ui" },
                [OpenIdConnectDefaults.AuthenticationScheme]
            );
        }
    );
}

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapHealthChecks("/healthz");
app.MapHealthChecks(
    "/readyz",
    new HealthCheckOptions
    {
        // there's currently no readiness probes depending on external state,
        // but in case we ever add one, this prepares the code for it.
        // see https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-7.0#separate-readiness-and-liveness-probes
        Predicate = healthCheck => healthCheck.Tags.Contains("ready"),
    }
);

app.MapHealthChecks("/livez", new HealthCheckOptions { Predicate = _ => false });

app.UseGrpcMetrics();

if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

app.MapControllers();

var shouldRunDatabaseMigrations =
    app.Environment.IsDevelopment()
    || app.Configuration.GetValue<bool>("ForceRunDatabaseMigrations");
if (shouldRunDatabaseMigrations)
{
    // only ran in a development setup or when forced.
    // See https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying?tabs=dotnet-core-cli>
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PseudonymContext>();
    db.Database.Migrate();
}

app.Run();

public partial class Program
{
    internal static readonly ActivitySource ActivitySource = new("Vfps");

    protected Program() { }
}
