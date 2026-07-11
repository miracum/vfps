namespace Vfps.PlaywrightTests;

/// <summary>
/// Shared across the whole test run (launching a browser per test would be far too slow) -
/// individual tests get isolation via a fresh <see cref="IBrowserContext"/>/<see cref="IPage"/>
/// each, created in <see cref="VfpsPageTestBase"/>.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    /// <summary>
    /// The vfps instance under test - defaults to the port `compose.yaml`'s "test" profile
    /// publishes. Overridable so CI (or a developer) can point this at any already-running
    /// instance, container-based or not.
    /// </summary>
    public static string BaseUrl { get; } =
        Environment.GetEnvironmentVariable("PLAYWRIGHT_BASE_URL") ?? "http://localhost:8080";

    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Args = ["--no-sandbox"] }
        );
    }

    public async Task DisposeAsync()
    {
        await Browser.CloseAsync();
        Playwright.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class PlaywrightCollection : ICollectionFixture<PlaywrightFixture>
{
    public const string Name = "Playwright";
}
