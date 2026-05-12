using System.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi.Models;
using Prometheus;
using Vfps;
using Vfps.Components;
using Vfps.Config;
using Vfps.Data;
using Vfps.Fhir;
using Vfps.PseudonymGenerators;
using Vfps.Services;
using Vfps.Tracing;
using Vfps.UI;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
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

builder.Services.AddHostedService<InitNamespacesBackgroundService>();

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

// Web UI
var uiConfig = new UiConfig();
builder.Configuration.GetSection("UI").Bind(uiConfig);
builder.Services.AddSingleton(uiConfig);
builder.Services.AddSingleton(uiConfig.CsvJobs);

if (uiConfig.IsEnabled)
{
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddSingleton<CsvJobService>();

    if (uiConfig.Oidc.IsEnabled)
    {
        var oidcConfig = uiConfig.Oidc;
        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Authority = oidcConfig.Authority;
                options.ClientId = oidcConfig.ClientId;
                if (!string.IsNullOrWhiteSpace(oidcConfig.ClientSecret))
                {
                    options.ClientSecret = oidcConfig.ClientSecret;
                }
                options.ResponseType = "code";
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.Scope.Add("profile");
                options.Scope.Add("email");
                foreach (var scope in oidcConfig.AdditionalScopes)
                {
                    options.Scope.Add(scope);
                }
                // Required for Keycloak: map the preferred_username claim
                options.TokenValidationParameters.NameClaimType = "preferred_username";
            });

        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
    }

    if (uiConfig.CsvJobs.S3.IsEnabled)
    {
        var s3Config = uiConfig.CsvJobs.S3;
        builder.Services.AddSingleton<Amazon.S3.IAmazonS3>(_ =>
        {
            var awsConfig = new Amazon.S3.AmazonS3Config
            {
                ForcePathStyle = s3Config.ForcePathStyle,
                AuthenticationRegion = s3Config.Region,
            };
            if (!string.IsNullOrWhiteSpace(s3Config.ServiceUrl))
            {
                awsConfig.ServiceURL = s3Config.ServiceUrl;
            }
            else
            {
                awsConfig.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(s3Config.Region);
            }
            var credentials = new Amazon.Runtime.BasicAWSCredentials(s3Config.AccessKey, s3Config.SecretKey);
            return new Amazon.S3.AmazonS3Client(credentials, awsConfig);
        });
        builder.Services.AddSingleton(s3Config);
        builder.Services.AddSingleton<ICsvFileStore, S3CsvFileStore>();
    }
    else
    {
        builder.Services.AddSingleton<ICsvFileStore, LocalCsvFileStore>();
    }

    builder.Services.AddHostedService<CsvPseudonymizationBackgroundService>();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<PseudonymService>();
app.MapGrpcService<NamespaceService>();
app.MapGrpcHealthChecksService();
app.MapGet("/", () => Results.Redirect("/swagger"));
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "VFPS API v1"));
app.UseHttpMetrics();

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

if (uiConfig.IsEnabled)
{
    app.UseStaticFiles();
    app.UseAntiforgery();

    if (uiConfig.Oidc.IsEnabled)
    {
        app.UseAuthentication();
        app.UseAuthorization();

        // Login endpoint: triggers the OIDC challenge and redirects back to the UI
        app.MapGet(
            "/ui/login",
            (string? returnUrl) =>
            {
                var redirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/ui" : returnUrl;
                return Results.Challenge(
                    new AuthenticationProperties { RedirectUri = redirectUri },
                    [OpenIdConnectDefaults.AuthenticationScheme]
                );
            }
        );

        // Logout endpoint: clears the local cookie and signs out from the OIDC provider
        app.MapGet(
            "/ui/logout",
            () =>
                Results.SignOut(
                    new AuthenticationProperties { RedirectUri = "/" },
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
                )
        );
    }

    var razorComponentsBuilder = app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    if (uiConfig.Oidc.IsEnabled)
    {
        razorComponentsBuilder.RequireAuthorization();
    }

    // Streaming download endpoint for pseudonymized CSV output files
    var csvDownload = app.MapGet(
        "/ui/csv/download/{jobId:guid}",
        async (Guid jobId, CsvJobService jobService, ICsvFileStore fileStore) =>
        {
            var job = jobService.GetJob(jobId);
            if (job is null || job.OutputKey is null)
                return Results.NotFound();

            var stream = await fileStore.OpenReadAsync(job.OutputKey, CancellationToken.None);
            if (stream is null)
                return Results.NotFound();

            var fileName = $"pseudonymized_{jobId:N}.csv";
            return Results.File(stream, "text/csv", fileName);
        }
    );

    if (uiConfig.Oidc.IsEnabled)
    {
        csvDownload.RequireAuthorization();
    }
}

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
