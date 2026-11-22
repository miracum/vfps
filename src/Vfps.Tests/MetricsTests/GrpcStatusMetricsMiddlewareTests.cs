using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Vfps.Metrics;

namespace Vfps.Tests.MetricsTests;

public class GrpcStatusMetricsMiddlewareTests
{
    private sealed class MetricsTagsFeature : IHttpMetricsTagsFeature
    {
        public ICollection<KeyValuePair<string, object?>> Tags { get; } = [];
        public bool MetricsDisabled { get; set; }
    }

    private sealed class ResponseTrailersFeature : IHttpResponseTrailersFeature
    {
        public IHeaderDictionary Trailers { get; set; } = new HeaderDictionary();
    }

    private static (DefaultHttpContext Context, MetricsTagsFeature Tags) CreateContext(
        string contentType
    )
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        var tags = new MetricsTagsFeature();
        context.Features.Set<IHttpMetricsTagsFeature>(tags);
        context.Features.Set<IHttpResponseTrailersFeature>(new ResponseTrailersFeature());
        return (context, tags);
    }

    [Fact]
    public async Task InvokeAsync_WithTheStatusInTheTrailers_ShouldTagIt()
    {
        var (context, tags) = CreateContext("application/grpc");
        var sut = new GrpcStatusMetricsMiddleware(ctx =>
        {
            ctx.Features.Get<IHttpResponseTrailersFeature>()!.Trailers["grpc-status"] = "13";
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context);

        tags.Tags.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new KeyValuePair<string, object?>("rpc.grpc.status_code", 13));
    }

    [Fact]
    public async Task InvokeAsync_WithATrailersOnlyResponse_ShouldTagTheStatusFromTheHeaders()
    {
        // A call failing before it writes a message sends its status in the headers instead.
        var (context, tags) = CreateContext("application/grpc+proto");
        var sut = new GrpcStatusMetricsMiddleware(ctx =>
        {
            ctx.Response.Headers["grpc-status"] = new StringValues("3");
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context);

        tags.Tags.Should().ContainSingle().Which.Value.Should().Be(3);
    }

    [Fact]
    public async Task InvokeAsync_ForARequestThatIsNotGrpc_ShouldAddNoTag()
    {
        // JSON-transcoded REST calls already carry their outcome in the HTTP status code.
        var (context, tags) = CreateContext("application/json");
        var sut = new GrpcStatusMetricsMiddleware(ctx =>
        {
            ctx.Response.Headers["grpc-status"] = "0";
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context);

        tags.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WithoutAnyStatus_ShouldAddNoTag()
    {
        var (context, tags) = CreateContext("application/grpc");
        var sut = new GrpcStatusMetricsMiddleware(_ => Task.CompletedTask);

        await sut.InvokeAsync(context);

        tags.Tags.Should().BeEmpty();
    }
}
