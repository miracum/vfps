namespace Vfps.PlaywrightTests;

public class ProjectsTests(PlaywrightFixture fixture) : VfpsPageTestBase(fixture)
{
    /// <summary>
    /// Setup goes through the REST API rather than the UI under test, so each test starts from
    /// exactly the state it needs and fails for its own reason only.
    /// </summary>
    private async Task CreateNamespaceViaApiAsync(string name)
    {
        var response = await Context.APIRequest.PostAsync(
            $"{PlaywrightFixture.BaseUrl}/v1/namespaces",
            new APIRequestContextOptions
            {
                DataObject = new
                {
                    name,
                    pseudonymGenerationMethod = "PSEUDONYM_GENERATION_METHOD_FULL_RANDOM_BASE62_ENCODED",
                    pseudonymLength = 16,
                },
            }
        );
        response.Ok.Should().BeTrue(await response.TextAsync());
    }

    private async Task CreateProjectViaApiAsync(string name, params string[] namespaces)
    {
        var response = await Context.APIRequest.PostAsync(
            $"{PlaywrightFixture.BaseUrl}/v1/projects",
            new APIRequestContextOptions { DataObject = new { name } }
        );
        response.Ok.Should().BeTrue(await response.TextAsync());

        foreach (var @namespace in namespaces)
        {
            var added = await Context.APIRequest.PutAsync(
                $"{PlaywrightFixture.BaseUrl}/v1/projects/{name}/namespaces/{@namespace}"
            );
            added.Ok.Should().BeTrue(await added.TextAsync());
        }
    }

    private ILocator ProjectCard(string projectName) =>
        Page.Locator($"[data-project-card=\"{projectName}\"]");

    private ILocator TreeNode(string namespaceName) =>
        Page.Locator($"[role=treeitem][data-value=\"{namespaceName}\"]");

    private static string UniqueProject() => $"e2e-project-{UniqueSuffix()}";

    private static string UniqueNamespace() => $"e2e-ns-{UniqueSuffix()}";

    [Fact]
    public async Task CreateProject_AppearsInTheList_AndAClearedDescriptionStaysOut()
    {
        var name = UniqueProject();

        await GotoAsync("/ui/projects");
        await Page.ClickAsync("#showCreateProjectFormButton");
        await Page.FillAsync("#projectName", name);
        await Page.FillAsync("#projectDescription", "the study's namespaces");
        await Page.ClickAsync("#saveProjectButton");

        await Expect(ProjectCard(name)).ToHaveCountAsync(1);
        await Expect(ProjectCard(name)).ToContainTextAsync("the study's namespaces");
        await Expect(Page.Locator("#projectName")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task CreateDuplicateProject_ShowsAFriendlyErrorInTheDialog()
    {
        var name = UniqueProject();
        await CreateProjectViaApiAsync(name);

        await GotoAsync("/ui/projects");
        await Page.ClickAsync("#showCreateProjectFormButton");
        await Page.FillAsync("#projectName", name);
        await Page.ClickAsync("#saveProjectButton");

        await Expect(Page.Locator("body")).ToContainTextAsync("already exists");
        await Expect(Page.Locator("#projectName")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task ManageNamespaces_AddsAndRemovesMembers()
    {
        var project = UniqueProject();
        var first = UniqueNamespace();
        var second = UniqueNamespace();
        await CreateNamespaceViaApiAsync(first);
        await CreateNamespaceViaApiAsync(second);
        await CreateProjectViaApiAsync(project, first);

        await GotoAsync("/ui/projects");
        var card = ProjectCard(project);
        await Expect(card).ToContainTextAsync(first);
        await Expect(card).Not.ToContainTextAsync(second);

        // Swap one member for the other.
        await card.Locator("[data-action=manage-namespaces]").ClickAsync();
        await Page.ClickAsync($"[id=\"projectNamespace-{first}\"]");
        await Page.ClickAsync($"[id=\"projectNamespace-{second}\"]");
        await Page.ClickAsync("#saveManageProjectButton");

        await Expect(Page.Locator("#saveManageProjectButton")).ToHaveCountAsync(0);
        await Expect(card).ToContainTextAsync(second);
        await Expect(card).Not.ToContainTextAsync(first);
    }

    [Fact]
    public async Task DeleteProject_RemovesItButNotItsNamespaces()
    {
        var project = UniqueProject();
        var @namespace = UniqueNamespace();
        await CreateNamespaceViaApiAsync(@namespace);
        await CreateProjectViaApiAsync(project, @namespace);

        await GotoAsync("/ui/projects");
        await ProjectCard(project).Locator("[data-action=delete-project]").ClickAsync();
        await Page.ClickAsync("#confirmDeleteProjectButton");

        await Expect(ProjectCard(project)).ToHaveCountAsync(0);

        await GotoAsync("/ui/namespaces");
        await Expect(TreeNode(@namespace)).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task NamespacesPage_ShowsProjectBadges_AndFiltersByProject()
    {
        var project = UniqueProject();
        var inProject = UniqueNamespace();
        var other = UniqueNamespace();
        await CreateNamespaceViaApiAsync(inProject);
        await CreateNamespaceViaApiAsync(other);
        await CreateProjectViaApiAsync(project, inProject);

        await GotoAsync("/ui/namespaces");
        await Expect(TreeNode(inProject).Locator("[data-project-badge]").First)
            .ToHaveTextAsync(project);
        await Expect(TreeNode(other).Locator("[data-project-badge]")).ToHaveCountAsync(0);

        await Page.Locator("#namespaceProjectFilter").SelectOptionAsync($"project:{project}");
        await Expect(TreeNode(inProject)).ToHaveCountAsync(1);
        await Expect(TreeNode(other)).ToHaveCountAsync(0);

        await Page.Locator("#namespaceProjectFilter").SelectOptionAsync("none");
        await Expect(TreeNode(inProject)).ToHaveCountAsync(0);
        await Expect(TreeNode(other)).ToHaveCountAsync(1);

        await Page.Locator("#namespaceProjectFilter").SelectOptionAsync("");
        await Expect(TreeNode(inProject)).ToHaveCountAsync(1);
        await Expect(TreeNode(other)).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task CreateNamespace_WithSeveralProjects_PutsItIntoAllOfThem()
    {
        var first = UniqueProject();
        var second = UniqueProject();
        var name = UniqueNamespace();
        await CreateProjectViaApiAsync(first);
        await CreateProjectViaApiAsync(second);

        await GotoAsync("/ui/namespaces");
        await Page.ClickAsync("#showCreateNamespaceFormButton");
        await Expect(Page.Locator("#name")).ToBeFocusedAsync();
        await Page.FillAsync("#name", name);
        await Page.ClickAsync($"[id=\"newNamespaceProject-{first}\"]");
        await Page.ClickAsync($"[id=\"newNamespaceProject-{second}\"]");
        await Page.ClickAsync("form:has(#name) button[type=submit]");

        await Expect(TreeNode(name)).ToHaveCountAsync(1);
        await Expect(TreeNode(name).Locator("[data-project-badge]")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task EditNamespace_ChangesItsProjects()
    {
        var first = UniqueProject();
        var second = UniqueProject();
        var name = UniqueNamespace();
        await CreateNamespaceViaApiAsync(name);
        await CreateProjectViaApiAsync(first, name);
        await CreateProjectViaApiAsync(second);

        await GotoAsync("/ui/namespaces");
        await TreeNode(name).Locator("[data-namespace-label]").First.ClickAsync();
        await Page.ClickAsync("#editNamespaceButton");

        // Stored memberships are pre-checked.
        await Expect(Page.Locator($"[id=\"editNamespaceProject-{first}\"]"))
            .ToHaveAttributeAsync("aria-checked", "true");

        await Page.ClickAsync($"[id=\"editNamespaceProject-{first}\"]");
        await Page.ClickAsync($"[id=\"editNamespaceProject-{second}\"]");
        await Page.ClickAsync("#saveNamespaceButton");

        await Expect(Page.Locator("#editDescription")).ToHaveCountAsync(0);
        var badges = TreeNode(name).Locator("[data-project-badge]");
        await Expect(badges).ToHaveCountAsync(1);
        await Expect(badges.First).ToHaveTextAsync(second);
        await Expect(Page.Locator("#namespaceDetails")).ToContainTextAsync(second);
    }

    [Fact]
    public async Task NamespaceInManyProjects_KeepsItsNameVisibleAndCollapsesTheExtraBadges()
    {
        var name = UniqueNamespace();
        await CreateNamespaceViaApiAsync(name);
        // Long names on purpose: it is a tight row that used to squeeze the namespace name out.
        var projects = Enumerable
            .Range(0, 4)
            .Select(i => $"{UniqueProject()}-with-a-rather-long-project-name-{i}")
            .ToArray();
        foreach (var project in projects)
        {
            await CreateProjectViaApiAsync(project, name);
        }

        await GotoAsync("/ui/namespaces");

        var node = TreeNode(name);
        await Expect(node.Locator("[data-namespace-label]").First).ToBeVisibleAsync();
        await Expect(node.Locator("[data-project-badge]")).ToHaveCountAsync(2);
        await Expect(node.Locator("[data-project-overflow]")).ToHaveTextAsync("+2");

        // Everything is still reachable: the details panel lists every project.
        await node.Locator("[data-namespace-label]").First.ClickAsync();
        foreach (var project in projects)
        {
            await Expect(Page.Locator("#namespaceDetails")).ToContainTextAsync(project);
        }
    }
}
