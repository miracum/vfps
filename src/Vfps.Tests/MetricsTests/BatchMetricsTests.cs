using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Vfps.Data.Models;
using Vfps.Metrics;
using Vfps.Tests.ServiceTests;

namespace Vfps.Tests.MetricsTests;

public class BatchMetricsTests : ServiceTestBase
{
    /// <summary>
    /// Records what <see cref="BatchMetrics"/> emits on the calling thread only: the instruments
    /// are static, and other test classes running in parallel record into them too. Counter
    /// measurements are delivered synchronously on the recording thread; gauges are observed on
    /// the thread calling RecordObservableInstruments, which is this one as well.
    /// </summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly int threadId = Environment.CurrentManagedThreadId;

        public List<(
            string Instrument,
            double Value,
            Dictionary<string, object?> Tags
        )> Measurements { get; } = [];

        public Recorder()
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (
                    instrument.Meter.Name == "Vfps"
                    && instrument.Name.StartsWith("vfps.batches", StringComparison.Ordinal)
                )
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
            listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
            listener.Start();
        }

        private void Add(
            Instrument instrument,
            double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags
        )
        {
            if (Environment.CurrentManagedThreadId == threadId)
            {
                Measurements.Add(
                    (instrument.Name, value, new Dictionary<string, object?>(tags.ToArray()))
                );
            }
        }

        public void Observe() => listener.RecordObservableInstruments();

        public void Dispose() => listener.Dispose();
    }

    [Fact]
    public void RecordFailed_ShouldLabelTheOutcomeAndReason()
    {
        using var recorder = new Recorder();

        BatchMetrics.RecordFailed(BatchMetrics.FailureReason.UnknownValue);
        BatchMetrics.RecordFailed(BatchMetrics.FailureReason.Error);

        recorder
            .Measurements.Select(m => (string?)m.Tags["reason"])
            .Should()
            .Equal("unknown_value", "error");
        recorder
            .Measurements.Should()
            .AllSatisfy(m =>
            {
                m.Instrument.Should().Be("vfps.batches.finished");
                m.Tags["outcome"].Should().Be("failed");
            });
    }

    [Theory]
    [InlineData(PseudonymizationJobStatus.Completed, "completed")]
    [InlineData(PseudonymizationJobStatus.Cancelled, "cancelled")]
    [InlineData(PseudonymizationJobStatus.Stalled, "stalled")]
    public void RecordFinished_WithAFinalState_ShouldCountIt(
        PseudonymizationJobStatus status,
        string outcome
    )
    {
        using var recorder = new Recorder();

        BatchMetrics.RecordFinished(status);

        recorder.Measurements.Should().ContainSingle().Which.Tags["outcome"].Should().Be(outcome);
    }

    [Theory]
    [InlineData(PseudonymizationJobStatus.Running)]
    [InlineData(PseudonymizationJobStatus.Queued)]
    [InlineData(PseudonymizationJobStatus.Failed)] // only RecordFailed knows why it failed
    public void RecordFinished_WithAnyOtherState_ShouldCountNothing(
        PseudonymizationJobStatus status
    )
    {
        using var recorder = new Recorder();

        BatchMetrics.RecordFinished(status);

        recorder.Measurements.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteAsync_ShouldCountACompletionOnlyWhenTheTransitionMatched()
    {
        // A batch that isn't running can't complete - and mustn't be counted as if it had.
        var repository = new PseudonymizationJobRepository(ContextFactory);
        var job = await repository.CreateAsync(
            new PseudonymizationJob
            {
                Id = Guid.NewGuid(),
                CreatedBy = "alice",
                Status = PseudonymizationJobStatus.Queued,
            },
            TestContext.Current.CancellationToken
        );
        using var recorder = new Recorder();

        var matched = await repository.CompleteAsync(
            job.Id,
            "out.csv",
            1,
            TestContext.Current.CancellationToken
        );

        matched.Should().BeFalse();
        recorder.Measurements.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshAsync_ShouldPublishEveryStateAndTheOldestQueuedAge()
    {
        var repository = new PseudonymizationJobRepository(ContextFactory);
        var longAgo = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (
            var (status, lastUpdated) in new[]
            {
                (PseudonymizationJobStatus.Queued, longAgo),
                (PseudonymizationJobStatus.Queued, DateTimeOffset.UtcNow),
                (PseudonymizationJobStatus.Failed, DateTimeOffset.UtcNow),
            }
        )
        {
            await repository.CreateAsync(
                new PseudonymizationJob
                {
                    Id = Guid.NewGuid(),
                    CreatedBy = "alice",
                    Status = status,
                    CreatedAt = lastUpdated,
                    LastUpdatedAt = lastUpdated,
                },
                TestContext.Current.CancellationToken
            );
        }

        await new BatchMetricsBackgroundService(
            repository,
            NullLogger<BatchMetricsBackgroundService>.Instance
        ).RefreshAsync(TestContext.Current.CancellationToken);
        using var recorder = new Recorder();
        recorder.Observe();

        var byState = recorder
            .Measurements.Where(m => m.Instrument == "vfps.batches")
            .ToDictionary(m => (string)m.Tags["state"]!, m => m.Value);
        byState
            .Should()
            .HaveCount(
                Enum.GetValues<PseudonymizationJobStatus>().Length,
                "every state is reported, zeros included"
            );
        byState["queued"].Should().Be(2);
        byState["failed"].Should().Be(1);
        byState["running"].Should().Be(0);
        byState["awaiting_upload"].Should().Be(0);
        recorder
            .Measurements.Single(m => m.Instrument == "vfps.batches.oldest_queued.age")
            .Value.Should()
            .BeApproximately(TimeSpan.FromHours(2).TotalSeconds, 60);
    }
}
