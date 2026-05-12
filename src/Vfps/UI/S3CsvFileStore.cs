using Amazon.S3;
using Amazon.S3.Model;

namespace Vfps.UI;

/// <summary>
/// <see cref="ICsvFileStore"/> implementation backed by an Amazon S3-compatible object store
/// (including MinIO). Objects are stored in the bucket specified by <see cref="S3StorageConfig.BucketName"/>.
/// The storage key is the S3 object key (e.g. <c>vfps-csv/vfps_csv_&lt;guid&gt;_input.csv</c>).
/// </summary>
/// <remarks>
/// The S3 bucket referenced by <see cref="S3StorageConfig.BucketName"/> must already exist.
/// Bucket creation is the responsibility of the infrastructure administrator.
/// </remarks>
public sealed class S3CsvFileStore(IAmazonS3 s3Client, S3StorageConfig config, ILogger<S3CsvFileStore> logger)
    : ICsvFileStore
{
    public async Task<string> UploadAsync(
        Stream content,
        string suggestedFileName,
        CancellationToken cancellationToken
    )
    {
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
}
