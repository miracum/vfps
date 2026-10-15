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
                NamespaceName = "existingNamespace",
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
                new ProjectServiceAddNamespaceRequest { Name = "study", NamespaceName = "nope" },
                Context()
            );

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StatusCode.Should()
            .Be(StatusCode.NotFound);
    }
}
