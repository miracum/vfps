using System.Text;
using Vfps.CsvProcessing;

namespace Vfps.Tests.CsvProcessingTests;

public class CsvFileEncodingTests
{
    [Theory]
    [InlineData(CsvFileEncoding.Utf8)]
    [InlineData(CsvFileEncoding.Utf16LittleEndian)]
    [InlineData(CsvFileEncoding.Utf16BigEndian)]
    [InlineData(CsvFileEncoding.Latin1)]
    [InlineData(CsvFileEncoding.Windows1252)]
    [InlineData(CsvFileEncoding.Ascii)]
    public void ToName_ShouldBeAnEncodingTheRuntimeCanDecodeAndRoundTrip(CsvFileEncoding encoding)
    {
        // The stored name is what the worker hands to Encoding.GetEncoding - if it didn't resolve,
        // every batch in this encoding would fail only once a worker picked it up.
        var name = CsvFileEncodings.ToName(encoding);

        var act = () => Encoding.GetEncoding(name);

        act.Should().NotThrow();
        CsvFileEncodings.FromName(name).Should().Be(encoding);
    }

    [Theory]
    [InlineData("UTF-8", CsvFileEncoding.Utf8)]
    [InlineData("Utf-8", CsvFileEncoding.Utf8)]
    [InlineData("latin1", CsvFileEncoding.Latin1)]
    [InlineData("unicodeFFFE", CsvFileEncoding.Utf16BigEndian)]
    [InlineData("cp1252", CsvFileEncoding.Windows1252)]
    public void FromName_ShouldMatchAnyNameForTheSameEncoding(string name, CsvFileEncoding expected)
    {
        // Batches created while the encoding was free text may carry any of these spellings.
        CsvFileEncodings.FromName(name).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-encoding")]
    [InlineData("utf-32")]
    public void FromName_WithANameOutsideTheEnum_ShouldReturnNull(string? name)
    {
        CsvFileEncodings.FromName(name).Should().BeNull();
    }

    [Fact]
    public void Windows1252_ShouldKeepTheCharactersLatin1DoesNot()
    {
        // The whole reason to offer it: Excel's "€" and typographic quotes are bytes 0x80-0x9F,
        // which ISO-8859-1 reads as invisible control codes.
        CsvFileEncodings.RegisterCodePageEncodings();
        var text = "id,betrag\n„A“,5 €\n";
        var encoding = Encoding.GetEncoding(CsvFileEncodings.ToName(CsvFileEncoding.Windows1252));

        using var output = new MemoryStream();
        using (var writer = CsvJobFormat.CreateWriter(output, encoding))
        {
            writer.Write(text);
        }

        output.ToArray().Should().Contain(0x80, "the euro sign is 0x80 in Windows-1252");
        using var reader = CsvJobFormat.CreateReader(new MemoryStream(output.ToArray()), encoding);
        reader.ReadToEnd().Should().Be(text);
        Encoding.Latin1.GetString(output.ToArray()).Should().NotBe(text);
    }
}
