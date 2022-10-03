using Microsoft.OpenApi.Models;
using Vfps.Data;
using Vfps.Services;
using Microsoft.EntityFrameworkCore;
using Vfps.PseudonymGenerators;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Prometheus;
using Vfps.Config;
using Microsoft.Extensions.Caching.Memory;
using Vfps.Fhir;
using Vfps.Tracing;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc().AddJsonTranscoding();
builder.Services.AddGrpcSwagger();
builder.Services.AddGrpcHealthChecks();
builder.Services.AddGrpcReflection();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<PseudonymContext>()
    .ForwardToPrometheus();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1",
        new OpenApiInfo
        {
            Title = "VFPS FHIR and gRPC JSON-transcoded API",
            Version = "v1",
            Description = "A very fast and resource-efficient pseudonym service.",
            License = new OpenApiLicense
            {
                Name = "Apache-2.0",
                Url = new Uri("https://www.apache.org/licenses/LICENSE-2.0")
            },
        });

    var filePath = Path.Combine(AppContext.BaseDirectory, "Vfps.xml");
    c.IncludeXmlComments(filePath);
    c.IncludeGrpcXmlComments(filePath, includeControllerXmlComments: true);
    c.UseInlineDefinitionsForEnums();
});

builder.Services.AddDbContext<PseudonymContext>((isp, options) =>
{
    var config = isp.GetService<IConfiguration>()!;

    var backingStore = config.GetValue<string>("Pseudonymization:BackingStore")
        ?? throw new InvalidOperationException("Failed to get backing store config. Make sure Pseudonymization:BackingStore is set");

    var connString = config.GetConnectionString(backingStore)
        ?? throw new InvalidOperationException($"Failed to get connection string for '{backingStore}' backing store");

    switch (backingStore.ToLowerInvariant())
    {
        case "postgresql":
            options.UseNpgsql(connString);
            break;
        default:
            throw new InvalidOperationException($"Unsupported backing store specified: {backingStore}");
    }
});

builder.Services.AddSingleton<PseudonymizationMethodsLookup>();

var namespaceCacheConfig = new CacheConfig();
builder.Configuration.GetSection("Pseudonymization:Caching:Namespaces").Bind(namespaceCacheConfig);
if (namespaceCacheConfig.IsEnabled)
{
    builder.Services.AddSingleton<IMemoryCache>(_ =>
        new MemoryCache(
            new MemoryCacheOptions
            {
                SizeLimit = namespaceCacheConfig.SizeLimit
            }));
    builder.Services.AddSingleton(_ => namespaceCacheConfig);
    builder.Services.AddScoped<INamespaceRepository, CachingNamespaceRepository>();
}
else
{
    builder.Services.AddScoped<INamespaceRepository, NamespaceRepository>();
}

builder.Services.AddScoped<IPseudonymRepository, PseudonymRepository>();

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

// Configure the HTTP request pipeline.
app.MapGrpcService<PseudonymService>();
app.MapGrpcService<NamespaceService>();
app.MapGrpcHealthChecksService();
app.MapGet("/", () => Results.Redirect("/swagger"));
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "VFPS API v1"));
app.UseHttpMetrics();

app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    // there's currently now readiness probes depending on external state,
    // but in case we ever add one, this prepares the code for it.
    // see https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-7.0#separate-readiness-and-liveness-probes
    Predicate = healthCheck => healthCheck.Tags.Contains("ready")
});

app.MapHealthChecks("/livez", new HealthCheckOptions
{
    Predicate = _ => false
});

app.MapMetrics();

if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

app.MapControllers();

var shouldRunDatabaseMigrations = app.Environment.IsDevelopment() ||
    app.Configuration.GetValue<bool>("ForceRunDatabaseMigrations");
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
}
