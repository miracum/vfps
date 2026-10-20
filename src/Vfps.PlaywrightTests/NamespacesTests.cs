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

        // Assert the pseudonyms page rendered, rather than scanning the body for "404", which is
        // what this used to do and which was wrong twice over.
        //
        // Both pages are InteractiveServer, so clicking the link routes over the circuit rather
        // than issuing a request: the address moves on pushState and the new markup arrives over
        // the socket after it. The URL assertion above is satisfied by the first half of that, and
        // the body read was a single shot with no retry, so on a loaded runner it could still be
        // looking at the namespaces page it came from - which lists every namespace in the
        // deployment, each ending in twelve random hex characters. About one suffix in four
        // hundred contains the literal "404", and the CI failure that prompted this was exactly
        // that: an unrelated "e2e-ns-32bfca540457" sitting in the tree.
        //
        // Matching this page instead of the absence of a string removes both halves at once -
        // these assertions retry, so they also wait for the render the URL alone doesn't prove.
        await Expect(Page.Locator("#originalValue")).ToBeVisibleAsync();

        // The heading is what actually pins the regression down: the name has to come back out of
        // the route parameter decoded, not as the escaped form the address bar shows. A page that
        // merely rendered would satisfy the locator above even if the name had not survived.
        await Expect(
                Page.GetByRole(
                    AriaRole.Heading,
                    new PageGetByRoleOptions { Name = $"Pseudonyms in {name}" }
                )
            )
            .ToBeVisibleAsync();
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
    public async Task EditDescription_UpdatesTheDetailsPanel_AndClearingItRemovesTheDescription()
    {
        var name = UniqueName();

        await GotoAsync("/ui/namespaces");
        await CreateNamespaceAsync(name, description: "the original description");
        await SelectNamespaceAsync(name);

        var details = Page.Locator("#namespaceDetails");
        var input = Page.Locator("#editDescription");

        await Page.ClickAsync("#editNamespaceButton");
        // Focused, so the new text can be typed straight away - and used as the settle signal
        // the same way OpenCreateDialogAsync uses the name field's focus.
        await Expect(input).ToBeFocusedAsync();
        await Expect(input).ToHaveValueAsync("the original description");

        await input.FillAsync("the edited description");
        await Page.ClickAsync("#saveNamespaceButton");

        await Expect(input).ToHaveCountAsync(0);
        await Expect(details).ToContainTextAsync("the edited description");
        await Expect(details).Not.ToContainTextAsync("the original description");

        await Page.ClickAsync("#editNamespaceButton");
        await Expect(input).ToBeFocusedAsync();
        await input.FillAsync("");
        await Page.ClickAsync("#saveNamespaceButton");

        await Expect(input).ToHaveCountAsync(0);
        await Expect(details).Not.ToContainTextAsync("the edited description");
    }

    [Fact]
    public async Task EditValidationRegex_ChecksThePatternAsItIsTyped_ThenShowsTheSavedPattern()
    {
        var name = UniqueName();

        await GotoAsync("/ui/namespaces");
        await CreateNamespaceAsync(name);
        await SelectNamespaceAsync(name);

        await Page.ClickAsync("#editNamespaceButton");
        await Expect(Page.Locator("#editDescription")).ToBeFocusedAsync();

        // The same checker as the create form's, under the edit dialog's own ids.
        var regex = Page.Locator("#editValidationRegex");
        await FillUntilReportedAsync(
            regex,
            "(unterminated",
            Page.Locator("#editValidationRegexError")
        );
        await Expect(Page.Locator("#editValidationRegexError")).ToContainTextAsync("Not enough )");

        await regex.ClearAsync();
        await regex.FillAsync("^[0-9]+$");
        await Expect(Page.Locator("#editValidationRegexValid")).ToBeVisibleAsync();
        await FillUntilReportedAsync(
            Page.Locator("#editValidationRegexSample"),
            "12a45",
            Page.Locator("#editValidationRegexSampleResult")
        );
        await Expect(Page.Locator("#editValidationRegexSampleResult"))
            .ToContainTextAsync("Would be rejected");

        await Page.ClickAsync("#saveNamespaceButton");

        await Expect(Page.Locator("#editDescription")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#namespaceDetails")).ToContainTextAsync("^[0-9]+$");

        // Reopening starts from what is stored, with the checker's verdict beside it.
        await Page.ClickAsync("#editNamespaceButton");
        await Expect(regex).ToHaveValueAsync("^[0-9]+$");
        await Expect(Page.Locator("#editValidationRegexValid")).ToBeVisibleAsync();
        await Expect(Page.Locator("#editValidationRegexSampleResult")).ToHaveCountAsync(0);
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

        await EnterValidationRegexSampleAsync("12345");
        await Expect(Page.Locator("#validationRegexSampleResult"))
            .ToContainTextAsync("Would be accepted");

        // A plain fill from here on: the verdict above proves the field is already reporting.
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
        await EnterValidationRegexSampleAsync("abc123");

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
        await EnterValidationRegexSampleAsync("12345");

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
        await EnterValidationRegexSampleAsync("12345");
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
    /// Enters a validation-regex pattern into the create dialog and waits for the checker's
    /// verdict to come back before returning.
    /// </summary>
    private Task EnterValidationRegexAsync(string pattern, bool expectValid = true) =>
        FillUntilReportedAsync(
            Page.Locator("#validationRegex"),
            pattern,
            Page.Locator(expectValid ? "#validationRegexValid" : "#validationRegexError")
        );

    /// <summary>
    /// Enters a value to try against the pattern and waits for the would-be-accepted/rejected
    /// verdict, so callers can assert on its text right away.
    /// </summary>
    private Task EnterValidationRegexSampleAsync(string sample) =>
        FillUntilReportedAsync(
            Page.Locator("#validationRegexSample"),
            sample,
            Page.Locator("#validationRegexSampleResult")
        );

    /// <summary>
    /// Fills a debounced BbInput and waits for the server's reaction to it, re-announcing the
    /// field's value until that reaction shows up.
    ///
    /// A BbInput only reports typing once its own OnAfterRenderAsync has attached the JS input
    /// listener, over a separate interop call - and nothing in the DOM says when that has
    /// happened. Anything filled in before then sits in the field but never reaches the server,
    /// and with nothing else typed afterwards no later event ever carries it. None of the
    /// waits available beforehand rule that out: the create dialog's name-field focus is
    /// requested from the page's OnAfterRenderAsync, which Blazor runs before its children's, so
    /// it lands ahead of the regex field's listener rather than after it; and the sample field
    /// is only rendered by the very verdict that precedes filling it.
    ///
    /// Dispatching `input` again is what a further keystroke would do, without changing the
    /// value: the listener, once attached, reads the field and reports all of it. If the first
    /// report did get through, the repeat just sends the same value again and changes nothing.
    /// </summary>
    private async Task FillUntilReportedAsync(ILocator field, string value, ILocator reaction)
    {
        await field.FillAsync(value);

        // Long enough for the 500 ms debounce plus a slow round trip, so a report that is merely
        // late is not mistaken for one that was never sent.
        for (var retry = 0; retry < 2; retry++)
        {
            try
            {
                await Expect(reaction).ToBeVisibleAsync(new() { Timeout = 2_000 });
                return;
            }
            catch (PlaywrightException)
            {
                await field.DispatchEventAsync("input");
            }
        }

        await Expect(reaction).ToBeVisibleAsync();
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
        await TreeNode(namespaceName).Locator("[data-namespace-label]").First.ClickAsync();

    private static string UniqueName() => $"e2e-ns-{UniqueSuffix()}";
}
