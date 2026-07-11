namespace Vfps.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public abstract class VfpsPageTestBase(PlaywrightFixture fixture) : IAsyncLifetime
{
    protected IBrowserContext Context { get; private set; } = null!;
    protected IPage Page { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Context = await fixture.Browser.NewContextAsync();
        Page = await Context.NewPageAsync();
    }

    public async Task DisposeAsync() => await Context.CloseAsync();

    protected async Task GotoAsync(string path) =>
        await Page.GotoAsync(
            $"{PlaywrightFixture.BaseUrl}{path}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle }
        );

    protected static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..12];
}
