namespace Vfps.PlaywrightTests;

/// <summary>
/// The app shell's keyboard and small-screen behaviour. None of this is visible in a screenshot,
/// and the first of these went unnoticed for a long time: the component library's nav rendered its
/// links outside the tab order, so they could only ever be reached with a mouse.
/// </summary>
public class NavigationTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    // More than enough to go round the page: the header has about a dozen tab stops.
    private const int MaxTabPresses = 40;

    [Fact]
    public async Task MainNav_IsInTheTabOrder_AndMarksTheCurrentPage()
    {
        await GotoAsync("/ui/namespaces");

        var link = Page.Locator(".main-nav")
            .GetByRole(AriaRole.Link, new() { Name = "Namespaces" });

        await Expect(link).ToHaveAttributeAsync("aria-current", "page");
        (await TabUntilFocusedAsync(link)).Should().BeTrue();
    }

    [Fact]
    public async Task SkipLink_AppearsOnFocus_AndMovesFocusToTheContent()
    {
        await GotoAsync("/ui/namespaces");
        var url = Page.Url;

        var skipLink = Page.Locator(".skip-link");
        (await TabUntilFocusedAsync(skipLink)).Should().BeTrue();
        await Expect(skipLink).ToBeInViewportAsync();

        await Page.Keyboard.PressAsync("Enter");

        await Expect(Page.Locator("main")).ToBeFocusedAsync();
        // The app's <base> would resolve the link's fragment to the home page if it were followed.
        Page.Url.Should().Be(url);
    }

    [Fact]
    public async Task BelowTheBreakpoint_TheNavIsASheet_ThatClosesOnNavigating()
    {
        await Page.SetViewportSizeAsync(1024, 768);
        await GotoAsync("/ui/namespaces");

        await Expect(Page.Locator(".main-nav")).ToBeHiddenAsync();

        await Page.ClickAsync(".nav-toggle");
        var sheet = Page.GetByRole(AriaRole.Dialog);
        await sheet.GetByRole(AriaRole.Link, new() { Name = "Projects" }).ClickAsync();

        await Expect(Page)
            .ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/ui/projects$"));
        await Expect(sheet).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task NoPage_ScrollsSideways_BetweenPhoneAndLaptopWidths()
    {
        // The nav used to collapse only below 768px, so from there up to about 1120px every page
        // was wider than the screen.
        foreach (var width in new[] { 320, 390, 768, 1024, 1247, 1248 })
        {
            await Page.SetViewportSizeAsync(width, 800);
            await GotoAsync("/ui/namespaces");

            var overflow = await Page.EvaluateAsync<int>(
                "document.documentElement.scrollWidth - document.documentElement.clientWidth"
            );
            overflow.Should().Be(0, $"nothing should stick out at {width}px");
        }
    }

    [Fact]
    public async Task HtmlLang_FollowsTheChosenLanguage()
    {
        await GotoAsync("/ui");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "en");

        await GotoAsync("/culture/set?culture=de&redirectUri=%2Fui");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("lang", "de");
    }

    [Fact]
    public async Task Footer_EndsThePage_WhetherThePageIsShorterOrLongerThanTheWindow()
    {
        // A window far taller than any page, then one far shorter. The footer used to stop where
        // the content did, with empty page below it.
        foreach (var (width, height) in new[] { (1280, 3000), (390, 600) })
        {
            await Page.SetViewportSizeAsync(width, height);
            await GotoAsync("/ui");

            var gap = await Page.EvaluateAsync<double>(
                """
                () => {
                    const pageEnd = Math.max(window.innerHeight, document.documentElement.scrollHeight);
                    const footerEnd = document.querySelector('footer').getBoundingClientRect().bottom + window.scrollY;
                    return Math.abs(pageEnd - footerEnd);
                }
                """
            );
            gap.Should().BeLessThan(1, $"the footer should end the page at {width}x{height}");
        }
    }

    [Fact]
    public async Task ALongDialog_StaysOnScreen_OnAPhoneHeldSideways()
    {
        await Page.SetViewportSizeAsync(844, 390);
        await GotoAsync("/ui/namespaces");
        await Page.ClickAsync("#showCreateNamespaceFormButton");

        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();

        var box = await dialog.BoundingBoxAsync();
        box!.Y.Should().BeGreaterThanOrEqualTo(0);
        (box.Y + box.Height).Should().BeLessThanOrEqualTo(390);
    }

    [Fact]
    public async Task AnUnknownAddress_ShowsAPageWithAWayBack()
    {
        // Used to be an empty response, so a blank page with nothing to click.
        var response = await Page.GotoAsync($"{PlaywrightFixture.BaseUrl}/ui/no-such-page");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        response!.Status.Should().Be(404);
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Page not found" }))
            .ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Link, new() { Name = "Go to the home page" }).ClickAsync();
        await Expect(Page.Locator("[data-testid]").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AnUnknownApiPath_StaysAPlainNotFound()
    {
        // The page above is for people. A client asking for a path that doesn't exist must not get
        // HTML back with it.
        var response = await Page.APIRequest.GetAsync(
            $"{PlaywrightFixture.BaseUrl}/v1/no-such-path"
        );

        response.Status.Should().Be(404);
        (await response.TextAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ALongNamespaceName_LeavesItsActionsOnScreen_OnAPhone()
    {
        // With no explicit column, the grid holding the tree and the details sized itself to the
        // longest name, and the page's overflow clipping then cut off the buttons at the end.
        var name = $"e2e-{new string('x', 60)}-{UniqueSuffix()}";
        await CreateNamespaceAsync(name);

        await Page.SetViewportSizeAsync(390, 844);
        await Page.Locator($"[role=treeitem][data-value=\"{name}\"]").ClickAsync();

        var delete = Page.Locator("#deleteSelectedNamespaceButton");
        await Expect(delete).ToBeVisibleAsync();
        var box = await delete.BoundingBoxAsync();
        (box!.X + box.Width).Should().BeLessThanOrEqualTo(390);
    }

    private async Task<bool> TabUntilFocusedAsync(ILocator target)
    {
        for (var i = 0; i < MaxTabPresses; i++)
        {
            await Page.Keyboard.PressAsync("Tab");
            if (await target.EvaluateAsync<bool>("el => el === document.activeElement"))
            {
                return true;
            }
        }

        return false;
    }
}
