using System.Globalization;

namespace Vfps.PlaywrightTests;

public class HomeTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    [Fact]
    public async Task StatTiles_ShowOnlyWhatThisDeploymentHasEnabled()
    {
        // The default "test" compose profile runs with authorization and S3 both off (see
        // CsvJobsTests.S3Disabled_ShowsWarningBanner), so there are no access tokens and no CSV
        // jobs to report on - their tiles and list must be missing rather than showing zeros.
        await GotoAsync("/ui");

        await Expect(Page.GetByTestId("stat-namespaces")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("stat-pseudonyms")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("home-namespaces")).ToBeVisibleAsync();

        await Expect(Page.GetByTestId("stat-csv-jobs")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("home-csv-jobs")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("stat-access-tokens")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("attention")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task NamespacesTile_CountsANewlyCreatedNamespace()
    {
        await GotoAsync("/ui");
        var before = await NamespaceCountAsync();

        // Created through the REST API rather than the namespaces page's dialog: what is under
        // test is the count, not the form (NamespacesTests covers that).
        var response = await Page.APIRequest.PostAsync(
            $"{PlaywrightFixture.BaseUrl}/v1/namespaces",
            new APIRequestContextOptions
            {
                DataObject = new { name = $"e2e-home-{UniqueSuffix()}", pseudonymLength = 16 },
            }
        );
        response.Ok.Should().BeTrue(await response.TextAsync());

        await GotoAsync("/ui");
        (await NamespaceCountAsync()).Should().Be(before + 1);
    }

    [Fact]
    public async Task Heading_IsFocusedAfterNavigating_WithoutAFocusOutline()
    {
        // Routes.razor's FocusOnNavigate moves focus to the page's h1 so a screen reader announces
        // it. On a full page load Chromium counts that as :focus-visible and used to draw its
        // default outline around the title of every page - see the h1:focus rule in app.css.
        await GotoAsync("/ui");

        var heading = Page.Locator("h1");
        await Expect(heading).ToBeFocusedAsync();
        (await heading.EvaluateAsync<string>("el => getComputedStyle(el).outlineStyle"))
            .Should()
            .Be("none");
    }

    private async Task<int> NamespaceCountAsync()
    {
        // The tile's first paragraph is its figure, formatted with the page's (English) group
        // separator - which the invariant culture shares.
        var text = await Page.GetByTestId("stat-namespaces").Locator("p").First.InnerTextAsync();
        return int.Parse(text, NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
    }
}
