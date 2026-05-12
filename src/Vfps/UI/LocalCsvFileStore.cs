namespace Vfps.UI;

/// <summary>
/// <see cref="ICsvFileStore"/> implementation backed by the local filesystem.
/// Uploaded files are saved under <see cref="CsvJobsConfig.TempDirectory"/>.
/// The storage key is the absolute file path, so it is directly usable with
/// <see cref="System.IO.File"/> APIs in the background service.
/// </summary>
public sealed class LocalCsvFileStore(CsvJobsConfig config) : ICsvFileStore
{
    public async Task<string> UploadAsync(
        Stream content,
        string suggestedFileName,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(config.TempDirectory);
        var safeFileName = Path.GetFileName(suggestedFileName);
        var filePath = Path.Combine(config.TempDirectory, $"vfps_csv_{Guid.NewGuid():N}_{safeFileName}");

        await using var fs = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 65536,
            useAsync: true
        );
        await content.CopyToAsync(fs, cancellationToken);
        return filePath;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!File.Exists(key))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(
            key,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            useAsync: true
        );
        return Task.FromResult<Stream?>(stream);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(key));
}
