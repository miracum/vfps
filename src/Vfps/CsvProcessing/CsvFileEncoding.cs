using System.Text;

namespace Vfps.CsvProcessing;

/// <summary>
/// The character encodings a batch's file may be in. A closed set rather than a free-text name, so
/// a typo is impossible to submit rather than a failure discovered once a worker picks the batch
/// up. <see cref="Windows1252"/> needs the code-page provider registered - see
/// <see cref="CsvFileEncodings.RegisterCodePageEncodings"/>.
/// </summary>
public enum CsvFileEncoding
{
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    Latin1,

    /// <summary>
    /// The "ANSI" code page Excel on Windows exports CSV files in. ISO-8859-1 with printable
    /// characters - "€", typographic quotes and dashes among them - where ISO-8859-1 has control
    /// codes, so a file from Excel read as <see cref="Latin1"/> loses exactly those.
    /// </summary>
    Windows1252,
    Ascii,
}

/// <summary>
/// Converts between <see cref="CsvFileEncoding"/> and the encoding name a
/// <see cref="Data.Models.PseudonymizationJob"/> stores. The stored form stays a name, not the
/// enum, so batches created before the enum existed - with any name <see cref="Encoding.GetEncoding(string)"/>
/// accepted - still run.
/// </summary>
public static class CsvFileEncodings
{
    static CsvFileEncodings() => RegisterCodePageEncodings();

    /// <summary>
    /// Makes the Windows code pages - <see cref="CsvFileEncoding.Windows1252"/> among them -
    /// resolvable through <see cref="Encoding.GetEncoding(string)"/>, which .NET only does for the
    /// Unicode encodings, ASCII and Latin-1 by default. Called once at startup, since a worker
    /// decodes a job's stored encoding name without going through this class. Safe to call more
    /// than once: registering the same provider again is a no-op.
    /// </summary>
    public static void RegisterCodePageEncodings() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The name stored on a job, and understood by <see cref="Encoding.GetEncoding(string)"/>.</summary>
    public static string ToName(CsvFileEncoding encoding) =>
        encoding switch
        {
            CsvFileEncoding.Utf8 => "utf-8",
            CsvFileEncoding.Utf16LittleEndian => "utf-16",
            CsvFileEncoding.Utf16BigEndian => "utf-16BE",
            CsvFileEncoding.Latin1 => "iso-8859-1",
            CsvFileEncoding.Windows1252 => "windows-1252",
            CsvFileEncoding.Ascii => "us-ascii",
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null),
        };

    /// <summary>How the encoding is shown to a person - a technical name, the same in every language.</summary>
    public static string ToDisplayName(CsvFileEncoding encoding) =>
        encoding switch
        {
            CsvFileEncoding.Utf8 => "UTF-8",
            CsvFileEncoding.Utf16LittleEndian => "UTF-16 LE",
            CsvFileEncoding.Utf16BigEndian => "UTF-16 BE",
            CsvFileEncoding.Latin1 => "ISO-8859-1 (Latin-1)",
            CsvFileEncoding.Windows1252 => "Windows-1252",
            CsvFileEncoding.Ascii => "US-ASCII",
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null),
        };

    /// <summary>
    /// The enum member a stored name denotes, matched by what it resolves to rather than by
    /// spelling - "UTF-8" and "utf-8" are both <see cref="CsvFileEncoding.Utf8"/>, and "latin1" is
    /// <see cref="CsvFileEncoding.Latin1"/>. Null for a name that resolves to none of them, which
    /// only a job created before the enum existed can carry.
    /// </summary>
    public static CsvFileEncoding? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        int codePage;
        try
        {
            codePage = Encoding.GetEncoding(name).CodePage;
        }
        catch (ArgumentException)
        {
            return null;
        }

        return Enum.GetValues<CsvFileEncoding>()
            .Cast<CsvFileEncoding?>()
            .FirstOrDefault(e => Encoding.GetEncoding(ToName(e!.Value)).CodePage == codePage);
    }
}
