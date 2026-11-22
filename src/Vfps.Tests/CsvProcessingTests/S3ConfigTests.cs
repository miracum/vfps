using Vfps.Config;

namespace Vfps.Tests.CsvProcessingTests;

public class S3ConfigTests
{
    [Theory]
    [InlineData("csv-jobs/", "csv-jobs/")]
    [InlineData("batches", "batches/")]
    [InlineData("/batches/", "batches/")]
    [InlineData("vfps/batches", "vfps/batches/")]
    [InlineData(" batches// ", "batches/")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void NormalizedObjectKeyPrefix_ShouldHaveExactlyOneTrailingSlashOrBeEmpty(
        string configured,
        string expected
    )
    {
        new S3Config { ObjectKeyPrefix = configured }
            .NormalizedObjectKeyPrefix.Should()
            .Be(expected);
    }

    [Fact]
    public void ObjectKeyPrefix_ShouldDefaultToWhereExistingDeploymentsStoreTheirObjects()
    {
        // Changing the default would silently move every deployment's lifecycle rule off the
        // objects it already holds.
        new S3Config()
            .NormalizedObjectKeyPrefix.Should()
            .Be("csv-jobs/");
    }
}
