using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Configuration;
using NBomber.Contracts;
using NBomber.CSharp;
using NBomber.Plugins.Network.Ping;
using Vfps.Protos;

var grpcAddress = new Uri(Environment.GetEnvironmentVariable("VFPS_GRPC_ADDRESS") ?? "http://localhost:8081");

var defaultMethodConfig = new MethodConfig
{
    Names = { MethodName.Default },
    RetryPolicy = new RetryPolicy
    {
        MaxAttempts = 3,
        InitialBackoff = TimeSpan.FromSeconds(5),
        MaxBackoff = TimeSpan.FromSeconds(30),
        BackoffMultiplier = 2,
        RetryableStatusCodes = { StatusCode.Unavailable, StatusCode.Internal }
    }
};

using var channel = GrpcChannel.ForAddress(grpcAddress, new GrpcChannelOptions()
{
    ServiceConfig = new ServiceConfig()
    {
        MethodConfigs = { defaultMethodConfig }
    },
    Credentials = ChannelCredentials.Insecure,
    UnsafeUseInsecureChannelCallCredentials = true,
});

var namespaceClient = new NamespaceService.NamespaceServiceClient(channel);
var pseudonymClient = new PseudonymService.PseudonymServiceClient(channel);

var namespaceRequest = new NamespaceServiceCreateRequest()
{
    Name = "stress-test",
    PseudonymGenerationMethod = PseudonymGenerationMethod.SecureRandomBase64UrlEncoded,
    PseudonymLength = 16,
    PseudonymPrefix = "stress-",
};

var createPseudonyms = Step.Create("create_pseudonyms",
                execute: async context =>
                {
                    var request = new PseudonymServiceCreateRequest()
                    {
                        Namespace = namespaceRequest.Name,
                        OriginalValue = Guid.NewGuid().ToString(),
                    };

                    try
                    {
                        var response = await pseudonymClient.CreateAsync(request);
                        return Response.Ok(statusCode: 200, sizeBytes: request.CalculateSize() + response.CalculateSize());
                    }
                    catch (RpcException exc)
                    {
                        context.Logger.Error(exc, "Pseudonym creation failed");
                        return Response.Fail();
                    }
                });

var scenario = ScenarioBuilder
    .CreateScenario("stress_pseudonym_creation", createPseudonyms)
    .WithInit(async context =>
    {
        try
        {
            var response = await namespaceClient.CreateAsync(namespaceRequest);
        }
        catch (RpcException exc) when (exc.StatusCode == StatusCode.AlreadyExists)
        {
            context.Logger.Warning($"Namespace {namespaceRequest.Name} already exists. Continuing anyway.");
        }
    })
    .WithWarmUpDuration(TimeSpan.FromSeconds(5))
    .WithLoadSimulations(
        Simulation.RampConstant(copies: 10, during: TimeSpan.FromMinutes(4)),
        Simulation.KeepConstant(copies: 100, during: TimeSpan.FromMinutes(4)),
        Simulation.InjectPerSecRandom(minRate: 10, maxRate: 50, during: TimeSpan.FromMinutes(4))
    );

// creates ping plugin that brings additional reporting data
var pingPluginConfig = PingPluginConfig.CreateDefault(new[] { grpcAddress.Host });
var pingPlugin = new PingPlugin(pingPluginConfig);

var stats = NBomberRunner
    .RegisterScenarios(scenario)
    .WithWorkerPlugins(pingPlugin)
    .Run();

Debug.Assert(stats.FailCount < 100);
