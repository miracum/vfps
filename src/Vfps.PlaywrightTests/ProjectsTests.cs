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

    private ILocator Details => Page.Locator("#namespaceDetails");

    private async Task SelectNamespaceAsync(string namespaceName) =>
        await TreeNode(namespaceName).Locator("[data-namespace-label]").First.ClickAsync();

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
    public async Task NamespacesPage_ShowsProjectsInDetails_AndFiltersByProject()
    {
        var project = UniqueProject();
        var inProject = UniqueNamespace();
        var other = UniqueNamespace();
        await CreateNamespaceViaApiAsync(inProject);
        await CreateNamespaceViaApiAsync(other);
        await CreateProjectViaApiAsync(project, inProject);

        await GotoAsync("/ui/namespaces");
        // The tree itself carries no project markers - the namespace name is all it shows.
        await Expect(TreeNode(inProject)).ToHaveTextAsync(inProject);

        await SelectNamespaceAsync(inProject);
        await Expect(Details).ToContainTextAsync(project);
        await SelectNamespaceAsync(other);
        await Expect(Details).ToContainTextAsync(other);
        await Expect(Details).Not.ToContainTextAsync(project);

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
        await SelectNamespaceAsync(name);
        await Expect(Details).ToContainTextAsync(first);
        await Expect(Details).ToContainTextAsync(second);
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
        await Expect(Details).ToContainTextAsync(second);
        await Expect(Details).Not.ToContainTextAsync(first);
    }

    [Fact]
    public async Task NamespaceInManyProjects_ListsEveryProjectInItsDetails_AndLeavesTheTreeRowAlone()
    {
        var name = UniqueNamespace();
        await CreateNamespaceViaApiAsync(name);
        // Long names on purpose: a crowded row used to squeeze the namespace name out.
        var projects = Enumerable
            .Range(0, 4)
            .Select(i => $"{UniqueProject()}-with-a-rather-long-project-name-{i}")
            .ToArray();
        foreach (var project in projects)
        {
            await CreateProjectViaApiAsync(project, name);
        }

        await GotoAsync("/ui/namespaces");

        // However many projects it is in, the row shows the namespace name and nothing else.
        await Expect(TreeNode(name).Locator("[data-namespace-label]").First).ToBeVisibleAsync();
        await Expect(TreeNode(name)).ToHaveTextAsync(name);

        await SelectNamespaceAsync(name);
        foreach (var project in projects)
        {
            await Expect(Details).ToContainTextAsync(project);
        }
    }

    [Fact]
    public async Task NamespacePill_ShowsALinkIcon_AndOpensTheNamespaceBrowser()
    {
        var project = UniqueProject();
        var @namespace = UniqueNamespace();
        await CreateNamespaceViaApiAsync(@namespace);
        await CreateProjectViaApiAsync(project, @namespace);

        await GotoAsync("/ui/projects");
        var pill = ProjectCard(project).Locator($"[data-namespace-link=\"{@namespace}\"]");

        // The icon is what tells the pill apart from a plain label.
        await Expect(pill.Locator("svg")).ToHaveCountAsync(1);

        await pill.ClickAsync();
        await Expect(Page)
            .ToHaveURLAsync(
                new System.Text.RegularExpressions.Regex($"/ui/namespaces/{@namespace}/pseudonyms$")
            );
    }

    [Fact]
    public async Task ProjectTitle_OpensTheProjectsOwnPage_WhichShowsOnlyThatProject()
    {
        var project = UniqueProject();
        var other = UniqueProject();
        var @namespace = UniqueNamespace();
        await CreateNamespaceViaApiAsync(@namespace);
        await CreateProjectViaApiAsync(project, @namespace);
        await CreateProjectViaApiAsync(other);

        await GotoAsync("/ui/projects");
        await Page.ClickAsync($"[data-project-link=\"{project}\"]");

        await Expect(Page)
            .ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/ui/projects/{project}$"));
        await Expect(Page.Locator("h1")).ToHaveTextAsync(project);
        await Expect(ProjectCard(project)).ToHaveCountAsync(1);
        await Expect(ProjectCard(other)).ToHaveCountAsync(0);
        await Expect(ProjectCard(project)).ToContainTextAsync(@namespace);
    }

    [Fact]
    public async Task StaticProjectLink_CanBeOpenedDirectly()
    {
        var project = UniqueProject();
        await CreateProjectViaApiAsync(project);

        await GotoAsync($"/ui/projects/{project}");

        await Expect(Page.Locator("h1")).ToHaveTextAsync(project);
        await Expect(ProjectCard(project)).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task StaticProjectLink_ToAnUnknownProject_SaysSo()
    {
        await GotoAsync($"/ui/projects/{UniqueProject()}");

        await Expect(Page.Locator("body")).ToContainTextAsync("does not exist");
        await Expect(Page.Locator("[data-project-card]")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ProjectPillInNamespaceDetails_OpensTheProject()
    {
        var project = UniqueProject();
        var @namespace = UniqueNamespace();
        await CreateNamespaceViaApiAsync(@namespace);
        await CreateProjectViaApiAsync(project, @namespace);

        await GotoAsync("/ui/namespaces");
        await SelectNamespaceAsync(@namespace);
        var pill = Details.Locator($"[data-project-link=\"{project}\"]");
        await Expect(pill.Locator("svg")).ToHaveCountAsync(1);
        await pill.ClickAsync();

        await Expect(Page)
            .ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/ui/projects/{project}$"));
        await Expect(ProjectCard(project)).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task DeletingAProjectFromItsOwnPage_ReturnsToTheList()
    {
        var project = UniqueProject();
        await CreateProjectViaApiAsync(project);

        await GotoAsync($"/ui/projects/{project}");
        await ProjectCard(project).Locator("[data-action=delete-project]").ClickAsync();
        await Page.ClickAsync("#confirmDeleteProjectButton");

        await Expect(Page)
            .ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/ui/projects$"));
        await Expect(ProjectCard(project)).ToHaveCountAsync(0);
    }
}
