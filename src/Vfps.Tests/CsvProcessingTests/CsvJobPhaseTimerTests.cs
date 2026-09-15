using System.Diagnostics;
using System.Diagnostics.Metrics;
using Vfps.CsvProcessing;
using Vfps.Data.Models;

namespace Vfps.Tests.CsvProcessingTests;

public class CsvJobPhaseTimerTests
{
    private const string MetricName = "vfps.csv.job.phase.duration.seconds";

    /// <summary>
    /// Collects this app's own meter for the duration of the callback. The counter is static (one
    /// instrument for the whole process, as every other metric in this app is), so the listener
    /// rather than the instrument is what scopes a test to its own measurements.
    /// </summary>
    private static List<(string Phase, string Direction, double Value)> Collect(Action act)
    {
        var measurements = new List<(string, string, double)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Vfps" && instrument.Name == MetricName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>(
            (_, value, tags, _) =>
            {
                string? phase = null;
                string? direction = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "phase")
                    {
                        phase = tag.Value?.ToString();
                    }
                    else if (tag.Key == "direction")
                    {
                        direction = tag.Value?.ToString();
                    }
                }

                lock (measurements)
                {
                    measurements.Add((phase!, direction!, value));
                }
            }
        );
        listener.Start();

        act();

        listener.RecordObservableInstruments();
        return measurements;
    }

    [Fact]
    public void Flush_ShouldReportEveryPhaseTaggedWithTheJobDirection()
    {
        var measurements = Collect(() =>
            new CsvJobPhaseTimer(PseudonymizationJobDirection.Export).Flush(null)
        );

        // Every phase reported even though none was measured - a phase that doesn't apply to a
        // direction still has to exist as a series, or a stacked dashboard silently omits it.
        measurements
            .Select(m => m.Phase)
            .Should()
            .BeEquivalentTo(["read_input", "resolve_database", "write_output", "report_progress"]);
        measurements.Should().OnlyContain(m => m.Direction == "Export");
        measurements.Should().OnlyContain(m => m.Value == 0);
    }

    [Fact]
    public void Measure_ShouldAccumulateTimeAgainstItsOwnPhaseOnly()
    {
        var sut = new CsvJobPhaseTimer(PseudonymizationJobDirection.Pseudonymize);

        var measurements = Collect(() =>
        {
            using (sut.Measure(CsvJobPhase.ResolveDatabase))
            {
                Thread.Sleep(20);
            }

            sut.Flush(null);
        });

        var byPhase = measurements.ToDictionary(m => m.Phase, m => m.Value);
        byPhase["resolve_database"].Should().BeGreaterThan(0.01);
        byPhase["read_input"].Should().Be(0);
        byPhase["write_output"].Should().Be(0);
        byPhase["report_progress"].Should().Be(0);
    }

    [Fact]
    public void Measure_CalledRepeatedly_ShouldSumIntoOneTotalPerPhase()
    {
        var sut = new CsvJobPhaseTimer(PseudonymizationJobDirection.Pseudonymize);

        var measurements = Collect(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                using (sut.Measure(CsvJobPhase.WriteOutput))
                {
                    Thread.Sleep(10);
                }
            }

            sut.Flush(null);
        });

        // One measurement per phase however many scopes were opened - the whole point of
        // accumulating is that a million-chunk job reports once, not a million times.
        measurements.Count(m => m.Phase == "write_output").Should().Be(1);
        measurements.Single(m => m.Phase == "write_output").Value.Should().BeGreaterThan(0.02);
    }

    [Fact]
    public void Flush_ShouldMirrorTheBreakdownOntoTheActivity()
    {
        // An ActivitySource with no listener returns null from StartActivity, so a test that wants
        // a real Activity has to register one that samples.
        using var source = new ActivitySource("CsvJobPhaseTimerTests");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "CsvJobPhaseTimerTests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("job");
        activity.Should().NotBeNull();

        var sut = new CsvJobPhaseTimer(PseudonymizationJobDirection.Depseudonymize);
        using (sut.Measure(CsvJobPhase.ReadInput))
        {
            Thread.Sleep(20);
        }

        sut.Flush(activity);

        activity!
            .GetTagItem("vfps.csv.phase.read_input.seconds")
            .Should()
            .BeOfType<double>()
            .Which.Should()
            .BeGreaterThan(0.01);
        activity.GetTagItem("vfps.csv.phase.resolve_database.seconds").Should().Be(0d);
    }
}
