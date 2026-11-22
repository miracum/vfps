using System.Text;
using Vfps.CsvProcessing;

namespace Vfps.Tests.CsvProcessingTests;

public class CsvJobFormatWriterTests
{
    private static byte[] Write(string encodingName, string text)
    {
        using var output = new MemoryStream();
        using (var writer = CsvJobFormat.CreateWriter(output, Encoding.GetEncoding(encodingName)))
        {
            writer.Write(text);
        }

        return output.ToArray();
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    [InlineData("utf-32BE")]
    public void CreateWriter_WithAUnicodeEncoding_ShouldWriteNoByteOrderMark(string encodingName)
    {
        // Encoding.GetEncoding hands back instances that do emit one, which is exactly what a job
        // configured with these names gets - so the writer has to strip it itself.
        var encoding = Encoding.GetEncoding(encodingName);
        encoding.GetPreamble().Should().NotBeEmpty("otherwise this test proves nothing");

        var bytes = Write(encodingName, "id,value\n");

        bytes.Should().Equal(encoding.GetBytes("id,value\n"));
    }

    [Fact]
    public void CreateWriter_WithAnEncodingWithoutAByteOrderMark_ShouldWriteItUnchanged()
    {
        var bytes = Write("iso-8859-1", "id,wert\nä,ö\n");

        bytes.Should().Equal(Encoding.Latin1.GetBytes("id,wert\nä,ö\n"));
    }

    [Fact]
    public void CreateWriter_WithoutAByteOrderMark_ShouldStillBeReadBackIdentically()
    {
        var bytes = Write("utf-8", "id\nP-ä\n");

        using var reader = CsvJobFormat.CreateReader(new MemoryStream(bytes), Encoding.UTF8);
        reader.ReadToEnd().Should().Be("id\nP-ä\n");
    }
}
