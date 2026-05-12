using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using Vfps.Data;
using Vfps.Data.Models;
using Vfps.PseudonymGenerators;

namespace Vfps.UI;

/// <summary>
/// Background service that processes CSV pseudonymization jobs from the <see cref="CsvJobService"/> queue.
/// Reads input CSVs row-by-row (streaming), pseudonymizes selected columns, and writes output CSVs.
/// Designed to handle very large files without buffering the entire content in memory.
/// </summary>
public class CsvPseudonymizationBackgroundService(
    CsvJobService jobService,
    ICsvFileStore fileStore,
    IServiceProvider serviceProvider,
    ILogger<CsvPseudonymizationBackgroundService> logger
) : BackgroundService
{
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in jobService.JobQueueReader.ReadAllAsync(stoppingToken))
        {
            var job = jobService.GetJob(jobId);
            if (job is null) continue;

            job.Status = CsvJobStatus.Processing;
            try
            {
                await ProcessJobAsync(job, stoppingToken);
                job.Status = CsvJobStatus.Done;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Status = CsvJobStatus.Failed;
                job.ErrorMessage = "Processing was cancelled due to service shutdown.";
                break;
            }
            catch (Exception ex)
            {
                job.Status = CsvJobStatus.Failed;
                job.ErrorMessage = ex.Message;
                logger.LogError(ex, "Failed to process CSV job {JobId}", jobId);
            }
        }
    }

    private async Task ProcessJobAsync(CsvPseudonymizationJob job, CancellationToken cancellationToken)
    {
        using var scope = serviceProvider.CreateScope();
        var namespaceRepository = scope.ServiceProvider.GetRequiredService<INamespaceRepository>();
        var pseudonymRepository = scope.ServiceProvider.GetRequiredService<IPseudonymRepository>();
        var lookup = scope.ServiceProvider.GetRequiredService<PseudonymizationMethodsLookup>();

        var @namespace = await namespaceRepository.FindAsync(job.NamespaceName, cancellationToken)
            ?? throw new InvalidOperationException($"Namespace '{job.NamespaceName}' not found.");

        var generator = lookup[@namespace.PseudonymGenerationMethod];

        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
        };

        using var inputStream =
            await fileStore.OpenReadAsync(job.InputKey, cancellationToken)
            ?? throw new InvalidOperationException($"Input file not found for key '{job.InputKey}'.");

        using var inputReader = new StreamReader(inputStream);
        using var csvReader = new CsvReader(inputReader, csvConfig);

        // Read headers
        await csvReader.ReadAsync();
        csvReader.ReadHeader();
        var headers = csvReader.HeaderRecord
            ?? throw new InvalidOperationException("CSV file has no header row.");

        var columnsToProcessSet = new HashSet<string>(job.ColumnsToProcess, StringComparer.OrdinalIgnoreCase);
        long rowsProcessed = 0;

        // Write pseudonymized output to a temp file, then upload to the file store.
        // Using a temp file avoids buffering the whole CSV in memory and works for
        // both small and large files, regardless of the storage backend.
        var tempOutputPath = Path.Combine(Path.GetTempPath(), $"vfps_out_{job.Id:N}.csv");
        try
        {
            await using (var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                useAsync: true
            ))
            await using (var outputWriter = new StreamWriter(outputStream))
            {
                using var csvWriter = new CsvWriter(outputWriter, csvConfig);

                // Write headers unchanged
                foreach (var header in headers)
                {
                    csvWriter.WriteField(header);
                }
                await csvWriter.NextRecordAsync();

                // rowPseudonyms holds the pseudonym candidates for all selected columns in the current row.
                // Each row's columns are persisted individually via CreateIfNotExist (which is an upsert).
                // This ensures correct deduplication semantics across replicas without requiring a bulk upsert API.
                var rowPseudonyms = new List<(string NamespaceName, string OriginalValue, string PseudonymValue)>(BatchSize);

                while (await csvReader.ReadAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var rowValues = new string[headers.Length];
                    for (int i = 0; i < headers.Length; i++)
                    {
                        rowValues[i] = csvReader.GetField(i) ?? string.Empty;
                    }

                    // Collect the pseudonym candidates for all selected columns in this row
                    rowPseudonyms.Clear();
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (columnsToProcessSet.Contains(headers[i]))
                        {
                            var originalValue = rowValues[i];
                            var pseudonymValue = @namespace.PseudonymPrefix
                                + generator.GeneratePseudonym(originalValue, @namespace.PseudonymLength)
                                + @namespace.PseudonymSuffix;
                            rowPseudonyms.Add((job.NamespaceName, originalValue, pseudonymValue));
                        }
                    }

                    // Persist pseudonyms for this row
                    var pseudonymMap = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (namespaceName, originalValue, pseudonymValue) in rowPseudonyms)
                    {
                        var stored = await pseudonymRepository.CreateIfNotExist(new Pseudonym
                        {
                            NamespaceName = namespaceName,
                            OriginalValue = originalValue,
                            PseudonymValue = pseudonymValue,
                            CreatedAt = DateTimeOffset.UtcNow,
                            LastUpdatedAt = DateTimeOffset.UtcNow,
                        });
                        pseudonymMap[originalValue] = stored?.PseudonymValue ?? pseudonymValue;
                    }

                    // Write output row
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (columnsToProcessSet.Contains(headers[i]) && pseudonymMap.TryGetValue(rowValues[i], out var pv))
                        {
                            csvWriter.WriteField(pv);
                        }
                        else
                        {
                            csvWriter.WriteField(rowValues[i]);
                        }
                    }
                    await csvWriter.NextRecordAsync();

                    rowsProcessed++;
                    if (rowsProcessed % BatchSize == 0)
                    {
                        job.RowsProcessed = rowsProcessed;
                        await outputWriter.FlushAsync(cancellationToken);
                        logger.LogDebug("Job {JobId}: processed {Rows} rows", job.Id, rowsProcessed);
                    }
                }

                job.RowsProcessed = rowsProcessed;
                job.TotalRows = rowsProcessed;
                await outputWriter.FlushAsync(cancellationToken);
            }

            // Upload the finished output file to the file store
            await using var tempReadStream = new FileStream(
                tempOutputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                useAsync: true
            );
            job.OutputKey = await fileStore.UploadAsync(
                tempReadStream,
                $"pseudonymized_{job.Id:N}.csv",
                cancellationToken
            );
        }
        finally
        {
            if (File.Exists(tempOutputPath))
                File.Delete(tempOutputPath);
        }
    }
}
