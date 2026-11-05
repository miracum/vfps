namespace Vfps.PlaywrightTests;

public class CsvJobsTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    [Fact]
    public async Task S3Disabled_ShowsWarningBanner()
    {
        // The default "test" compose profile (what CI runs against) leaves S3__IsEnabled unset,
        // since CSV pseudonymization needs a reachable S3-compatible object store (the "s3"
        // profile's SeaweedFS service). This just confirms the page degrades to a clear warning
        // rather than a broken/half-rendered form when that's the case.
        await GotoAsync("/ui/csv-jobs");

        await Expect(Page.Locator("body")).ToContainTextAsync("CSV pseudonymization is disabled");
    }

    [Fact]
    [Trait("Category", "RequiresS3")]
    public async Task HeaderRowChecked_PrefillsSourceColumnOptions()
    {
        // Requires the "s3" compose profile (SeaweedFS) in addition to "test" - S3__IsEnabled must
        // be true for this page to render the upload form at all. Not run by the default CI
        // job; run locally with `docker compose --profile test --profile s3 up -d --build` and
        // `dotnet test --filter Category=RequiresS3`.
        var csvPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
        await File.WriteAllTextAsync(csvPath, "patient_id,name,notes\n1,Jane Doe,none\n");
        try
        {
            await GotoAsync("/ui/csv-jobs?new=1");

            await Page.SetInputFilesAsync("#csvFileInput", csvPath);
            await Page.Locator(".file-format summary").ClickAsync();
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

    [Fact]
    [Trait("Category", "RequiresS3")]
    public async Task FileFormat_IsFoldedAway_WithWhatItIsSetToInPlainView()
    {
        await GotoAsync("/ui/csv-jobs?new=1");

        var summary = Page.Locator("#fileFormatSummary");
        await Expect(Page.Locator("#delimiter")).ToBeHiddenAsync();
        await Expect(summary).ToContainTextAsync("utf-8");

        await Page.Locator(".file-format summary").ClickAsync();
        await FillInputAsync("#delimiter", ";");
        await Page.Locator("#delimiter").BlurAsync();

        await Expect(summary).ToContainTextAsync("delimiter \";\"");
    }

    [Fact]
    [Trait("Category", "RequiresS3")]
    public async Task ADelimiterOfTwoCharacters_IsRefusedInTheForm_RatherThanFailingAJobLater()
    {
        var namespaceName = $"e2e-delimiter-{UniqueSuffix()}";
        var csvPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
        await File.WriteAllTextAsync(
            csvPath,
            "patient_id||name\n1||a\n",
            TestContext.Current.CancellationToken
        );
        try
        {
            await CreateNamespaceAsync(namespaceName);

            await GotoAsync("/ui/csv-jobs?new=1");
            await Page.SetInputFilesAsync("#csvFileInput", csvPath);
            await Page.Locator(".file-format summary").ClickAsync();
            await FillInputAsync("#delimiter", "||");
            await Page.Locator("#delimiter").BlurAsync();
            // A text box until the file's header row has been read, and only then a dropdown.
            await Page.Locator("select[id^=sourceColumn-]")
                .SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await Page.Locator("[id^=namespace-]").SelectOptionAsync(namespaceName);
            await Page.ClickAsync("#submitJobButton");

            await Expect(Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Alert))
                .ToContainTextAsync("single ASCII character");
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    [Trait("Category", "RequiresS3")]
    public async Task AFailedJobsReason_WrapsInsteadOfWideningTheGrid()
    {
        // The grid used to be as wide as the longest error message, with the job's actions at the
        // far end of it: 2235px with a real one, so Download was off screen on any monitor.
        var namespaceName = $"e2e-failing-{UniqueSuffix()}";
        var csvPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
        await File.WriteAllTextAsync(
            csvPath,
            "patient_id\nnot-in-the-namespace\n",
            TestContext.Current.CancellationToken
        );
        try
        {
            await CreateNamespaceAsync(namespaceName);

            await GotoAsync("/ui/csv-jobs?new=1");
            await Page.SelectOptionAsync("#pseudonymizeMode", "FailIfMissing");
            await Page.SetInputFilesAsync("#csvFileInput", csvPath);
            // A text box until the file's header row has been read, and only then a dropdown.
            await Page.Locator("select[id^=sourceColumn-]").SelectOptionAsync("patient_id");
            await Page.Locator("[id^=namespace-]").SelectOptionAsync(namespaceName);
            await Page.ClickAsync("#submitJobButton");

            var row = Page.Locator("tr")
                .Filter(new LocatorFilterOptions { HasText = Path.GetFileName(csvPath) })
                .First;
            await Expect(row).ToContainTextAsync("Failed", new() { Timeout = 60_000 });

            foreach (var width in new[] { 1440, 1024, 390 })
            {
                await Page.SetViewportSizeAsync(width, 900);
                var overflow = await Page.Locator(".jobs-grid table")
                    .EvaluateAsync<int>(
                        "t => t.parentElement.scrollWidth - t.parentElement.clientWidth"
                    );
                overflow.Should().Be(0, $"the grid should fit at {width}px");
            }
        }
        finally
        {
            File.Delete(csvPath);
        }
    }
}
