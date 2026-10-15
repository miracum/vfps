using Grpc.Core;

namespace Vfps.Tests.ServiceTests;

public class ProjectServiceTests : ServiceTestBase
{
    private readonly Services.ProjectService sut;

    public ProjectServiceTests()
    {
        sut = new Services.ProjectService(CreateProjectAppService());
    }

    private static ServerCallContext Context() =>
        TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Create_WithoutADescription_ShouldReturnItAbsent()
    {
        var response = await sut.Create(
            new ProjectServiceCreateRequest { Name = "study" },
            Context()
        );

        response.Project.Name.Should().Be("study");
        response.Project.HasDescription.Should().BeFalse();
        response.Project.NamespaceNames.Should().BeEmpty();
    }

    [Fact]
    public async Task AddNamespace_ThenGet_ShouldListTheNamespace()
    {
        await sut.Create(new ProjectServiceCreateRequest { Name = "study" }, Context());

        await sut.AddNamespace(
            new ProjectServiceAddNamespaceRequest
            {
                Name = "study",
                Namespace = "existingNamespace",
            },
            Context()
        );
        var response = await sut.Get(new ProjectServiceGetRequest { Name = "study" }, Context());

        response.Project.NamespaceNames.Should().Equal("existingNamespace");
    }

    [Fact]
    public async Task Create_WithAnExistingName_ShouldThrowAlreadyExists()
    {
        await sut.Create(new ProjectServiceCreateRequest { Name = "study" }, Context());

        var act = () => sut.Create(new ProjectServiceCreateRequest { Name = "study" }, Context());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.AlreadyExists);
    }

    [Fact]
    public async Task Get_WithAnUnknownProject_ShouldThrowNotFound()
    {
        var act = () => sut.Get(new ProjectServiceGetRequest { Name = "nope" }, Context());

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task AddNamespace_WithAnUnknownNamespace_ShouldThrowNotFound()
    {
        await sut.Create(new ProjectServiceCreateRequest { Name = "study" }, Context());

        var act = () =>
            sut.AddNamespace(
                new ProjectServiceAddNamespaceRequest { Name = "study", Namespace = "nope" },
                Context()
            );

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task List_ShouldReturnTheProjects()
    {
        await sut.Create(new ProjectServiceCreateRequest { Name = "study" }, Context());

        var response = await sut.List(new ProjectServiceListRequest(), Context());

        response.Projects.Select(p => p.Name).Should().Equal("study");
    }

    [Fact]
    public async Task Update_WithADescriptionMask_ShouldReplaceIt_AndClearItWhenAbsent()
    {
        await sut.Create(
            new ProjectServiceCreateRequest { Name = "study", Description = "before" },
            Context()
        );

        var changed = await sut.Update(
            new ProjectServiceUpdateRequest
            {
                Project = new Project { Name = "study", Description = "after" },
                UpdateMask = new Google.Protobuf.WellKnownTypes.FieldMask
                {
                    Paths = { "description" },
                },
            },
            Context()
        );
        changed.Project.Description.Should().Be("after");

        // Named in the mask but absent from the request: cleared.
        var cleared = await sut.Update(
            new ProjectServiceUpdateRequest
            {
                Project = new Project { Name = "study" },
                UpdateMask = new Google.Protobuf.WellKnownTypes.FieldMask
                {
                    Paths = { "description" },
                },
            },
            Context()
        );
        cleared.Project.HasDescription.Should().BeFalse();
    }

    [Fact]
    public async Task Update_WithoutAMaskOrAnyFieldSet_ShouldKeepTheDescription()
    {
        // The trap the old PUT had: an update that says nothing about the description must not
        // wipe it.
        await sut.Create(
            new ProjectServiceCreateRequest { Name = "study", Description = "kept" },
            Context()
        );

        var response = await sut.Update(
            new ProjectServiceUpdateRequest { Project = new Project { Name = "study" } },
            Context()
        );

        response.Project.Description.Should().Be("kept");
    }

    [Fact]
    public async Task Update_WithAMaskNamingTheNamespaces_ShouldThrowInvalidArgument()
    {
        await sut.Create(new ProjectServiceCreateRequest { Name = "study" }, Context());

        var act = () =>
            sut.Update(
                new ProjectServiceUpdateRequest
                {
                    Project = new Project
                    {
                        Name = "study",
                        NamespaceNames = { "existingNamespace" },
                    },
                    UpdateMask = new Google.Protobuf.WellKnownTypes.FieldMask
                    {
                        Paths = { "namespace_names" },
                    },
                },
                Context()
            );

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.InvalidArgument);
    }
}
