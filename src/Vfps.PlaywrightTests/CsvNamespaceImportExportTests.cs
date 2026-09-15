namespace Vfps.PlaywrightTests;

/// <summary>
/// End-to-end round trip for the namespace import/export directions: a CSV of original/pseudonym
/// pairs goes in, the namespace is checked for what landed, and the export comes back out with
/// the same pairs.
///
/// Every test here needs the "s3" compose profile (SeaweedFS) on top of "test" - without
/// S3__IsEnabled the CSV jobs page renders a warning banner instead of a form. Not run by the
/// default CI job; run locally with `docker compose --profile test --profile s3 up -d --build`
/// and `dotnet test --filter Category=RequiresS3`.
/// </summary>
[Trait("Category", "RequiresS3")]
public class CsvNamespaceImportExportTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    // Generous: the job has to be picked up by a Hangfire worker, run, and then be noticed by the
    // jobs grid's own 2s poll - none of which is instant on a loaded machine.
    private const float JobTimeoutMs = 60_000;

    [Fact]
    public async Task ExportDirection_ReplacesTheFileFormWithANamespacePicker()
    {
        await GotoAsync("/ui/csv-jobs");

        await Expect(Page.Locator("#csvFileInput")).ToBeVisibleAsync();

        await Page.SelectOptionAsync("#direction", "Export");

        // An export has no file to upload and no header row to declare - only the namespace to
        // read, plus the output's own encoding/delimiter.
        await Expect(Page.Locator("#csvFileInput")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#hasHeaderRow")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#jobNamespace")).ToBeVisibleAsync();
        await Expect(Page.Locator("#submitJobButton")).ToContainTextAsync("Start export");
    }

    [Fact]
    public async Task ImportDirection_DefaultsToTheColumnNamesAnExportWrites()
    {
        await GotoAsync("/ui/csv-jobs");
        await Page.SelectOptionAsync("#direction", "Import");

        // An export file imports back without the operator restating either column name.
        await Expect(Page.Locator("#originalValueColumn")).ToHaveValueAsync("original");
        await Expect(Page.Locator("#pseudonymValueColumn")).ToHaveValueAsync("pseudonym");
        await Expect(Page.Locator("#csvFileInput")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ImportThenExport_RoundTripsThePairsThroughTheNamespace()
    {
        var namespaceName = $"e2e-import-{UniqueSuffix()}";
        var pairs = new[] { ("alice", "psn-alice"), ("bob", "psn-bob"), ("carol", "psn-carol") };
        var csvPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
        await File.WriteAllTextAsync(
            csvPath,
            "original,pseudonym\n" + string.Concat(pairs.Select(p => $"{p.Item1},{p.Item2}\n"))
        );

        try
        {
            await CreateNamespaceAsync(namespaceName);

            await GotoAsync("/ui/csv-jobs");
            await Page.SelectOptionAsync("#direction", "Import");
            await Page.SetInputFilesAsync("#csvFileInput", csvPath);
            await Page.SelectOptionAsync("#jobNamespace", namespaceName);
            await Page.ClickAsync("#submitJobButton");

            var importRow = JobRow(Path.GetFileName(csvPath));
            await Expect(importRow).ToContainTextAsync("Completed", RowTimeout());
            await Expect(importRow).ToContainTextAsync($"{pairs.Length} rows");

            // The pairs are stored verbatim - nothing was generated for them.
            await GotoAsync($"/ui/namespaces/{Uri.EscapeDataString(namespaceName)}/pseudonyms");
            foreach (var (originalValue, pseudonymValue) in pairs)
            {
                await Expect(Page.Locator("body")).ToContainTextAsync(originalValue);
                await Expect(Page.Locator("body")).ToContainTextAsync(pseudonymValue);
            }

            await GotoAsync("/ui/csv-jobs");
            await Page.SelectOptionAsync("#direction", "Export");
            await Page.SelectOptionAsync("#jobNamespace", namespaceName);
            await Page.ClickAsync("#submitJobButton");

            var exportRow = JobRow(namespaceName);
            await Expect(exportRow).ToContainTextAsync("Completed", RowTimeout());
            await Expect(exportRow).ToContainTextAsync($"{pairs.Length} rows");

            var exported = await DownloadOutputAsync(exportRow);
            // A leading UTF-8 BOM: every CSV job's output is written through a StreamWriter over
            // the job's own encoding, which emits one for utf-8. Trimmed here rather than
            // asserted against, since it is the writer's behavior for every direction and not
            // this one's concern - and StreamReader strips it right back off on the way in, which
            // is what lets the re-import below find the "original" column at all.
            exported.TrimStart('\uFEFF').Should().StartWith("original,pseudonym");
            foreach (var (originalValue, pseudonymValue) in pairs)
            {
                exported.Should().Contain($"{originalValue},{pseudonymValue}");
            }

            // The point of the pair of directions: an export is directly importable, with no
            // editing and nothing restated on the form.
            var secondNamespace = $"e2e-reimport-{UniqueSuffix()}";
            var exportedPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
            await File.WriteAllTextAsync(exportedPath, exported);
            try
            {
                await CreateNamespaceAsync(secondNamespace);

                await GotoAsync("/ui/csv-jobs");
                await Page.SelectOptionAsync("#direction", "Import");
                await Page.SetInputFilesAsync("#csvFileInput", exportedPath);
                await Page.SelectOptionAsync("#jobNamespace", secondNamespace);
                await Page.ClickAsync("#submitJobButton");

                var reimportRow = JobRow(Path.GetFileName(exportedPath));
                await Expect(reimportRow).ToContainTextAsync("Completed", RowTimeout());

                var report = await DownloadOutputAsync(reimportRow);
                foreach (var (originalValue, pseudonymValue) in pairs)
                {
                    report.Should().Contain($"{originalValue},{pseudonymValue},Imported");
                }
            }
            finally
            {
                File.Delete(exportedPath);
            }
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public async Task Import_ReportsAConflictingRowWithoutFailingTheJob()
    {
        var namespaceName = $"e2e-import-{UniqueSuffix()}";
        var csvPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
        // Two rows claiming the same pseudonym for different original values: the first wins, the
        // second is refused, and the job still completes.
        await File.WriteAllTextAsync(
            csvPath,
            "original,pseudonym\nalice,shared-psn\nbob,shared-psn\n"
        );

        try
        {
            await CreateNamespaceAsync(namespaceName);

            await GotoAsync("/ui/csv-jobs");
            await Page.SelectOptionAsync("#direction", "Import");
            await Page.SetInputFilesAsync("#csvFileInput", csvPath);
            await Page.SelectOptionAsync("#jobNamespace", namespaceName);
            await Page.ClickAsync("#submitJobButton");

            var importRow = JobRow(Path.GetFileName(csvPath));
            await Expect(importRow).ToContainTextAsync("Completed", RowTimeout());

            var report = await DownloadOutputAsync(importRow);
            report.Should().Contain("alice,shared-psn,Imported");
            report.Should().Contain("bob,shared-psn,PseudonymValueConflict");
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    private static LocatorAssertionsToContainTextOptions RowTimeout() =>
        new() { Timeout = JobTimeoutMs };

    /// <summary>The jobs grid row identified by what its "File" column shows.</summary>
    private ILocator JobRow(string subject) =>
        Page.Locator("tr").Filter(new LocatorFilterOptions { HasText = subject }).First;

    /// <summary>
    /// Clicks a completed job's Download button and returns what the presigned URL serves.
    /// The button opens the URL via <c>window.open</c> rather than navigating, so that's stubbed
    /// out to capture the URL instead of letting the browser start a download it would hand to
    /// the OS.
    /// </summary>
    private async Task<string> DownloadOutputAsync(ILocator jobRow)
    {
        await Page.EvaluateAsync(
            "() => { window.__vfpsOpenedUrl = null; window.open = url => { window.__vfpsOpenedUrl = url; return null; }; }"
        );
        await jobRow.GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync();

        var url = await Page.WaitForFunctionAsync("() => window.__vfpsOpenedUrl");
        var response = await Page.APIRequest.GetAsync(await url.JsonValueAsync<string>());
        response.Ok.Should().BeTrue();

        return await response.TextAsync();
    }

    private async Task CreateNamespaceAsync(string name)
    {
        await GotoAsync("/ui/namespaces");
        await Page.ClickAsync("#showCreateNamespaceFormButton");
        await Expect(Page.Locator("#name")).ToBeVisibleAsync();
        await Page.FillAsync("#name", name);
        await Page.ClickAsync("form:has(#name) button[type=submit]");
        await Expect(Page.Locator($"[role=treeitem][data-value=\"{name}\"]")).ToHaveCountAsync(1);
    }
}
