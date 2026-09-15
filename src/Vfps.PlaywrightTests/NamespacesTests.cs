namespace Vfps.PlaywrightTests;

public class NamespacesTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    [Fact]
    public async Task CreateNamespace_AppearsInTheHierarchy()
    {
        var name = UniqueName();

        await GotoAsync("/ui/namespaces");
        await CreateNamespaceAsync(name);

        await Expect(TreeNode(name)).ToHaveCountAsync(1);
        // A successful create closes the dialog behind itself.
        await Expect(Page.Locator("#name")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task CreateForm_IsADialogTheHeaderButtonOpens()
    {
        await GotoAsync("/ui/namespaces");

        await Expect(Page.Locator("#name")).ToHaveCountAsync(0);

        // Visible and focused, so typing can start straight away - OpenCreateDialogAsync relies
        // on the focus half as its settle signal.
        await OpenCreateDialogAsync();
        await Expect(Page.Locator("#name")).ToBeVisibleAsync();

        // The two boolean options are switches now, not checkboxes.
        await Expect(Page.Locator("#allowsMultiplePseudonyms"))
            .ToHaveAttributeAsync("role", "switch");
        await Expect(Page.Locator("#ensureParentPseudonymExists"))
            .ToHaveAttributeAsync("role", "switch");

        // Closed via Cancel rather than the Escape key: Escape is handled by the component
        // library's own JS module, which is imported on first render, so pressing it this soon
        // after opening is a race - the press lands before the handler is registered often
        // enough to make the assertion flaky. Cancel goes through our own OnClick.
        await CloseCreateDialogAsync();
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
        await CreateNamespaceAsync(name);

        // A create that fails leaves the dialog open, with the name still in the field, so the
        // second attempt below types over it rather than starting from a fresh dialog.
        await OpenCreateDialogAsync();
        await Page.FillAsync("#name", name);
        await SubmitCreateFormAsync();
        await Expect(Page.Locator("body")).ToContainTextAsync($"'{name}'");
        await Expect(Page.Locator("body")).ToContainTextAsync("already exists");

        await Page.FillAsync("#name", otherName);
        await SubmitCreateFormAsync();

        await Expect(TreeNode(otherName)).ToHaveCountAsync(1);
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
        await OpenCreateDialogAsync();
        await Page.FillAsync("#name", name);

        var createButton = Page.Locator(CreateSubmitSelector);
        var clickOptions = new LocatorClickOptions { Force = true };
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ClickIgnoringDetachedAsync()));

        // The click that wins closes the dialog, which tears the button out from under the ones
        // still in flight. That's the scenario under test, not a failure: what matters is that
        // exactly one namespace comes out of it and the circuit survives.
        async Task ClickIgnoringDetachedAsync()
        {
            try
            {
                await createButton.ClickAsync(clickOptions);
            }
            catch (PlaywrightException)
            {
                // The dialog was already gone.
            }
        }

        await Task.Delay(1000);
        await Page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // One node in the tree, not one occurrence of the name in the page text: the name shows
        // up elsewhere too - the create dialog's parent picker lists every existing namespace as
        // an option - so matching raw body text would report a namespace that was never created.
        await Expect(TreeNode(name)).ToHaveCountAsync(1);
        pageErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task NamespaceWithUrlUnsafeCharacters_BrowseLinkWorks()
    {
        // Regression test: browsing a namespace whose name contains URL-unsafe characters (e.g.
        // a FHIR identifier system URI) used to 404 because the "Browse" link didn't URL-encode
        // the name - see Uri.EscapeDataString(selected.Name) in Namespaces.razor.
        var name = $"https://example.org/fhir/{UniqueSuffix()}";

        await GotoAsync("/ui/namespaces");
        await CreateNamespaceAsync(name);

        await SelectNamespaceAsync(name);
        await Page.Locator("#namespaceDetails")
            .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Browse" })
            .ClickAsync();

        await Expect(Page)
            .ToHaveURLAsync(
                $"{PlaywrightFixture.BaseUrl}/ui/namespaces/{Uri.EscapeDataString(name)}/pseudonyms"
            );
        var bodyText = await Page.Locator("body").InnerTextAsync();
        bodyText.Should().NotContain("404");
    }

    [Fact]
    public async Task DeleteNamespace_FocusesTheConfirmation_ThenRemovesTheNamespace()
    {
        // Regression test: the confirmation panel renders at the top of the page while the button
        // that opens it sits further down, so once the page scrolls at all, clicking Delete
        // revealed the panel off-screen above the viewport and the click looked like it had done
        // nothing. StartDeleteNamespace now asks for the confirmation input to be focused, which
        // brings the panel into view.
        var name = UniqueName();

        await GotoAsync("/ui/namespaces");
        await CreateNamespaceAsync(name);

        await SelectNamespaceAsync(name);
        await Page.ClickAsync("#deleteSelectedNamespaceButton");

        var confirmation = Page.Locator("#confirmDeleteNamespaceName");
        await Expect(confirmation).ToBeInViewportAsync();
        await Expect(confirmation).ToBeFocusedAsync();

        await confirmation.FillAsync(name);
        await Page.ClickAsync("#confirmDeleteNamespaceButton");

        await Expect(TreeNode(name)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Hierarchy_NestsAChildUnderItsParent_AndSelectingOneShowsItsDetails()
    {
        var parent = UniqueName();
        var child = UniqueName();

        await GotoAsync("/ui/namespaces");
        await CreateNamespaceAsync(parent, description: "the parent namespace");
        await CreateNamespaceAsync(child, parent: parent, description: "the child namespace");

        // The parent has to end up expanded on its own: the child was created after the tree's
        // first render, so nothing would reveal it if expansion were left to DefaultExpandAll.
        await Expect(TreeNode(parent)).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(TreeNode(child)).ToHaveAttributeAsync("data-depth", "1");

        await SelectNamespaceAsync(child);

        var details = Page.Locator("#namespaceDetails");
        await Expect(details).ToContainTextAsync(child);
        await Expect(details).ToContainTextAsync("the child namespace");
        await Expect(details).ToContainTextAsync("Created at");
        await Expect(details).ToContainTextAsync(parent);
    }

    [Fact]
    public async Task Search_KeepsMatchesAndTheirAncestors_AndHidesEverythingElse()
    {
        var parent = UniqueName();
        var child = $"{parent}-child";
        var unrelated = UniqueName();

        await GotoAsync("/ui/namespaces");
        await CreateNamespaceAsync(parent);
        await CreateNamespaceAsync(unrelated);
        await CreateNamespaceAsync(child, parent: parent);

        // Searching for the child keeps the parent too: without the path leading down to it, a
        // nested match would have nothing to hang off.
        await Page.FillAsync("#namespaceSearch", child);
        await Expect(TreeNode(child)).ToHaveCountAsync(1);
        await Expect(TreeNode(parent)).ToHaveCountAsync(1);
        await Expect(TreeNode(unrelated)).ToHaveCountAsync(0);

        await Page.FillAsync("#namespaceSearch", $"no-such-namespace-{UniqueSuffix()}");
        await Expect(Page.Locator("[role=treeitem]")).ToHaveCountAsync(0);
        await Expect(Page.Locator("body")).ToContainTextAsync("No namespace matches");

        await Page.FillAsync("#namespaceSearch", string.Empty);
        await Expect(TreeNode(unrelated)).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task ValidationRegex_ChecksThePatternAsItIsTyped()
    {
        await GotoAsync("/ui/namespaces");
        await OpenCreateDialogAsync();

        // An empty field says nothing: no pattern means no validation, which is the default
        // rather than a mistake.
        await Expect(Page.Locator("#validationRegexValid")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#validationRegexError")).ToHaveCountAsync(0);

        await EnterValidationRegexAsync("(unterminated", expectValid: false);
        // The regex parser's own explanation, not a generic "invalid" - it names the construct
        // that is wrong, which is the whole reason it is surfaced verbatim.
        await Expect(Page.Locator("#validationRegexError")).ToContainTextAsync("Not enough )");
        await Expect(Page.Locator("#validationRegexValid")).ToHaveCountAsync(0);

        await Page.Locator("#validationRegex").ClearAsync();
        await EnterValidationRegexAsync("^[0-9]+$");
        await Expect(Page.Locator("#validationRegexError")).ToHaveCountAsync(0);

        await Page.Locator("#validationRegex").ClearAsync();
        await Expect(Page.Locator("#validationRegexValid")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ValidationRegex_TriesASampleValueAgainstThePattern()
    {
        await GotoAsync("/ui/namespaces");
        await OpenCreateDialogAsync();
        await EnterValidationRegexAsync("^[0-9]+$");

        // Nothing to report until there is something to test. Asserted only after the verdict
        // above has landed - before that the sample box does not exist either, so this would pass
        // against a DOM the update has not reached and prove nothing.
        await Expect(Page.Locator("#validationRegexSample")).ToBeVisibleAsync();
        await Expect(Page.Locator("#validationRegexSampleResult")).ToHaveCountAsync(0);

        await Page.FillAsync("#validationRegexSample", "12345");
        await Expect(Page.Locator("#validationRegexSampleResult"))
            .ToContainTextAsync("Would be accepted");

        await Page.FillAsync("#validationRegexSample", "12a45");
        await Expect(Page.Locator("#validationRegexSampleResult"))
            .ToContainTextAsync("Would be rejected");
    }

    [Fact]
    public async Task ValidationRegex_MakesAnUnanchoredPatternsSubstringMatchVisible()
    {
        // The mistake this box exists for: a pattern that looks like "digits only" but, being
        // unanchored, accepts any value that merely contains a digit. Nothing about the pattern
        // itself is wrong, so only trying a value against it shows the problem - and it shows it
        // before the namespace is created around it rather than after.
        await GotoAsync("/ui/namespaces");
        await OpenCreateDialogAsync();
        await EnterValidationRegexAsync("[0-9]+");
        await Page.FillAsync("#validationRegexSample", "abc123");

        await Expect(Page.Locator("#validationRegexSampleResult"))
            .ToContainTextAsync("Would be accepted");
    }

    [Fact]
    public async Task ValidationRegex_VerdictSurvivesDismissingTheDialogWithThePatternItDescribes()
    {
        // The create form keeps what was typed when it is dismissed - every field does, and the
        // duplicate-name flow above relies on it. The checker has to follow the pattern rather
        // than reset independently of it, or reopening would show a pattern with no verdict
        // beside it and it would read as unchecked.
        await GotoAsync("/ui/namespaces");
        await OpenCreateDialogAsync();
        await EnterValidationRegexAsync("^[0-9]+$");
        await Page.FillAsync("#validationRegexSample", "12345");
        await Expect(Page.Locator("#validationRegexSampleResult")).ToBeVisibleAsync();

        await CloseCreateDialogAsync();
        await OpenCreateDialogAsync();

        await Expect(Page.Locator("#validationRegex")).ToHaveValueAsync("^[0-9]+$");
        await Expect(Page.Locator("#validationRegexValid")).ToBeVisibleAsync();
        await Expect(Page.Locator("#validationRegexSampleResult"))
            .ToContainTextAsync("Would be accepted");
    }

    [Fact]
    public async Task ValidationRegex_CheckerIsClearOnTheDialogAfterASuccessfulCreate()
    {
        // A successful create blanks the form, so the next dialog must not open still showing the
        // last namespace's pattern verdict or the value it was tried against.
        await GotoAsync("/ui/namespaces");
        await OpenCreateDialogAsync();
        await Page.FillAsync("#name", UniqueName());
        await EnterValidationRegexAsync("^[0-9]+$");
        await Page.FillAsync("#validationRegexSample", "12345");
        await Expect(Page.Locator("#validationRegexSampleResult")).ToBeVisibleAsync();
        await SubmitCreateFormAsync();
        await Expect(Page.Locator("#name")).ToHaveCountAsync(0);

        await OpenCreateDialogAsync();

        await Expect(Page.Locator("#validationRegex")).ToHaveValueAsync(string.Empty);
        await Expect(Page.Locator("#validationRegexValid")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#validationRegexSampleResult")).ToHaveCountAsync(0);

        await EnterValidationRegexAsync("^[0-9]+$");
        await Expect(Page.Locator("#validationRegexSample")).ToHaveValueAsync(string.Empty);
    }

    /// <summary>
    /// Types a validation-regex pattern into the create dialog and waits for the checker's verdict
    /// to come back before returning.
    ///
    /// Typed character by character rather than set with FillAsync, for the same reason the
    /// feature exists: the verdict is recomputed per keystroke, over the Blazor circuit. FillAsync
    /// dispatches exactly one `input` event for the whole string, so that single round trip
    /// becomes the test's single point of failure - and on a CPU-starved runner, losing it leaves
    /// every later step waiting on an element the server was never told to render. Typing carries
    /// the full value on every keystroke, so a dropped intermediate event is corrected by the next
    /// one, exactly as it would be for someone typing this in.
    ///
    /// Waiting for the verdict is the other half: it is what makes the following step act on a
    /// DOM the round trip has actually reached, rather than racing the render that creates the
    /// elements it needs.
    /// </summary>
    private async Task EnterValidationRegexAsync(string pattern, bool expectValid = true)
    {
        await Page.Locator("#validationRegex").PressSequentiallyAsync(pattern);

        await Expect(Page.Locator(expectValid ? "#validationRegexValid" : "#validationRegexError"))
            .ToBeVisibleAsync();
    }

    /// <summary>
    /// The create dialog's own submit button. "Create" on its own is ambiguous, since the header
    /// carries a "Create namespace" button to open the dialog in the first place.
    /// </summary>
    private const string CreateSubmitSelector = "form:has(#name) button[type=submit]";

    private ILocator TreeNode(string namespaceName) =>
        Page.Locator($"[role=treeitem][data-value=\"{namespaceName}\"]");

    private async Task OpenCreateDialogAsync()
    {
        await Page.ClickAsync("#showCreateNamespaceFormButton");

        // Focused, not merely visible. The dialog asks for the name field to be focused from
        // OnAfterRenderAsync, over JS interop - so focus arriving is proof that the render which
        // opened the dialog has completed a full round trip, where visibility only means the
        // markup reached the browser while the dialog may still be animating and settling.
        // Anything typed before that can land on an element Blazor is still reconciling.
        await Expect(Page.Locator("#name")).ToBeFocusedAsync();
    }

    /// <summary>
    /// Dismisses a dialog that was never submitted. A successful create closes it on its own, so
    /// this is only for the cancel path.
    /// </summary>
    private async Task CloseCreateDialogAsync()
    {
        await Page.ClickAsync("#cancelCreateNamespaceButton");
        await Expect(Page.Locator("#name")).ToHaveCountAsync(0);
    }

    private async Task SubmitCreateFormAsync() => await Page.ClickAsync(CreateSubmitSelector);

    /// <summary>
    /// Opens the create dialog, fills it in and submits it, then waits for the namespace to
    /// appear in the tree and the dialog to close itself. Both waits matter: a successful create
    /// closes the dialog, so anything typed straight afterwards would land nowhere.
    /// </summary>
    private async Task CreateNamespaceAsync(
        string name,
        string? parent = null,
        string? description = null
    )
    {
        await OpenCreateDialogAsync();
        await Page.FillAsync("#name", name);

        if (description is not null)
        {
            await Page.FillAsync("#description", description);
        }

        if (parent is not null)
        {
            await Page.SelectOptionAsync("#parentName", parent);
        }

        await SubmitCreateFormAsync();
        await Expect(TreeNode(name)).ToHaveCountAsync(1);
        await Expect(Page.Locator("#name")).ToHaveCountAsync(0);
    }

    private async Task SelectNamespaceAsync(string namespaceName) =>
        await TreeNode(namespaceName).Locator("span.truncate").First.ClickAsync();

    private static string UniqueName() => $"e2e-ns-{UniqueSuffix()}";
}
