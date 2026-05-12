using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vfps.Data;
using Vfps.PseudonymGenerators;
using Vfps.Tests.ServiceTests;
using Vfps.UI;

namespace Vfps.Tests.UI;

public class CsvPseudonymizationBackgroundServiceTests : ServiceTestBase
{
    private readonly CsvJobService _jobService;
    private readonly CsvPseudonymizationBackgroundService _sut;

    public CsvPseudonymizationBackgroundServiceTests()
    {
        _jobService = new CsvJobService();

        var services = new ServiceCollection();
        services.AddSingleton(InMemoryPseudonymContext);
        services.AddSingleton<INamespaceRepository>(new NamespaceRepository(InMemoryPseudonymContext));
        services.AddSingleton<IPseudonymRepository>(new PseudonymRepository(InMemoryPseudonymContext));
        services.AddSingleton<PseudonymizationMethodsLookup>();

        _sut = new CsvPseudonymizationBackgroundService(
            _jobService,
            services.BuildServiceProvider(),
            NullLogger<CsvPseudonymizationBackgroundService>.Instance
        );
    }

    [Fact]
    public async Task ProcessJob_WithValidCsvAndSelectedColumns_ShouldProduceOutputFile()
    {
        var inputFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.csv");
        var csvContent = "patient_id,diagnosis,visit_date\nPAT001,cold,2024-01-01\nPAT002,flu,2024-02-01\n";
        await File.WriteAllTextAsync(inputFile, csvContent);

        try
        {
            var job = _jobService.EnqueueJob(
                inputFile,
                columnsToProcess: ["patient_id"],
                namespaceName: "existingNamespace"
            );

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var backgroundTask = _sut.StartAsync(cts.Token);

            // Wait for the job to complete
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (job.Status != CsvJobStatus.Done && job.Status != CsvJobStatus.Failed && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            await cts.CancelAsync();
            await backgroundTask;

            job.Status.Should().Be(CsvJobStatus.Done);
            job.OutputFilePath.Should().NotBeNull();
            File.Exists(job.OutputFilePath).Should().BeTrue();
            job.TotalRows.Should().Be(2);

            // The output should still have the original diagnosis and visit_date columns
            var outputContent = await File.ReadAllTextAsync(job.OutputFilePath!);
            outputContent.Should().Contain("cold");
            outputContent.Should().Contain("flu");
            outputContent.Should().Contain("2024-01-01");

            // The patient_id column should have been replaced with pseudonyms
            outputContent.Should().NotContain("PAT001");
            outputContent.Should().NotContain("PAT002");
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
        }
    }

    [Fact]
    public async Task ProcessJob_ShouldStorePseudonymsInDatabase()
    {
        var inputFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(inputFile, "id,name\nALICE,Alice Smith\nBOB,Bob Jones\n");

        try
        {
            var job = _jobService.EnqueueJob(
                inputFile,
                columnsToProcess: ["id"],
                namespaceName: "existingNamespace"
            );

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var backgroundTask = _sut.StartAsync(cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (job.Status != CsvJobStatus.Done && job.Status != CsvJobStatus.Failed && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            await cts.CancelAsync();
            await backgroundTask;

            job.Status.Should().Be(CsvJobStatus.Done);

            // The original values should now have pseudonyms stored in the DB
            InMemoryPseudonymContext.Pseudonyms
                .Where(p => p.NamespaceName == "existingNamespace" && p.OriginalValue == "ALICE")
                .Should().HaveCount(1);
            InMemoryPseudonymContext.Pseudonyms
                .Where(p => p.NamespaceName == "existingNamespace" && p.OriginalValue == "BOB")
                .Should().HaveCount(1);
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
        }
    }

    [Fact]
    public async Task ProcessJob_WithNonExistingNamespace_ShouldMarkJobAsFailed()
    {
        var inputFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(inputFile, "id\nABC\n");

        try
        {
            var job = _jobService.EnqueueJob(
                inputFile,
                columnsToProcess: ["id"],
                namespaceName: "nonExistingNamespace"
            );

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var backgroundTask = _sut.StartAsync(cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (job.Status != CsvJobStatus.Done && job.Status != CsvJobStatus.Failed && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            await cts.CancelAsync();
            await backgroundTask;

            job.Status.Should().Be(CsvJobStatus.Failed);
            job.ErrorMessage.Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(inputFile)) File.Delete(inputFile);
        }
    }

    [Fact]
    public void EnqueueJob_ShouldReturnJobWithQueuedStatus()
    {
        var jobService = new CsvJobService();
        var job = jobService.EnqueueJob(
            "/tmp/test.csv",
            columnsToProcess: ["col1", "col2"],
            namespaceName: "testNs"
        );

        job.Status.Should().Be(CsvJobStatus.Queued);
        job.ColumnsToProcess.Should().BeEquivalentTo(["col1", "col2"]);
        job.NamespaceName.Should().Be("testNs");
    }

    [Fact]
    public void GetAllJobs_AfterEnqueuingMultipleJobs_ShouldReturnAllJobs()
    {
        var jobService = new CsvJobService();
        jobService.EnqueueJob("/tmp/a.csv", ["col"], "ns");
        jobService.EnqueueJob("/tmp/b.csv", ["col"], "ns");
        jobService.EnqueueJob("/tmp/c.csv", ["col"], "ns");

        jobService.GetAllJobs().Should().HaveCount(3);
    }
}
