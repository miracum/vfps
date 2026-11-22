using Microsoft.AspNetCore.Http.Features;

namespace Vfps.Metrics;

/// <summary>
/// Tags ASP.NET Core's <c>http.server.request.duration</c> metric with a gRPC call's status code,
/// as <c>rpc.grpc.status_code</c> (exported as <c>rpc_grpc_status_code</c>).
///
/// A gRPC call answers HTTP 200 whether or not it succeeded - the outcome travels in the
/// <c>grpc-status</c> trailer - so without this every failed gRPC call is indistinguishable from a
/// successful one in the HTTP metrics, and an error-rate alert built on
/// <c>http_response_status_code</c> only ever sees the JSON-transcoded REST calls. Grpc.AspNetCore
/// sets the status only as a tracing tag, not on the metric. Added after the call has run, which
/// is still before the hosting layer records the measurement at the end of the request.
/// </summary>
public sealed class GrpcStatusMetricsMiddleware(RequestDelegate next)
{
    private const string StatusTag = "rpc.grpc.status_code";
    private const string GrpcStatusHeader = "grpc-status";

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (
            context.Request.ContentType?.StartsWith("application/grpc", StringComparison.Ordinal)
                is not true
            || context.Features.Get<IHttpMetricsTagsFeature>() is not { } tags
        )
        {
            return;
        }

        if (TryGetStatus(context, out var status))
        {
            tags.Tags.Add(new KeyValuePair<string, object?>(StatusTag, status));
        }
    }

    // A call that fails before writing any message answers "trailers-only": the status goes out
    // in the response headers instead of the trailers, so both have to be looked at.
    private static bool TryGetStatus(HttpContext context, out int status)
    {
        var trailers = context.Features.Get<IHttpResponseTrailersFeature>()?.Trailers;
        var value =
            trailers is not null && trailers.TryGetValue(GrpcStatusHeader, out var trailer)
                ? trailer.ToString()
                : context.Response.Headers[GrpcStatusHeader].ToString();

        return int.TryParse(value, out status);
    }
}
