using Amazon.S3;
using Amazon.S3.Model;

namespace Vfps.UI;

/// <summary>
/// <see cref="ICsvFileStore"/> implementation backed by an Amazon S3-compatible object store
/// (including MinIO). Objects are stored in the bucket specified by <see cref="S3StorageConfig.BucketName"/>.
/// The storage key is the S3 object key (e.g. <c>vfps-csv/vfps_csv_&lt;guid&gt;_input.csv</c>).
/// </summary>
public sealed class S3CsvFileStore(IAmazonS3 s3Client, S3StorageConfig config, ILogger<S3CsvFileStore> logger)
    : ICsvFileStore
{
    public async Task<string> UploadAsync(
        Stream content,
        string suggestedFileName,
        CancellationToken cancellationToken
    )
    {
        await EnsureBucketExistsAsync(cancellationToken);

        var safeFileName = Path.GetFileName(suggestedFileName);
        var objectKey = $"vfps_csv_{Guid.NewGuid():N}_{safeFileName}";

        var request = new PutObjectRequest
        {
            BucketName = config.BucketName,
            Key = objectKey,
            InputStream = content,
            ContentType = "text/csv",
        };

        await s3Client.PutObjectAsync(request, cancellationToken);

        logger.LogDebug("Uploaded CSV file to S3 with key {Key} in bucket {Bucket}", objectKey, config.BucketName);
        return objectKey;
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await s3Client.GetObjectAsync(config.BucketName, key, cancellationToken);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await s3Client.GetObjectMetadataAsync(config.BucketName, key, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task EnsureBucketExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await s3Client.EnsureBucketExistsAsync(config.BucketName);
        }
        catch (AmazonS3Exception ex)
        {
            logger.LogWarning(ex, "Could not ensure S3 bucket '{Bucket}' exists: {Message}", config.BucketName, ex.Message);
        }
    }
}
