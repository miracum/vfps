namespace Vfps.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public abstract class VfpsPageTestBase(PlaywrightFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// Set on an input or textarea the moment script attaches a <c>change</c> listener to it. See
    /// <see cref="FillInputAsync(ILocator, string)"/> for why the tests need to know.
    /// </summary>
    private const string ListeningAttribute = "data-e2e-listening";

    // Runs in the page's main world before any of its own scripts, so it sees every listener
    // BlazorBlueprint's text-input.js attaches. Playwright's own fill/click machinery runs in an
    // isolated world with its own prototypes and is unaffected. Blazor itself delegates its
    // events from the document rather than individual elements, so it never trips this either.
    private const string MarkListeningInputsScript = $$"""
        (() => {
          const addEventListener = EventTarget.prototype.addEventListener;
          EventTarget.prototype.addEventListener = function (type, ...rest) {
            const result = addEventListener.call(this, type, ...rest);
            if (
              type === "change" &&
              (this instanceof HTMLInputElement || this instanceof HTMLTextAreaElement)
            ) {
              this.setAttribute("{{ListeningAttribute}}", "");
            }
            return result;
          };
        })();
        """;

    protected IBrowserContext Context { get; private set; } = null!;
    protected IPage Page { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Context = await fixture.Browser.NewContextAsync();
        await Context.AddInitScriptAsync(MarkListeningInputsScript);
        Page = await Context.NewPageAsync();
    }

    public async ValueTask DisposeAsync() => await Context.CloseAsync();

    protected async Task GotoAsync(string path) =>
        await Page.GotoAsync(
            $"{PlaywrightFixture.BaseUrl}{path}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle }
        );

    /// <summary>
    /// Fills a BbInput, but only once it is able to tell the server about it.
    ///
    /// A BbInput reports what is typed through listeners its OnAfterRenderAsync attaches over a
    /// JS interop call of its own, separate from the render that put the element on screen - and
    /// the page itself has no way to signal when that has happened. A value filled in before then
    /// sits in the field but never reaches the server: no input event carries it, and the change
    /// event that would on blur has already fired by the time anyone listens. The server goes on
    /// with an empty field - a required name fails validation, a pattern gets no verdict - while
    /// the browser shows it filled in. Waiting for the element to be visible, or for something
    /// else in its dialog to be focused, does not rule that out: Blazor runs a parent's
    /// OnAfterRenderAsync before its children's, so a dialog's own interop lands ahead of its
    /// inputs' rather than after.
    /// </summary>
    protected async Task FillInputAsync(ILocator field, string value)
    {
        await Expect(field).ToHaveAttributeAsync(ListeningAttribute, string.Empty);
        await field.FillAsync(value);
    }

    /// <inheritdoc cref="FillInputAsync(ILocator, string)"/>
    protected Task FillInputAsync(string selector, string value) =>
        FillInputAsync(Page.Locator(selector), value);

    protected static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..12];
}
