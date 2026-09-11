namespace Vfps.Tests;

/// <summary>
/// The metrics listener must not double as a second copy of the whole application - see
/// <see cref="MetricsPortGuard"/> for why that isn't just untidiness.
/// </summary>
public class MetricsPortGuardTests
{
    private const ushort MetricsPort = 8082;
    private const ushort PublicPort = 8080;

    /// <summary>
    /// One path per listener-reachable surface the app maps: the admin UI, the Hangfire
    /// dashboard, the OpenAPI UI, the transcoded REST API, the FHIR facade, a gRPC method and the
    /// health probes - which are deliberately not exempt, nothing probes the metrics port.
    /// </summary>
    public static TheoryData<string> NonMetricsPaths =>
        [
            "/",
            "/ui",
            "/ui/namespaces",
            "/hangfire",
            "/swagger/index.html",
            "/v1/namespaces",
            "/v1/fhir/$create-pseudonym",
            "/vfps.api.v1.PseudonymService/Create",
            "/healthz",
            "/readyz",
            "/livez",
        ];

    [Fact]
    public void ShouldReject_MetricsPathOnAPublicPort_ShouldReject()
    {
        MetricsPortGuard.ShouldReject("/metrics", PublicPort, MetricsPort).Should().BeTrue();
    }

    [Fact]
    public void ShouldReject_MetricsPathOnTheMetricsPort_ShouldAllow()
    {
        MetricsPortGuard.ShouldReject("/metrics", MetricsPort, MetricsPort).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(NonMetricsPaths))]
    public void ShouldReject_NonMetricsPathOnTheMetricsPort_ShouldReject(string path)
    {
        MetricsPortGuard.ShouldReject(path, MetricsPort, MetricsPort).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(NonMetricsPaths))]
    public void ShouldReject_NonMetricsPathOnAPublicPort_ShouldAllow(string path)
    {
        MetricsPortGuard.ShouldReject(path, PublicPort, MetricsPort).Should().BeFalse();
    }

    // Endpoint routing matches case-insensitively, so a guard that didn't would hand "/METRICS" to
    // the exporter on a public port while believing it had blocked it.
    [Fact]
    public void ShouldReject_DifferentlyCasedMetricsPathOnAPublicPort_ShouldReject()
    {
        MetricsPortGuard.ShouldReject("/METRICS", PublicPort, MetricsPort).Should().BeTrue();
    }

    [Fact]
    public void ShouldReject_DifferentlyCasedMetricsPathOnTheMetricsPort_ShouldAllow()
    {
        MetricsPortGuard.ShouldReject("/Metrics", MetricsPort, MetricsPort).Should().BeFalse();
    }

    // The WebApplicationFactory case: MetricsPort=0 and a TestServer local port of 0 must not be
    // read as "this is the metrics listener", or every test would get a 404 for the whole app.
    [Theory]
    [MemberData(nameof(NonMetricsPaths))]
    public void ShouldReject_NonMetricsPathWithNoDedicatedMetricsPort_ShouldAllow(string path)
    {
        MetricsPortGuard.ShouldReject(path, 0, 0).Should().BeFalse();
    }

    // Unchanged from before this guard grew its second half: with no dedicated metrics port
    // there's nowhere /metrics is legitimately served, so it stays unreachable everywhere.
    [Theory]
    [InlineData(0)]
    [InlineData(41234)]
    public void ShouldReject_MetricsPathWithNoDedicatedMetricsPort_ShouldReject(int localPort)
    {
        MetricsPortGuard.ShouldReject("/metrics", localPort, 0).Should().BeTrue();
    }
}
