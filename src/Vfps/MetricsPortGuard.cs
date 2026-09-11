namespace Vfps;

/// <summary>
/// Decides whether a request may be served on the Kestrel listener that accepted it: /metrics
/// answers only on the dedicated metrics listener (<c>MetricsPort</c>, see Program.cs), and that
/// listener answers nothing else.
///
/// ASP.NET Core routing is indifferent to which listener accepted a connection, so without this
/// the admin UI, the Hangfire dashboard, the REST/FHIR API and the gRPC services are all served
/// on the metrics port too - and a deployment that scopes that port more loosely than the API
/// ports is then publishing the whole pseudonymization API through it. The bundled chart does
/// exactly that scoping: its NetworkPolicy gives http/grpc and http-metrics separate ingress
/// rules, and the metrics one is the permissive default.
///
/// A pure function rather than logic inlined into the middleware so that it can be unit-tested -
/// the middleware itself can't be, since MetricsPort is 0 under WebApplicationFactory (see
/// appsettings.Test.json).
/// </summary>
internal static class MetricsPortGuard
{
    /// <summary>Path the Prometheus scraping endpoint is mapped on.</summary>
    internal const string MetricsPath = "/metrics";

    public static bool ShouldReject(PathString path, int localPort, ushort metricsPort)
    {
        // PathString comparison is OrdinalIgnoreCase, which is how endpoint routing matches too -
        // a case-sensitive one would wave "/METRICS" past on a public port and still hit the
        // exporter.
        var isMetricsPath = path == MetricsPath;

        // MetricsPort=0 asks Kestrel for an ephemeral, OS-assigned port, so no configured value
        // can identify the metrics listener. It has to mean "there is no dedicated metrics
        // listener" rather than "the listener on port 0": ASP.NET Core's TestServer reports a
        // local port of 0 for every request, which would otherwise match and 404 the whole
        // application out from under WebApplicationFactory.
        var isMetricsListener = metricsPort != 0 && localPort == metricsPort;

        // The two have to agree. Either without the other is a request on the wrong listener:
        // /metrics somewhere public, or anything else on the port reserved for the scraper.
        return isMetricsPath != isMetricsListener;
    }
}
