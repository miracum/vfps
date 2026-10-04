using Vfps.Components;

namespace Vfps.Tests.ComponentsTests;

/// <summary>
/// The example calls on the Access Tokens page are meant to be pasted into a shell, so they have
/// to point at the right address and pick up the token and namespace the reader exports.
/// </summary>
public class ApiCallExamplesTests
{
    [Fact]
    public void Curl_ShouldCallTheApiAtTheRootRatherThanUnderTheUiPathBase()
    {
        var command = ApiCallExamples.Curl(new Uri("https://vfps.example.org/ui/"));

        command
            .Should()
            .Contain("\"https://vfps.example.org/v1/namespaces/$VFPS_NAMESPACE/pseudonyms\"");
    }

    [Fact]
    public void Curl_ShouldSendTheExportedToken()
    {
        var command = ApiCallExamples.Curl(new Uri("http://localhost:8080/ui/"));

        command.Should().StartWith("export VFPS_TOKEN=\"vfps_pat_...\"\nexport VFPS_NAMESPACE=");
        command.Should().Contain("-H \"Authorization: Bearer $VFPS_TOKEN\"");
    }

    [Fact]
    public void Grpcurl_ShouldPutTheExportedNamespaceIntoADoubleQuotedRequestBody()
    {
        // Double quotes, not single: inside single quotes the shell wouldn't expand the variable
        // and the server would be asked for a namespace literally named "$VFPS_NAMESPACE".
        var command = ApiCallExamples.Grpcurl("localhost:8081");

        command
            .Should()
            .Contain(
                """-d "{\"namespace\": \"$VFPS_NAMESPACE\", \"originalValue\": \"to be pseudonymized\"}" \"""
            );
        command.Should().Contain("-H \"authorization: Bearer $VFPS_TOKEN\"");
        command.Should().Contain("localhost:8081 \\");
        command.Should().EndWith("vfps.api.v1.PseudonymService/Create");
    }

    [Fact]
    public void Grpcurl_ShouldRelyOnServerReflectionRatherThanProtoFiles()
    {
        // The server offers reflection everywhere, so the command works as pasted with no
        // checkout of the repository around it.
        var command = ApiCallExamples.Grpcurl("localhost:8081");

        command.Should().NotContain("-proto").And.NotContain("-import-path");
    }

    [Theory]
    [InlineData("http://0.0.0.0:8081", 8081)]
    [InlineData("http://*:9091", 9091)]
    [InlineData("http://+:9092", 9092)]
    [InlineData(null, ApiCallExamples.DefaultGrpcPort)]
    [InlineData("", ApiCallExamples.DefaultGrpcPort)]
    [InlineData("not a url", ApiCallExamples.DefaultGrpcPort)]
    public void GrpcPort_ShouldReadThePortOfTheConfiguredEndpoint(string? url, int expected)
    {
        ApiCallExamples.GrpcPort(url).Should().Be(expected);
    }
}
