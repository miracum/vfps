using System.Reflection;
using OpenTelemetry.Trace;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Npgsql;

namespace Vfps.Tracing
{
    public static class TracingConfigurationExtensions
    {
        public static WebApplicationBuilder AddTracing(this WebApplicationBuilder builder)
        {
            var assembly = Assembly.GetExecutingAssembly().GetName();
            var assemblyVersion = assembly.Version?.ToString() ?? "unknown";
            var tracingExporter =
                builder.Configuration.GetValue<string>("Tracing:Exporter")?.ToLowerInvariant()
                ?? "jaeger";
            var serviceName =
                builder.Configuration.GetValue("Tracing:ServiceName", assembly.Name) ?? "vfps";

            var rootSamplerType = builder.Configuration.GetValue(
                "Tracing:RootSampler",
                "AlwaysOnSampler"
            );
            var samplingRatio = builder.Configuration.GetValue("Tracing:SamplingProbability", 0.1d);

            Sampler rootSampler = rootSamplerType switch
            {
                nameof(AlwaysOnSampler) => new AlwaysOnSampler(),
                nameof(AlwaysOffSampler) => new AlwaysOffSampler(),
                nameof(TraceIdRatioBasedSampler) => new TraceIdRatioBasedSampler(samplingRatio),
                _ => throw new ArgumentException($"Unsupported sampler type '{rootSamplerType}'"),
            };

            builder.Services.AddOpenTelemetryTracing(options =>
            {
                options
                    .ConfigureResource(
                        r =>
                            r.AddService(
                                serviceName: serviceName,
                                serviceVersion: assemblyVersion,
                                serviceInstanceId: Environment.MachineName
                            )
                    )
                    .SetSampler(new ParentBasedSampler(rootSampler))
                    .AddNpgsql()
                    .AddSource(Program.ActivitySource.Name)
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.Filter = (r) =>
                        {
                            var ignoredPaths = new[] { "/healthz", "/readyz", "/livez" };

                            var path = r.Request.Path.Value!;
                            return !ignoredPaths.Any(path.Contains);
                        };
                    });

                switch (tracingExporter)
                {
                    case "jaeger":
                        options.AddJaegerExporter();
                        builder.Services.Configure<JaegerExporterOptions>(
                            builder.Configuration.GetSection("Tracing:Jaeger")
                        );
                        break;

                    case "otlp":
                        var endpoint =
                            builder.Configuration.GetValue<string>("Tracing:Otlp:Endpoint") ?? "";
                        options.AddOtlpExporter(
                            otlpOptions => otlpOptions.Endpoint = new Uri(endpoint)
                        );
                        break;
                }
            });

            return builder;
        }
    }
}
