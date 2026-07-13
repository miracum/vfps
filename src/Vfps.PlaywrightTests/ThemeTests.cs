namespace Vfps.PlaywrightTests;

public class ThemeTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    [Fact]
    public async Task ToggleDarkMode_TogglesHtmlClass()
    {
        await GotoAsync("/ui/namespaces");

        var html = Page.Locator("html");
        var wasDark = await html.EvaluateAsync<bool>("el => el.classList.contains('dark')");

        await Page.ClickAsync("[aria-label='Toggle dark mode']");

        var isDarkNow = await html.EvaluateAsync<bool>("el => el.classList.contains('dark')");
        isDarkNow.Should().Be(!wasDark);

        // Toggling back and reloading confirms the preference persists (localStorage), not just
        // the in-memory DOM class for this one page load.
        await Page.ClickAsync("[aria-label='Toggle dark mode']");
        await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var isDarkAfterReload = await html.EvaluateAsync<bool>(
            "el => el.classList.contains('dark')"
        );
        isDarkAfterReload.Should().Be(wasDark);
    }
}
