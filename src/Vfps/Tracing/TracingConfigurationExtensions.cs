using System.Reflection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Vfps.Tracing
{
    public static class TracingConfigurationExtensions
    {
        public static WebApplicationBuilder AddTracing(this WebApplicationBuilder builder)
        {
            var assembly = Assembly.GetExecutingAssembly().GetName();
            var assemblyVersion = assembly.Version?.ToString() ?? "unknown";
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

            builder
                .Services.AddOpenTelemetry()
                .ConfigureResource(r =>
                    r.AddService(
                        serviceName: serviceName,
                        serviceVersion: assemblyVersion,
                        serviceInstanceId: Environment.MachineName
                    )
                )
                .WithTracing(tracingBuilder =>
                {
                    tracingBuilder
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

                    var endpoint = builder.Configuration.GetValue<string>("Tracing:Otlp:Endpoint");
                    tracingBuilder.AddOtlpExporter(otlpOptions =>
                    {
                        // Leave the SDK's own default (http://localhost:4317) in place when
                        // unset, rather than failing outright - Uri's constructor throws on an
                        // empty string.
                        if (!string.IsNullOrEmpty(endpoint))
                        {
                            otlpOptions.Endpoint = new Uri(endpoint);
                        }
                    });
                });

            return builder;
        }
    }
}
