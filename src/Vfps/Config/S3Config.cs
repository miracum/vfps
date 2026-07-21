namespace Vfps.Config;

/// <summary>
/// S3-compatible object storage used for CSV pseudonymization job input/output files. Off by
/// default, matching this codebase's existing optional-feature idiom - the CSV upload/download
/// UI and Hangfire job runner are only wired up when <see cref="IsEnabled"/> is true.
/// </summary>
public class S3Config
{
    public bool IsEnabled { get; set; }

    /// <summary>Endpoint URL, e.g. a local MinIO instance or a real AWS/S3-compatible endpoint.</summary>
    public string ServiceUrl { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Path-style addressing (https://host/bucket/key) rather than virtual-hosted-style
    /// (https://bucket.host/key) - required for MinIO and most non-AWS S3-compatible stores.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>How long presigned upload/download URLs remain valid.</summary>
    public TimeSpan PresignedUrlExpiry { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many days an uploaded CSV or its pseudonymized output is kept before an S3 bucket
    /// lifecycle rule expires (deletes) it. Applied on startup, scoped to the "csv-jobs/" prefix
    /// only. Job records themselves are never deleted, so without this the objects they
    /// reference - the original, unpseudonymized input included - would otherwise live in the
    /// bucket forever. Set to 0 to leave the bucket's lifecycle configuration untouched.
    /// </summary>
    public int ObjectRetentionDays { get; set; } = 30;

    /// <summary>
    /// Origins allowed to PUT/GET objects directly against the bucket via the presigned URLs
    /// handed out for CSV upload/download (see wwwroot/js/csvUpload.js). The browser talks to
    /// the bucket on a different origin than vfps itself is served from, so without this applied
    /// as an S3 bucket CORS rule on startup, the browser blocks the upload (preflighted because
    /// csvUpload.js sets a Content-Type header) before it ever reaches the bucket. Empty (the
    /// default) leaves the bucket's CORS configuration untouched - set this to the origin(s) the
    /// CSV upload UI is served from (e.g. "https://vfps.example.org") to enable it.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];
}
