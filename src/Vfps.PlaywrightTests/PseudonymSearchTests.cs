namespace Vfps.PlaywrightTests;

public class PseudonymSearchTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    [Fact]
    public async Task Search_RunsOnEnterAndOnTheSearchButton_NotWhileTyping()
    {
        var name = $"e2e-search-{UniqueSuffix()}";
        await CreateNamespaceWithPseudonymsAsync(name, "alice", "alicia", "bob");

        await GotoAsync($"/ui/namespaces/{Uri.EscapeDataString(name)}/pseudonyms");
        await Expect(OriginalValue("bob")).ToBeVisibleAsync();

        var search = Page.Locator("#pseudonymSearch");
        await search.FillAsync("bob");

        // Well past the 300ms the grid's own search box used to wait after the last keystroke.
        await Task.Delay(1000);
        await Expect(OriginalValue("alice")).ToBeVisibleAsync();

        await search.PressAsync("Enter");
        await Expect(OriginalValue("alice")).ToHaveCountAsync(0);
        await Expect(OriginalValue("bob")).ToBeVisibleAsync();

        await search.FillAsync("ali");
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Search" })
            .ClickAsync();
        await Expect(OriginalValue("bob")).ToHaveCountAsync(0);
        await Expect(OriginalValue("alice")).ToBeVisibleAsync();
        await Expect(OriginalValue("alicia")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Search_OfAnEmptyBox_ListsEverythingAgain()
    {
        var name = $"e2e-search-{UniqueSuffix()}";
        await CreateNamespaceWithPseudonymsAsync(name, "alice", "bob");

        await GotoAsync($"/ui/namespaces/{Uri.EscapeDataString(name)}/pseudonyms");
        var search = Page.Locator("#pseudonymSearch");
        await search.FillAsync("bob");
        await search.PressAsync("Enter");
        // Both halves: while the search runs the grid shows a skeleton and neither is on screen,
        // so "alice is gone" alone would let the next step start before this one has finished.
        await Expect(OriginalValue("alice")).ToHaveCountAsync(0);
        await Expect(OriginalValue("bob")).ToBeVisibleAsync();

        await search.FillAsync("");
        await search.PressAsync("Enter");
        await Expect(OriginalValue("alice")).ToBeVisibleAsync();
        await Expect(OriginalValue("bob")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Search_SubmittedAgainWhileTheFirstIsStillRunning_ShowsTheLatestOne()
    {
        // Regression test: the grid of a search that had been replaced could still ask for its
        // rows and then reset the loading state of the search that replaced it, leaving that one
        // on its skeleton for good. It depends on timing - the second Enter has to land while the
        // first search is in flight - so the pair is repeated.
        var name = $"e2e-search-{UniqueSuffix()}";
        await CreateNamespaceWithPseudonymsAsync(name, "alice", "bob");

        for (var attempt = 0; attempt < 15; attempt++)
        {
            // A fresh page each time: the first search after a page load is where this happened.
            await GotoAsync($"/ui/namespaces/{Uri.EscapeDataString(name)}/pseudonyms");
            var search = Page.Locator("#pseudonymSearch");
            await Expect(OriginalValue("bob")).ToBeVisibleAsync();

            await search.FillAsync("bob");
            await search.PressAsync("Enter");
            await Task.Delay(attempt % 3 * 30);
            await search.FillAsync("");
            await search.PressAsync("Enter");

            await Expect(OriginalValue("alice")).ToBeVisibleAsync();
            await Expect(OriginalValue("bob")).ToBeVisibleAsync();
        }
    }

    private ILocator OriginalValue(string value) =>
        Page.Locator("code").GetByText(value, new LocatorGetByTextOptions { Exact = true });

    private async Task CreateNamespaceWithPseudonymsAsync(string name, params string[] values)
    {
        var created = await Context.APIRequest.PostAsync(
            $"{PlaywrightFixture.BaseUrl}/v1/namespaces",
            new APIRequestContextOptions
            {
                DataObject = new
                {
                    name,
                    pseudonymGenerationMethod = "PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED",
                    pseudonymLength = 16,
                },
            }
        );
        created.Ok.Should().BeTrue();

        foreach (var value in values)
        {
            var pseudonym = await Context.APIRequest.PostAsync(
                $"{PlaywrightFixture.BaseUrl}/v1/namespaces/{name}/pseudonyms",
                new APIRequestContextOptions { DataObject = new { originalValue = value } }
            );
            pseudonym.Ok.Should().BeTrue();
        }
    }
}
