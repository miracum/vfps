using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Vfps.UI;

/// <summary>
/// Manages in-memory CSV pseudonymization jobs and their processing queue.
/// </summary>
public class CsvJobService
{
    private readonly ConcurrentDictionary<Guid, CsvPseudonymizationJob> _jobs = new();
    private readonly Channel<Guid> _jobQueue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    /// <summary>Enqueue a new job and return it.</summary>
    public CsvPseudonymizationJob EnqueueJob(
        string inputFilePath,
        string[] columnsToProcess,
        string namespaceName
    )
    {
        var job = new CsvPseudonymizationJob
        {
            InputFilePath = inputFilePath,
            ColumnsToProcess = columnsToProcess,
            NamespaceName = namespaceName,
        };

        _jobs[job.Id] = job;
        _jobQueue.Writer.TryWrite(job.Id);
        return job;
    }

    /// <summary>Get a job by its ID, or null if not found.</summary>
    public CsvPseudonymizationJob? GetJob(Guid id) =>
        _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>All jobs, newest first.</summary>
    public IReadOnlyList<CsvPseudonymizationJob> GetAllJobs() =>
        _jobs.Values.OrderByDescending(j => j.Id).ToList();

    /// <summary>The channel reader used by the background processing service.</summary>
    public ChannelReader<Guid> JobQueueReader => _jobQueue.Reader;
}
