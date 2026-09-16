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

    /// <summary>
    /// Picks a column in one of the import form's column dropdowns, waiting for the option to be
    /// there first.
    ///
    /// Those dropdowns only exist once the browser has read the file's header row and told the
    /// server about it, which is a JS interop call plus a render round trip after the file input
    /// changes. Selecting straight after SetInputFilesAsync races that: the element resolves to
    /// the free-text fallback the selector renders when no columns are known yet, or to one being
    /// swapped out mid-render.
    /// </summary>
    private async Task SelectDetectedColumnAsync(string selectId, string column)
    {
        await Expect(Page.Locator($"{selectId} option[value='{column}']")).ToHaveCountAsync(1);
        await Page.SelectOptionAsync(selectId, column);
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

    [Fact]
    public async Task ImportColumns_AreOfferedAsDropdownsOfTheFilesOwnHeaderRow()
    {
        // The same header detection the pseudonymize form uses - readHeaderRow reads only the
        // first chunk of the file, so this costs nothing even for a huge upload.
        var csvPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
        await File.WriteAllTextAsync(csvPath, "mrn,psn,target_ns\n1,p1,ns-a\n");

        try
        {
            await GotoAsync("/ui/csv-jobs");
            await Page.SelectOptionAsync("#direction", "Import");
            await Page.SetInputFilesAsync("#csvFileInput", csvPath);

            foreach (
                var id in new[]
                {
                    "#originalValueColumn",
                    "#pseudonymValueColumn",
                    "#namespaceColumn",
                }
            )
            {
                // Retrying, unlike AllInnerTextsAsync on its own: the options arrive with the
                // render that follows the header being read, not with the file selection.
                await Expect(Page.Locator($"{id} option"))
                    .ToContainTextAsync(["mrn", "psn", "target_ns"]);
            }
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public async Task ChoosingANamespaceColumn_ReplacesTheNamespacePicker()
    {
        var csvPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
        await File.WriteAllTextAsync(csvPath, "original,pseudonym,namespace\na,p,ns\n");

        try
        {
            await GotoAsync("/ui/csv-jobs");
            await Page.SelectOptionAsync("#direction", "Import");
            await Page.SetInputFilesAsync("#csvFileInput", csvPath);

            // With no namespace column, one namespace is picked for the whole file.
            await Expect(Page.Locator("#jobNamespace")).ToBeVisibleAsync();

            await SelectDetectedColumnAsync("#namespaceColumn", "namespace");

            // With one, there is no single namespace to pick any more.
            await Expect(Page.Locator("#jobNamespace")).ToHaveCountAsync(0);

            await Page.SelectOptionAsync("#namespaceColumn", string.Empty);
            await Expect(Page.Locator("#jobNamespace")).ToBeVisibleAsync();
        }
        finally
        {
            File.Delete(csvPath);
        }
    }

    [Fact]
    public async Task ImportWithANamespaceColumn_LoadsOneFileIntoSeveralNamespaces()
    {
        var first = $"e2e-multi-a-{UniqueSuffix()}";
        var second = $"e2e-multi-b-{UniqueSuffix()}";
        var csvPath = Path.Join(Path.GetTempPath(), $"vfps-e2e-{UniqueSuffix()}.csv");
        await File.WriteAllTextAsync(
            csvPath,
            "original,pseudonym,namespace\n"
                + $"alice,psn-alice,{first}\n"
                + $"bob,psn-bob,{second}\n"
                + $"carol,psn-carol,{first}\n"
                + "dave,psn-dave,no-such-namespace-here\n"
        );

        try
        {
            await CreateNamespaceAsync(first);
            await CreateNamespaceAsync(second);

            await GotoAsync("/ui/csv-jobs");
            await Page.SelectOptionAsync("#direction", "Import");
            await Page.SetInputFilesAsync("#csvFileInput", csvPath);
            await SelectDetectedColumnAsync("#namespaceColumn", "namespace");
            await Page.ClickAsync("#submitJobButton");

            var jobRow = JobRow(Path.GetFileName(csvPath));
            await Expect(jobRow).ToContainTextAsync("Completed", RowTimeout());

            // Every row reported, including the one naming a namespace that does not exist - which
            // is skipped rather than failing the job.
            var report = await DownloadOutputAsync(jobRow);
            report.Should().Contain($"alice,psn-alice,{first},Imported");
            report.Should().Contain($"bob,psn-bob,{second},Imported");
            report.Should().Contain($"carol,psn-carol,{first},Imported");
            report.Should().Contain("dave,psn-dave,no-such-namespace-here,UnknownNamespace");

            // And the pairs really landed in the namespace each row named, not all in one.
            await GotoAsync($"/ui/namespaces/{Uri.EscapeDataString(first)}/pseudonyms");
            await Expect(Page.Locator("body")).ToContainTextAsync("psn-alice");
            await Expect(Page.Locator("body")).ToContainTextAsync("psn-carol");
            await Expect(Page.Locator("body")).Not.ToContainTextAsync("psn-bob");

            await GotoAsync($"/ui/namespaces/{Uri.EscapeDataString(second)}/pseudonyms");
            await Expect(Page.Locator("body")).ToContainTextAsync("psn-bob");
            await Expect(Page.Locator("body")).Not.ToContainTextAsync("psn-alice");
        }
        finally
        {
            File.Delete(csvPath);
        }
    }
}
