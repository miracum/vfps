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
    /// Directory where uploaded and output CSV files are stored.
    /// Defaults to <see cref="Path.GetTempPath"/> when not set or set to an empty string.
    /// </summary>
    public string TempDirectory
    {
        get => string.IsNullOrWhiteSpace(_tempDirectory) ? Path.GetTempPath() : _tempDirectory;
        set => _tempDirectory = value;
    }
}
