namespace Vfps.UI;

/// <summary>
/// Configuration options for the web UI.
/// </summary>
public class UiConfig
{
    /// <summary>Whether the web UI is enabled. Defaults to true.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>CSV job settings.</summary>
    public CsvJobsConfig CsvJobs { get; set; } = new();
}

/// <summary>
/// Configuration for CSV pseudonymization jobs.
/// </summary>
public class CsvJobsConfig
{
    /// <summary>Maximum allowed upload file size in megabytes. Defaults to 512 MB.</summary>
    public int MaxFileSizeMb { get; set; } = 512;

    private string _tempDirectory = string.Empty;

    /// <summary>
    /// Directory where uploaded and output CSV files are temporarily stored when using the local
    /// filesystem store. Defaults to <see cref="Path.GetTempPath"/> when not set or set to an empty string.
    /// Not used when <see cref="S3"/> storage is enabled.
    /// </summary>
    public string TempDirectory
    {
        get => string.IsNullOrWhiteSpace(_tempDirectory) ? Path.GetTempPath() : _tempDirectory;
        set => _tempDirectory = value;
    }

    /// <summary>S3/MinIO storage configuration. When <see cref="S3StorageConfig.IsEnabled"/> is
    /// <c>true</c>, uploaded CSVs and pseudonymized output are stored in S3 instead of the local
    /// filesystem.</summary>
    public S3StorageConfig S3 { get; set; } = new();
}

/// <summary>
/// Configuration for the S3/MinIO storage backend used to store uploaded and processed CSV files.
/// </summary>
public class S3StorageConfig
{
    /// <summary>
    /// Whether to use S3/MinIO as the CSV storage backend.
    /// When <c>false</c> (the default) the local filesystem is used instead.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Custom service URL for S3-compatible endpoints such as MinIO
    /// (e.g. <c>http://minio:9000</c>). Leave empty to use the default AWS endpoint.
    /// </summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>The S3 bucket that will hold all CSV files. Defaults to <c>vfps-csv</c>.</summary>
    public string BucketName { get; set; } = "vfps-csv";

    /// <summary>AWS / MinIO access key ID.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>AWS / MinIO secret access key.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// AWS region. Defaults to <c>us-east-1</c>. For MinIO this can usually be left at its default.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Force path-style URL format (<c>http://host/bucket/key</c>) instead of
    /// virtual-hosted–style (<c>http://bucket.host/key</c>).
    /// Required for MinIO. Defaults to <c>true</c>.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;
}
