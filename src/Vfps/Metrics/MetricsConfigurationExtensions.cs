using System.Reflection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Vfps.Metrics;

public static class MetricsConfigurationExtensions
{
    public static WebApplicationBuilder AddMetrics(
        this WebApplicationBuilder builder,
        ushort metricsPort
    )
    {
        var assembly = Assembly.GetExecutingAssembly().GetName();
        var assemblyVersion = assembly.Version?.ToString() ?? "unknown";
        var serviceName =
            builder.Configuration.GetValue("Tracing:ServiceName", assembly.Name) ?? "vfps";

        builder
            .Services.AddOpenTelemetry()
            .ConfigureResource(r =>
                r.AddService(
                    serviceName: serviceName,
                    serviceVersion: assemblyVersion,
                    serviceInstanceId: Environment.MachineName
                )
            )
            .WithMetrics(metricsBuilder =>
            {
                metricsBuilder
                    .AddMeter(Program.Meter.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsqlInstrumentation()
                    .AddRuntimeInstrumentation();

                // 0 (used by appsettings.Test.json) means "not exported at all" rather than "pick
                // a port": HttpListener, unlike Kestrel, has no ephemeral-port trick - it rejects
                // port 0 outright - and a real deployment never wants metrics silently
                // unreachable, so there's no other value 0 could sensibly mean here.
                if (metricsPort != 0)
                {
                    // A standalone HttpListener on its own port, rather than
                    // AddPrometheusExporter()/MapPrometheusScrapingEndpoint(): that alternative
                    // maps /metrics onto the app's own Kestrel pipeline, which means adding a
                    // second Kestrel listener for it - and Kestrel drops ASPNETCORE_URLS/
                    // ASPNETCORE_HTTP_PORTS support entirely as soon as any endpoint is configured
                    // in code or via the Kestrel:Endpoints config section (which this app's Http/
                    // HttpGrpc endpoints already use). This exporter instead runs its own
                    // independent listener that never touches Kestrel at all, and it inherently
                    // serves nothing but /metrics - there's no shared routing table for the admin
                    // UI, the Hangfire dashboard or the gRPC services to be reachable through, so
                    // unlike a second Kestrel listener it needs no separate guard middleware.
                    metricsBuilder.AddPrometheusHttpListener(options =>
                    {
                        // Host/Port build a System.Uri internally, so neither HttpListener's own
                        // "+"/"*" wildcard syntax nor a literal "0.0.0.0" work here - Uri rejects
                        // the former outright, and .NET's cross-platform HttpListener refuses to
                        // bind the latter on Linux ("the request is not supported"). Left at the
                        // "localhost" default (which Uri accepts, giving the constructor a prefix
                        // it can register without throwing) and replaced below with the actual
                        // all-interfaces prefix, needed since the scraper is never the same host
                        // as the container.
                        options.Port = metricsPort;
                        options.ConfigureHttpListener = (_, listener) =>
                        {
                            listener.Prefixes.Clear();
                            listener.Prefixes.Add($"http://+:{metricsPort}/metrics/");
                        };
                    });
                }
            });

        return builder;
    }
}
