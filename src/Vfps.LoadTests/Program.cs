using NBomber.CSharp;
using NBomber.Plugins.Http.CSharp;
using NBomber.Plugins.Network.Ping;
using System.Net.Http.Json;

var baseUrl = "https://localhost:7078/v1";

var httpFactory = HttpClientFactory.Create();

var createNamespace = Step.Create("create_namespace",
                clientFactory: httpFactory,
                execute: context =>
                {
                    var request = Http.CreateRequest("POST", baseUrl + "/namespaces")
                        .WithHeader("Accept", "application/json")
                        .WithBody(JsonContent.Create(new
                        {
                            Name = "load-test",
                            PseudonymLength = 32,
                            PseudonymGenerationMethod = 1,
                        }));

                    return Http.Send(request, context);
                });

var createPseudonyms = Step.Create("create_pseudonyms",
                clientFactory: httpFactory,
                execute: context =>
                {
                    var request = Http.CreateRequest("POST", baseUrl + "/namespaces/load-test/pseudonyms")
                        .WithHeader("Accept", "application/json")
                        .WithBody(JsonContent.Create(new
                        {
                            OriginalValue = Guid.NewGuid().ToString(),
                        }));

                    return Http.Send(request, context);
                });

var scenario = ScenarioBuilder
    .CreateScenario("create_namespace_and_pseudonyms", createNamespace, createPseudonyms)
    .WithWarmUpDuration(TimeSpan.FromSeconds(5))
    .WithLoadSimulations(
        Simulation.InjectPerSec(rate: 100, during: TimeSpan.FromSeconds(30))
    );

// creates ping plugin that brings additional reporting data
var pingPluginConfig = PingPluginConfig.CreateDefault(new[] { "127.0.0.1" });
var pingPlugin = new PingPlugin(pingPluginConfig);

NBomberRunner
    .RegisterScenarios(scenario)
    .WithWorkerPlugins(pingPlugin)
    .Run();
