namespace Vfps.PlaywrightTests;

public class CsvJobsTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    [Fact]
    public async Task S3Disabled_ShowsWarningBanner()
    {
        // The default "test" compose profile (what CI runs against) leaves S3__IsEnabled unset,
        // since CSV pseudonymization needs a reachable S3-compatible object store (the "s3"
        // profile's MinIO service). This just confirms the page degrades to a clear warning
        // rather than a broken/half-rendered form when that's the case.
        await GotoAsync("/ui/csv-jobs");

        await Expect(Page.Locator("body")).ToContainTextAsync("CSV pseudonymization is disabled");
    }

    [Fact]
    [Trait("Category", "RequiresS3")]
    public async Task HeaderRowChecked_PrefillsSourceColumnOptions()
    {
        // Requires the "s3" compose profile (MinIO) in addition to "test" - S3__IsEnabled must
        // be true for this page to render the upload form at all. Not run by the default CI
        // job; run locally with `docker compose --profile test --profile s3 up -d --build` and
        // `dotnet test --filter Category=RequiresS3`.
        var csvPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
        await File.WriteAllTextAsync(csvPath, "patient_id,name,notes\n1,Jane Doe,none\n");
        try
        {
            await GotoAsync("/ui/csv-jobs");

            await Page.SetInputFilesAsync("#csvFileInput", csvPath);
            await Page.CheckAsync("#hasHeaderRow");

            var sourceColumnSelect = Page.GetByLabel("Source column");
            await Expect(sourceColumnSelect).ToBeVisibleAsync();

            var optionTexts = await sourceColumnSelect.Locator("option").AllInnerTextsAsync();
            optionTexts.Should().Contain(["patient_id", "name", "notes"]);
        }
        finally
        {
            File.Delete(csvPath);
        }
    }
}
