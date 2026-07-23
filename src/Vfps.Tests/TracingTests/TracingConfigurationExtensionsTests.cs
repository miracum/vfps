using Microsoft.AspNetCore.Builder;
using Vfps.Tracing;

namespace Vfps.Tests.TracingTests;

public class TracingConfigurationExtensionsTests
{
    private static WebApplicationBuilder CreateBuilder(string rootSampler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Tracing:RootSampler"] = rootSampler;
        return builder;
    }

    [Theory]
    [InlineData("AlwaysOnSampler")]
    [InlineData("AlwaysOffSampler")]
    [InlineData("TraceIdRatioBasedSampler")]
    public void AddTracing_WithSupportedSamplerType_ShouldNotThrow(string samplerType)
    {
        var builder = CreateBuilder(samplerType);

        var act = () => builder.AddTracing();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddTracing_WithUnsupportedSamplerType_ShouldThrowArgumentException()
    {
        var builder = CreateBuilder("SomeUnknownSampler");

        var act = () => builder.AddTracing();

        act.Should().Throw<ArgumentException>().WithMessage("*SomeUnknownSampler*");
    }
}
