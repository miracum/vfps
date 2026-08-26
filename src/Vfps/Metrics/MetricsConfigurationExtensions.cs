using System.Reflection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Vfps.Metrics;

public static class MetricsConfigurationExtensions
{
    public static WebApplicationBuilder AddMetrics(this WebApplicationBuilder builder)
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
                metricsBuilder
                    .AddMeter(Program.Meter.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsqlInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddPrometheusExporter()
            );

        return builder;
    }
}
