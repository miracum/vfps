using NBomber.Contracts.Stats;

namespace Vfps.StressTests;

public class StressTests
{
    private readonly NamespaceService.NamespaceServiceClient namespaceService;
    private readonly PseudonymService.PseudonymServiceClient pseudonymService;
    private readonly string reportFolder;

    public StressTests()
    {
        var grpcAddress = new Uri(Environment.GetEnvironmentVariable("VFPS_GRPC_ADDRESS") ?? "http://localhost:8081");

        var defaultMethodConfig = new MethodConfig
        {
            Names = { MethodName.Default },
            RetryPolicy = new RetryPolicy
            {
                MaxAttempts = 5,
                InitialBackoff = TimeSpan.FromSeconds(5),
                MaxBackoff = TimeSpan.FromSeconds(30),
                BackoffMultiplier = 2,
                RetryableStatusCodes = { StatusCode.Unavailable, StatusCode.Internal }
            }
        };

        var channel = GrpcChannel.ForAddress(grpcAddress, new GrpcChannelOptions()
        {
            ServiceConfig = new ServiceConfig()
            {
                MethodConfigs = { defaultMethodConfig }
            },
            Credentials = ChannelCredentials.Insecure,
            UnsafeUseInsecureChannelCallCredentials = true,
        });

        namespaceService = new NamespaceService.NamespaceServiceClient(channel);
        pseudonymService = new PseudonymService.PseudonymServiceClient(channel);

        _ = bool.TryParse(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), out bool isRunningInContainer);
        reportFolder = isRunningInContainer ? Path.Combine(Path.GetTempPath(), "reports") : "./nbomber-reports";
    }

    private IStep CreatePseudonymStep(string namespaceName)
    {
        return Step.Create("create_pseudonyms",
                execute: async context =>
                {
                    var request = new PseudonymServiceCreateRequest()
                    {
                        Namespace = namespaceName,
                        OriginalValue = Guid.NewGuid().ToString(),
                    };

                    try
                    {
                        var response = await pseudonymService.CreateAsync(request);
                        return Response.Ok(statusCode: 200, sizeBytes: request.CalculateSize() + response.CalculateSize());
                    }
                    catch (RpcException exc)
                    {
                        context.Logger.Error(exc, "Pseudonym creation failed");
                        return Response.Fail();
                    }
                });
    }

    [Fact]
    public void RunStressSimulation()
    {
        var namespaceRequest = new NamespaceServiceCreateRequest()
        {
            Name = nameof(RunStressSimulation),
            PseudonymGenerationMethod = PseudonymGenerationMethod.SecureRandomBase64UrlEncoded,
            PseudonymLength = 16,
            PseudonymPrefix = "stress-",
        };

        var scenario = ScenarioBuilder
            .CreateScenario(namespaceRequest.Name, CreatePseudonymStep(namespaceRequest.Name))
            .WithInit(async context =>
            {
                try
                {
                    await namespaceService.CreateAsync(namespaceRequest);
                }
                catch (RpcException exc) when (exc.StatusCode == StatusCode.AlreadyExists)
                {
                    context.Logger.Warning($"Namespace {namespaceRequest.Name} already exists. Continuing anyway.");
                }
            })
            .WithWarmUpDuration(TimeSpan.FromSeconds(5))
            .WithLoadSimulations(
                Simulation.RampConstant(copies: 10, during: TimeSpan.FromMinutes(5)),
                Simulation.KeepConstant(copies: 100, during: TimeSpan.FromMinutes(5)),
                Simulation.InjectPerSecRandom(minRate: 10, maxRate: 50, during: TimeSpan.FromMinutes(5))
            );

        var stats = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder(reportFolder)
            .WithReportFormats(ReportFormat.Txt, ReportFormat.Csv, ReportFormat.Html, ReportFormat.Md)
            .Run();

        var failPercentage = stats.FailCount / (double)stats.RequestCount * 100.0;
        failPercentage.Should().BeLessThan(0.1);
    }
}
