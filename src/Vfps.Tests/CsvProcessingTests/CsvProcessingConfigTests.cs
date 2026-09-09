using Vfps.Config;

namespace Vfps.Tests.CsvProcessingTests;

public class CsvProcessingConfigTests
{
    [Fact]
    public void ProcessJobs_ShouldDefaultToEnabled()
    {
        // Guards a silent, total failure mode rather than a cosmetic default. A deployment that
        // doesn't split out worker pods sets nothing at all here, so if this ever flipped to false,
        // every such deployment would keep accepting CSV jobs and quietly never run any of them -
        // no error, no failed job, just work that sits in Queued forever.
        new CsvProcessingConfig()
            .ProcessJobs.Should()
            .BeTrue();
    }
}
