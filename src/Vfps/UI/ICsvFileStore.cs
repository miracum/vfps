namespace Vfps.UI;

/// <summary>
/// Abstraction for storing and retrieving CSV files used during pseudonymization.
/// Implementations may use the local filesystem or an object store such as S3/MinIO.
/// </summary>
public interface ICsvFileStore
{
    /// <summary>
    /// Upload a stream of CSV content and return a storage key that can later be used to retrieve it.
    /// </summary>
    Task<string> UploadAsync(Stream content, string suggestedFileName, CancellationToken cancellationToken);

    /// <summary>
    /// Open a readable stream for the content identified by <paramref name="key"/>.
    /// Returns <c>null</c> if no object exists for the given key.
    /// </summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Returns <c>true</c> when an object identified by <paramref name="key"/> exists in the store.
    /// </summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);
}
