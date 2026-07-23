using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vfps.Config;
using Vfps.CsvProcessing;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Tests.CsvProcessingTests;

public class StalledPseudonymizationJobWatchdogServiceTests
{
    // Short enough that a test can comfortably wait for a real tick without a slow test run - the
    // service no longer checks immediately on startup (see ExecuteAsync's own comment on why), so
    // every test here needs at least one real PeriodicTimer tick to elapse.
    private static readonly TimeSpan TestCheckInterval = TimeSpan.FromMilliseconds(20);

    // A real (minimal) DI container, not a FakeItEasy fake of IServiceProvider - the service
    // resolves its repository via serviceProvider.CreateScope(), which needs a real
    // IServiceScopeFactory behind it to work at all, same reasoning as CsvJobs.razor's
    // ReloadJobsAsync (see its own comment on why a shared scope isn't used here either).
    private static IServiceProvider CreateServiceProvider(
        IPseudonymizationJobRepository jobRepository
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(jobRepository);
        return services.BuildServiceProvider();
    }

    private static StalledPseudonymizationJobWatchdogService CreateSut(
        IPseudonymizationJobRepository jobRepository,
        TimeSpan? staleThreshold = null
    ) =>
        new(
            CreateServiceProvider(jobRepository),
            Options.Create(
                new CsvProcessingConfig
                {
                    StalledJobThreshold = staleThreshold ?? TimeSpan.FromMinutes(10),
                }
            ),
            NullLogger<StalledPseudonymizationJobWatchdogService>.Instance,
            TestCheckInterval
        );

    [Fact]
    public async Task ExecuteAsync_WithStalledJobs_ShouldMarkEachOneStalled()
    {
        var jobRepository = A.Fake<IPseudonymizationJobRepository>();
        var staleThreshold = TimeSpan.FromMinutes(10);
        var stalledJobId1 = Guid.NewGuid();
        var stalledJobId2 = Guid.NewGuid();
        A.CallTo(() =>
                jobRepository.FindStalledRunningJobIdsAsync(staleThreshold, A<CancellationToken>._)
            )
            .Returns([stalledJobId1, stalledJobId2]);
        var sut = CreateSut(jobRepository, staleThreshold);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    stalledJobId1,
                    PseudonymizationJobStatus.Stalled,
                    A<string>.That.IsNotNull(),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceOrMore();
        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    stalledJobId2,
                    PseudonymizationJobStatus.Stalled,
                    A<string>.That.IsNotNull(),
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceOrMore();
    }

    [Fact]
    public async Task ExecuteAsync_OnStartup_ShouldNotCheckBeforeTheFirstTick()
    {
        // A restart/upgrade is exactly when a job that's actually still fine is most likely to
        // look "stalled" for a moment - checking the instant this instance comes up (before it's
        // had a chance to catch up) is the one thing this test guards against regressing back to.
        var jobRepository = A.Fake<IPseudonymizationJobRepository>();
        A.CallTo(() =>
                jobRepository.FindStalledRunningJobIdsAsync(A<TimeSpan>._, A<CancellationToken>._)
            )
            .Returns([Guid.NewGuid()]);
        var sut = new StalledPseudonymizationJobWatchdogService(
            CreateServiceProvider(jobRepository),
            Options.Create(new CsvProcessingConfig()),
            NullLogger<StalledPseudonymizationJobWatchdogService>.Instance,
            TimeSpan.FromMinutes(10)
        );

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        A.CallTo(() =>
                jobRepository.FindStalledRunningJobIdsAsync(A<TimeSpan>._, A<CancellationToken>._)
            )
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_WithNoStalledJobs_ShouldNotUpdateAnyStatus()
    {
        var jobRepository = A.Fake<IPseudonymizationJobRepository>();
        A.CallTo(() =>
                jobRepository.FindStalledRunningJobIdsAsync(A<TimeSpan>._, A<CancellationToken>._)
            )
            .Returns([]);
        var sut = CreateSut(jobRepository);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        A.CallTo(() =>
                jobRepository.UpdateStatusAsync(
                    A<Guid>._,
                    A<PseudonymizationJobStatus>._,
                    A<string>._,
                    A<CancellationToken>._
                )
            )
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ExecuteAsync_WhenRepositoryThrows_ShouldNotCrashTheHost()
    {
        // An unhandled BackgroundService exception stops the entire host - a transient DB issue
        // while checking for stalled jobs must not be allowed to take the whole app down, same
        // reasoning as every other periodic background service in this codebase.
        var jobRepository = A.Fake<IPseudonymizationJobRepository>();
        A.CallTo(() =>
                jobRepository.FindStalledRunningJobIdsAsync(A<TimeSpan>._, A<CancellationToken>._)
            )
            .Throws(new InvalidOperationException("boom"));
        var sut = CreateSut(jobRepository);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await sut.StopAsync(TestContext.Current.CancellationToken);

        var act = async () => await sut.ExecuteTask!;
        await act.Should().NotThrowAsync();
    }
}
