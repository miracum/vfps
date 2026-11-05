using System.Text;
using CsvHelper;
using FakeItEasy;
using Microsoft.Extensions.Logging.Abstractions;
using Vfps.CsvProcessing;
using Vfps.Data;
using Vfps.Data.Models;

namespace Vfps.Tests.CsvProcessingTests;

/// <summary>
/// What the options in <see cref="CsvJobFormat"/> have to keep true of a job's file. The runner's
/// tests check which values get resolved rather than the bytes written, and several of these are
/// library defaults that would change a file without failing anything: CsvHelper 34 changed three
/// of them compared with 33, which is why each one is pinned here by what it protects.
/// </summary>
public class CsvJobFormatOptionsTests
{
    private static PseudonymizationJob Job(string delimiter = ",", bool hasHeaderRow = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            CreatedBy = "test-user",
            Delimiter = delimiter,
            HasHeaderRow = hasHeaderRow,
        };

    private static async Task<(
        IReadOnlyList<string> Header,
        List<string[]> Rows,
        int Malformed
    )> ReadAsync(string csv, PseudonymizationJob job)
    {
        var progress = new CsvJobProgressReporter(
            A.Fake<IPseudonymizationJobRepository>(),
            job.Id,
            new CsvJobPhaseTimer(job.Direction),
            TimeSpan.Zero
        );
        var options = CsvJobFormat.CreateReaderOptions(job, progress, NullLogger.Instance);

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        using var text = CsvJobFormat.CreateReader(input, Encoding.UTF8);
        using var reader = CsvReader.Create(text, options, leaveOpen: true);

        IReadOnlyList<string> header = [];
        if (job.HasHeaderRow)
        {
            await reader.ReadHeaderAsync(TestContext.Current.CancellationToken);
            header = reader.Headers;
        }

        var rows = new List<string[]>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.GetString(i);
            }

            rows.Add(row);
        }

        return (header, rows, progress.BadDataRowCount);
    }

    private static async Task<string> WriteAsync(string[][] rows, string delimiter = ",")
    {
        var output = new StringWriter();
        await using (
            var writer = CsvWriter.Create(
                output,
                CsvJobFormat.CreateWriterOptions(Job(delimiter)),
                leaveOpen: true
            )
        )
        {
            foreach (var row in rows)
            {
                foreach (var field in row)
                {
                    writer.WriteField(field);
                }

                await writer.EndRecordAsync(TestContext.Current.CancellationToken);
            }
        }

        return output.ToString();
    }

    [Fact]
    public async Task Writer_ShouldPassThroughRowsOfDifferentLengths()
    {
        // The library's default throws on the second row, which would fail any job over a file
        // whose rows are uneven - and those are passed through deliberately.
        var written = await WriteAsync([
            ["a", "b", "c"],
            ["1"],
            ["1", "2"],
        ]);

        written.Should().Be("a,b,c\r\n1\r\n1,2\r\n");
    }

    [Theory]
    [InlineData(" a", "\" a\"\r\n")]
    [InlineData("a ", "\"a \"\r\n")]
    public async Task Writer_ShouldQuoteAValueWithSurroundingSpaces(string value, string expected)
    {
        // Not quoted by the library's default, leaving the value open to any reader that trims.
        var written = await WriteAsync([
            [value],
        ]);

        written.Should().Be(expected);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("@x")]
    [InlineData("+x")]
    [InlineData("-x")]
    public async Task Writer_ShouldLeaveFormulaLikeValuesExactlyAsTheyAre(string value)
    {
        // A pseudonym or an original value is data to hand back unchanged. Escaping it for
        // spreadsheets, which the library can do, would alter what the file holds.
        var written = await WriteAsync([
            [value],
        ]);

        written.Should().Be($"{value}\r\n");
    }

    [Fact]
    public async Task ARowWhoseOnlyFieldIsEmpty_ShouldSurviveBeingWrittenAndReadBack()
    {
        // As a blank line it would be skipped when the file is read again, one row short.
        var written = await WriteAsync([
            ["id"],
            [""],
            ["1"],
        ]);

        var (header, rows, _) = await ReadAsync(written, Job());

        header.Should().Equal("id");
        rows.Should().HaveCount(2);
        rows[0].Should().Equal("");
        rows[1].Should().Equal("1");
    }

    [Theory]
    [InlineData("with,comma")]
    [InlineData("with\"quote")]
    [InlineData("with\nnewline")]
    [InlineData("with\r\ncrlf")]
    [InlineData(" spaces ")]
    [InlineData("\ttabs\t")]
    [InlineData("Müller ß € 😀")]
    public async Task AnAwkwardValue_ShouldReadBackExactlyAsItWasWritten(string value)
    {
        var written = await WriteAsync([
            ["id", "value"],
            ["1", value],
        ]);

        var (_, rows, malformed) = await ReadAsync(written, Job());

        rows.Should().ContainSingle().Which.Should().Equal("1", value);
        malformed.Should().Be(0);
    }

    [Fact]
    public async Task Reader_ShouldUseTheJobsDelimiterRatherThanDetectingOne()
    {
        // Detection is the library's default, and this file looks semicolon-separated to it.
        var (header, rows, _) = await ReadAsync("a;b;c,d\n1;2;3,4\n", Job());

        header.Should().Equal("a;b;c", "d");
        rows.Should().ContainSingle().Which.Should().Equal("1;2;3", "4");
    }

    [Fact]
    public async Task Reader_ShouldNotDropASepHintLine()
    {
        // The library's default takes this line, written by Excel, out of the file altogether.
        var (header, rows, _) = await ReadAsync("sep=;\nid;value\n1;a\n", Job(delimiter: ";"));

        header.Should().Equal("sep=", "");
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Reader_ShouldKeepMalformedFieldsAsTheyAre_AndCountTheRowOnce()
    {
        var (_, rows, malformed) = await ReadAsync("id,a,b\n1,x\"y,p\"q\n2,ok,ok\n", Job());

        rows.Should().HaveCount(2);
        rows[0].Should().Equal("1", "x\"y", "p\"q");
        malformed.Should().Be(1);
    }

    [Fact]
    public async Task Reader_ShouldKeepSurroundingWhitespace_AndLinesOfOnlyWhitespace()
    {
        var (_, rows, _) = await ReadAsync("id,value\n1, a \n\n   \n2,b\n", Job());

        rows.Should().HaveCount(3);
        rows[0].Should().Equal("1", " a ");
        rows[1].Should().Equal("   ");
        rows[2].Should().Equal("2", "b");
    }

    [Fact]
    public async Task Reader_ShouldReportEachRowsOwnFieldCount()
    {
        var (_, rows, malformed) = await ReadAsync("a,b\n1\n1,2,3\n", Job());

        rows[0].Should().Equal("1");
        rows[1].Should().Equal("1", "2", "3");
        malformed.Should().Be(0);
    }

    [Theory]
    [InlineData(",", ',')]
    [InlineData(";", ';')]
    [InlineData("|", '|')]
    [InlineData("\t", '\t')]
    [InlineData(" ", ' ')]
    public void ParseDelimiter_ShouldAcceptASingleAsciiCharacter(string delimiter, char expected)
    {
        CsvJobFormat.ParseDelimiter(delimiter).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("||")]
    [InlineData("§")]
    [InlineData("\"")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void ParseDelimiter_ShouldRefuseAnythingElse(string delimiter)
    {
        var parse = () => CsvJobFormat.ParseDelimiter(delimiter);

        parse.Should().Throw<InvalidOperationException>().WithMessage("*single ASCII character*");
    }
}
