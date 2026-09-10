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

        await Page.ClickAsync("#showCreateNamespaceFormButton");
        await Expect(Page.Locator("#name")).ToBeVisibleAsync();
        // Focused, not merely present, so typing can start straight away.
        await Expect(Page.Locator("#name")).ToBeFocusedAsync();

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
        await Expect(Page.Locator("#name")).ToBeVisibleAsync();
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
