namespace Vfps.PlaywrightTests;

public class NamespacesTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    [Fact]
    public async Task CreateNamespace_AppearsInList()
    {
        var name = UniqueName();

        await GotoAsync("/ui/namespaces");
        await Page.FillAsync("#name", name);
        await Page.ClickAsync("button:has-text(\"Create\")");

        await Expect(Page.Locator("body")).ToContainTextAsync(name);
    }

    [Fact]
    public async Task CreateDuplicateNamespace_ShowsFriendlyError_ThenNewNameSucceeds()
    {
        // Regression test: a failed create (duplicate name) used to leave the circuit-scoped
        // DbContext's change tracker holding the failed entity as "Added" forever, so the
        // *next* create call - even for a genuinely different name - crashed with
        // "The instance of entity type 'Namespace' cannot be tracked because another instance
        // with the same key value... is already being tracked." Fixed by wrapping
        // SaveChangesAsync in try/finally in NamespaceRepository.CreateAsync so
        // ChangeTracker.Clear() runs on the failure path too, not just on success.
        var name = UniqueName();
        var otherName = UniqueName();

        await GotoAsync("/ui/namespaces");

        await Page.FillAsync("#name", name);
        await Page.ClickAsync("button:has-text(\"Create\")");
        await Expect(Page.Locator("body")).ToContainTextAsync(name);

        await Page.FillAsync("#name", name);
        await Page.ClickAsync("button:has-text(\"Create\")");
        await Expect(Page.Locator("body")).ToContainTextAsync($"'{name}'");
        await Expect(Page.Locator("body")).ToContainTextAsync("already exists");

        await Page.FillAsync("#name", otherName);
        await Page.ClickAsync("button:has-text(\"Create\")");

        await Expect(Page.Locator("body")).ToContainTextAsync(otherName);
        var bodyText = await Page.Locator("body").InnerTextAsync();
        bodyText.Should().NotContain("already being tracked");
    }

    [Fact]
    public async Task RapidClicks_CreatesExactlyOneNamespace()
    {
        // Regression test for the re-entrancy guard in CreateNamespaceAsync: Blazor Server
        // dispatches each click as a potentially-concurrent invocation on the circuit's
        // synchronization context, so without the `_isCreating` guard, a second rapid click
        // could start a genuinely overlapping call against the same non-thread-safe DbContext.
        var name = UniqueName();
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await GotoAsync("/ui/namespaces");
        await Page.FillAsync("#name", name);

        var createButton = Page.Locator("button:has-text(\"Create\")");
        var clickOptions = new LocatorClickOptions { Force = true };
        await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => createButton.ClickAsync(clickOptions))
        );

        await Task.Delay(1000);
        await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var bodyText = await Page.Locator("body").InnerTextAsync();
        CountOccurrences(bodyText, name).Should().Be(1);
        pageErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task NamespaceWithUrlUnsafeCharacters_BrowseLinkWorks()
    {
        // Regression test: browsing a namespace whose name contains URL-unsafe characters (e.g.
        // a FHIR identifier system URI) used to 404 because the "Browse" link didn't URL-encode
        // the name - see Uri.EscapeDataString(n.Name) in Namespaces.razor.
        var name = $"https://example.org/fhir/{UniqueSuffix()}";

        await GotoAsync("/ui/namespaces");
        await Page.FillAsync("#name", name);
        await Page.ClickAsync("button:has-text(\"Create\")");
        await Expect(Page.Locator("body")).ToContainTextAsync(name);

        var row = Page.Locator("tr", new PageLocatorOptions { HasText = name });
        await row.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Browse" })
            .ClickAsync();

        await Expect(Page)
            .ToHaveURLAsync(
                $"{PlaywrightFixture.BaseUrl}/ui/namespaces/{Uri.EscapeDataString(name)}/pseudonyms"
            );
        var bodyText = await Page.Locator("body").InnerTextAsync();
        bodyText.Should().NotContain("404");
    }

    private static string UniqueName() => $"e2e-ns-{UniqueSuffix()}";

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
