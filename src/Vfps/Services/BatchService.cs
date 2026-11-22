using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vfps.AppServices;
using Vfps.Authorization;
using Vfps.CsvProcessing;
using Vfps.Data.Models;
using Vfps.Protos;

namespace Vfps.Services;

/// <summary>
/// The API surface of CSV jobs, called batches here - a thin translation onto
/// <see cref="IPseudonymizationJobAppService"/>, which the admin UI drives as well. See
/// batches.proto for the flow a client follows.
/// </summary>
public class BatchService(IPseudonymizationJobAppService jobAppService)
    : Protos.BatchService.BatchServiceBase
{
    private const string DefaultDelimiter = ",";

    /// <inheritdoc/>
    public override async Task<BatchServiceCreateResponse> Create(
        BatchServiceCreateRequest request,
        ServerCallContext context
    )
    {
        var user = context.GetUser();
        var originalFileName = string.IsNullOrEmpty(request.OriginalFileName)
            ? null
            : request.OriginalFileName;

        try
        {
            switch (request.KindCase)
            {
                case BatchServiceCreateRequest.KindOneofCase.Export:
                {
                    var spec = request.Export;
                    if (string.IsNullOrEmpty(spec.Namespace))
                    {
                        throw InvalidArgument("export.namespace is required.");
                    }

                    var (encoding, delimiter, _) = ResolveFormat(spec.Format);
                    var job = await jobAppService.CreateExportJobAsync(
                        new CreateCsvExportJobRequest(
                            spec.Namespace,
                            encoding,
                            delimiter,
                            OrDefault(spec.OriginalValueColumn, CsvNamespaceColumns.OriginalValue),
                            OrDefault(spec.PseudonymValueColumn, CsvNamespaceColumns.PseudonymValue)
                        ),
                        user,
                        context.CancellationToken
                    );

                    return new BatchServiceCreateResponse { Batch = ToProto(job) };
                }

                case BatchServiceCreateRequest.KindOneofCase.Transform:
                case BatchServiceCreateRequest.KindOneofCase.Import:
                {
                    var (job, upload) = await jobAppService.CreateJobAsync(
                        request.KindCase == BatchServiceCreateRequest.KindOneofCase.Transform
                            ? ToTransformRequest(request.Transform, originalFileName)
                            : ToImportRequest(request.Import, originalFileName),
                        user,
                        context.CancellationToken
                    );

                    return new BatchServiceCreateResponse
                    {
                        Batch = ToProto(job),
                        Upload = ToProto(upload),
                    };
                }

                default:
                    throw InvalidArgument(
                        "Exactly one of transform, import or export must be set."
                    );
            }
        }
        catch (ForbiddenException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    /// <inheritdoc/>
    public override async Task<BatchServiceGetResponse> Get(
        BatchServiceGetRequest request,
        ServerCallContext context
    )
    {
        var batchId = ParseBatchId(request.BatchId);
        var user = context.GetUser();

        return await TranslateErrorsAsync(async () =>
        {
            var job = await jobAppService.PollAsync(batchId, user, context.CancellationToken);
            var response = new BatchServiceGetResponse { Batch = ToProto(job) };

            if (job.Status == PseudonymizationJobStatus.Completed)
            {
                var download = await jobAppService.GetDownloadUrlAsync(
                    batchId,
                    user,
                    context.CancellationToken
                );
                response.Download = ToProto(download);
            }

            return response;
        });
    }

    /// <inheritdoc/>
    public override async Task<BatchServiceListResponse> List(
        BatchServiceListRequest request,
        ServerCallContext context
    )
    {
        if (request.PageSize < 0)
        {
            throw InvalidArgument("page_size must not be negative.");
        }

        return await TranslateErrorsAsync(async () =>
        {
            var page = await jobAppService.ListPageAsync(
                context.GetUser(),
                request.PageSize,
                request.PageToken,
                context.CancellationToken
            );

            var response = new BatchServiceListResponse
            {
                NextPageToken = page.NextPageToken ?? string.Empty,
            };
            response.Batches.AddRange(page.Jobs.Select(ToProto));
            return response;
        });
    }

    /// <inheritdoc/>
    public override async Task<BatchServiceCancelResponse> Cancel(
        BatchServiceCancelRequest request,
        ServerCallContext context
    )
    {
        var batchId = ParseBatchId(request.BatchId);
        var user = context.GetUser();

        return await TranslateErrorsAsync(async () =>
        {
            var job = await jobAppService.CancelAsync(batchId, user, context.CancellationToken);
            return new BatchServiceCancelResponse { Batch = ToProto(job) };
        });
    }

    /// <inheritdoc/>
    public override async Task<BatchServiceDeleteResponse> Delete(
        BatchServiceDeleteRequest request,
        ServerCallContext context
    )
    {
        var batchId = ParseBatchId(request.BatchId);

        return await TranslateErrorsAsync(async () =>
        {
            await jobAppService.DeleteAsync(batchId, context.GetUser(), context.CancellationToken);
            return new BatchServiceDeleteResponse();
        });
    }

    private static async Task<T> TranslateErrorsAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (PseudonymizationJobNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (ForbiddenException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    private static CreateCsvJobRequest ToTransformRequest(
        BatchTransformSpec spec,
        string? originalFileName
    )
    {
        var direction = spec.Direction switch
        {
            BatchTransformDirection.Pseudonymize => PseudonymizationJobDirection.Pseudonymize,
            BatchTransformDirection.Depseudonymize => PseudonymizationJobDirection.Depseudonymize,
            _ => throw InvalidArgument(
                "transform.direction must be BATCH_TRANSFORM_DIRECTION_PSEUDONYMIZE or "
                    + "BATCH_TRANSFORM_DIRECTION_DEPSEUDONYMIZE."
            ),
        };

        var pseudonymizeMode = spec.PseudonymizeMode switch
        {
            BatchPseudonymizeMode.Unspecified or BatchPseudonymizeMode.CreateIfMissing =>
                PseudonymizeMode.CreateIfMissing,
            BatchPseudonymizeMode.FailIfMissing => PseudonymizeMode.FailIfMissing,
            BatchPseudonymizeMode.KeepIfMissing => PseudonymizeMode.KeepIfMissing,
            _ => throw InvalidArgument("transform.pseudonymize_mode is not a known mode."),
        };

        if (spec.ColumnMappings.Count == 0)
        {
            throw InvalidArgument("transform.column_mappings must name at least one column.");
        }

        if (
            spec.ColumnMappings.Any(m =>
                string.IsNullOrEmpty(m.SourceColumn) || string.IsNullOrEmpty(m.Namespace)
            )
        )
        {
            throw InvalidArgument(
                "Every entry of transform.column_mappings needs a source_column and a namespace."
            );
        }

        var (encoding, delimiter, hasHeaderRow) = ResolveFormat(spec.Format);

        return new CreateCsvJobRequest(
            encoding,
            delimiter,
            hasHeaderRow,
            [
                .. spec.ColumnMappings.Select(m => new ColumnMapping
                {
                    SourceColumn = m.SourceColumn,
                    TargetColumn = string.IsNullOrEmpty(m.TargetColumn) ? null : m.TargetColumn,
                    Namespace = m.Namespace,
                }),
            ],
            direction,
            originalFileName,
            pseudonymizeMode
        );
    }

    private static CreateCsvJobRequest ToImportRequest(
        BatchImportSpec spec,
        string? originalFileName
    )
    {
        var hasNamespace = !string.IsNullOrEmpty(spec.Namespace);
        var hasNamespaceColumn = !string.IsNullOrEmpty(spec.NamespaceColumn);
        if (hasNamespace == hasNamespaceColumn)
        {
            throw InvalidArgument(
                "Exactly one of import.namespace and import.namespace_column must be set."
            );
        }

        var (encoding, delimiter, hasHeaderRow) = ResolveFormat(spec.Format);

        return new CreateCsvJobRequest(
            encoding,
            delimiter,
            hasHeaderRow,
            [
                new ColumnMapping
                {
                    SourceColumn = OrDefault(
                        spec.OriginalValueColumn,
                        CsvNamespaceColumns.OriginalValue
                    ),
                    TargetColumn = OrDefault(
                        spec.PseudonymValueColumn,
                        CsvNamespaceColumns.PseudonymValue
                    ),
                    // Stored empty when the namespace comes from a column, as the UI does - see
                    // ColumnMapping.NamespaceColumn.
                    Namespace = hasNamespace ? spec.Namespace : string.Empty,
                    NamespaceColumn = hasNamespaceColumn ? spec.NamespaceColumn : null,
                },
            ],
            PseudonymizationJobDirection.Import,
            originalFileName
        );
    }

    /// <summary>
    /// Applies the defaults and rejects what the batch would only fail on once a worker picked it
    /// up - by which point the client has already uploaded the whole file.
    /// </summary>
    private static (CsvFileEncoding Encoding, string Delimiter, bool HasHeaderRow) ResolveFormat(
        CsvFormat? format
    )
    {
        var encoding = (format?.Encoding ?? CsvEncoding.Unspecified) switch
        {
            CsvEncoding.Unspecified or CsvEncoding.Utf8 => CsvFileEncoding.Utf8,
            CsvEncoding.Utf16Le => CsvFileEncoding.Utf16LittleEndian,
            CsvEncoding.Utf16Be => CsvFileEncoding.Utf16BigEndian,
            CsvEncoding.Iso88591 => CsvFileEncoding.Latin1,
            CsvEncoding.UsAscii => CsvFileEncoding.Ascii,
            CsvEncoding.Windows1252 => CsvFileEncoding.Windows1252,
            var unknown => throw InvalidArgument(
                $"format.encoding {unknown} is not a known encoding."
            ),
        };
        var delimiter = OrDefault(format?.Delimiter, DefaultDelimiter);
        var hasHeaderRow = format is null || !format.HasHasHeaderRow || format.HasHeaderRow;

        try
        {
            CsvJobFormat.ParseDelimiter(delimiter);
        }
        catch (InvalidOperationException ex)
        {
            throw InvalidArgument($"format.delimiter: {ex.Message}");
        }

        return (encoding, delimiter, hasHeaderRow);
    }

    private static string OrDefault(string? value, string defaultValue) =>
        string.IsNullOrEmpty(value) ? defaultValue : value;

    private static Guid ParseBatchId(string batchId) =>
        Guid.TryParse(batchId, out var id)
            ? id
            : throw InvalidArgument("batch_id must be the id of a batch, as returned by Create.");

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    private static PresignedRequest ToProto(PresignedUrl url)
    {
        var request = new PresignedRequest
        {
            Method = url.Method,
            Uri = url.Url,
            ExpiresAt = Timestamp.FromDateTimeOffset(url.ExpiresAt),
        };
        foreach (var (name, value) in url.Headers)
        {
            request.Headers[name] = value;
        }

        return request;
    }

    internal static Batch ToProto(PseudonymizationJob job)
    {
        var batch = new Batch
        {
            Id = job.Id.ToString(),
            State = ToProto(job.Status),
            OriginalFileName = job.OriginalFileName ?? string.Empty,
            ErrorMessage = job.ErrorMessage ?? string.Empty,
            CreatedBy = job.CreatedBy,
            Meta = new Meta
            {
                CreatedAt = Timestamp.FromDateTimeOffset(job.CreatedAt),
                LastUpdatedAt = Timestamp.FromDateTimeOffset(job.LastUpdatedAt),
            },
            Progress = new BatchProgress
            {
                TotalBytes = job.TotalBytes,
                BytesProcessed = job.BytesProcessed,
                RowsProcessed = job.RowsProcessed,
                BadDataRowCount = job.BadDataRowCount,
                MissingValueCount = job.MissingValueCount,
                UnresolvedValueCount = job.UnresolvedValueCount,
            },
        };

        var format = new CsvFormat
        {
            Encoding = CsvFileEncodings.FromName(job.Encoding) switch
            {
                CsvFileEncoding.Utf8 => CsvEncoding.Utf8,
                CsvFileEncoding.Utf16LittleEndian => CsvEncoding.Utf16Le,
                CsvFileEncoding.Utf16BigEndian => CsvEncoding.Utf16Be,
                CsvFileEncoding.Latin1 => CsvEncoding.Iso88591,
                CsvFileEncoding.Ascii => CsvEncoding.UsAscii,
                CsvFileEncoding.Windows1252 => CsvEncoding.Windows1252,
                _ => CsvEncoding.Unspecified,
            },
            Delimiter = job.Delimiter,
            HasHeaderRow = job.HasHeaderRow,
        };

        switch (job.Direction)
        {
            case PseudonymizationJobDirection.Import:
            {
                var mapping = job.ColumnMappings.Single();
                batch.Import = new BatchImportSpec
                {
                    Namespace = mapping.Namespace,
                    NamespaceColumn = mapping.NamespaceColumn ?? string.Empty,
                    Format = format,
                    OriginalValueColumn = mapping.SourceColumn,
                    PseudonymValueColumn = mapping.TargetColumn ?? string.Empty,
                };
                break;
            }

            case PseudonymizationJobDirection.Export:
            {
                var mapping = job.ColumnMappings.Single();
                batch.Export = new BatchExportSpec
                {
                    Namespace = mapping.Namespace,
                    Format = format,
                    OriginalValueColumn = mapping.SourceColumn,
                    PseudonymValueColumn = mapping.TargetColumn ?? string.Empty,
                };
                break;
            }

            default:
            {
                var transform = new BatchTransformSpec
                {
                    Direction =
                        job.Direction == PseudonymizationJobDirection.Depseudonymize
                            ? BatchTransformDirection.Depseudonymize
                            : BatchTransformDirection.Pseudonymize,
                    Format = format,
                    PseudonymizeMode =
                        job.Direction == PseudonymizationJobDirection.Depseudonymize
                            ? BatchPseudonymizeMode.Unspecified
                            : job.PseudonymizeMode switch
                            {
                                PseudonymizeMode.FailIfMissing =>
                                    BatchPseudonymizeMode.FailIfMissing,
                                PseudonymizeMode.KeepIfMissing =>
                                    BatchPseudonymizeMode.KeepIfMissing,
                                _ => BatchPseudonymizeMode.CreateIfMissing,
                            },
                };
                transform.ColumnMappings.AddRange(
                    job.ColumnMappings.Select(m => new BatchColumnMapping
                    {
                        SourceColumn = m.SourceColumn,
                        TargetColumn = m.TargetColumn ?? string.Empty,
                        Namespace = m.Namespace,
                    })
                );
                batch.Transform = transform;
                break;
            }
        }

        return batch;
    }

    private static BatchState ToProto(PseudonymizationJobStatus status) =>
        status switch
        {
            PseudonymizationJobStatus.AwaitingUpload => BatchState.AwaitingUpload,
            PseudonymizationJobStatus.Queued => BatchState.Queued,
            PseudonymizationJobStatus.Running => BatchState.Running,
            PseudonymizationJobStatus.Completed => BatchState.Completed,
            PseudonymizationJobStatus.Failed => BatchState.Failed,
            PseudonymizationJobStatus.Cancelled => BatchState.Cancelled,
            PseudonymizationJobStatus.Stalled => BatchState.Stalled,
            _ => BatchState.Unspecified,
        };
}
