using FluentAssertions;
using Grpc.Core;
using Vfps.Protos;

namespace Vfps.Tests.ServiceTests;

public class NamespaceServiceTests : ServiceTestBase
{
    private readonly Services.NamespaceService sut;

    public NamespaceServiceTests() : base()
    {
        sut = new Services.NamespaceService(InMemoryPseudonymContext);
    }

    [Fact]
    public async void Get_WithExistingPseudonym_ShouldReturnNamespace()
    {
        var request = new NamespaceServiceGetRequest
        {
            Name = "existingNamespace",
        };

        var response = await sut.Get(request, TestServerCallContext.Create());

        response.Namespace.Name.Should().Be(request.Name);
    }

    [Fact]
    public async void GetAll_ShouldReturnAllNamespace()
    {
        var request = new NamespaceServiceGetAllRequest();

        var response = await sut.GetAll(request, TestServerCallContext.Create());

        response.Results.Should().HaveSameCount(InMemoryPseudonymContext.Namespaces);
    }

    [Fact]
    public async void Get_WithNonExistingNamespace_ShouldThrowNotFoundException()
    {
        var request = new NamespaceServiceGetRequest
        {
            Name = "notExisting",
        };

        var act = () => sut.Get(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async void Create_ShouldSaveNewNamespace()
    {
        var request = new NamespaceServiceCreateRequest
        {
            Name = nameof(Create_ShouldSaveNewNamespace),
            PseudonymLength = 16,
        };

        var response = await sut.Create(request, TestServerCallContext.Create());

        response.Namespace.Name.Should().Be(request.Name);

        InMemoryPseudonymContext.Namespaces.Should().Contain(n => n.Name == request.Name);
    }

    [Fact]
    public async void Create_WithPseudonymLengthZero_ShouldFail()
    {
        var request = new NamespaceServiceCreateRequest
        {
            Name = nameof(Create_WithPseudonymLengthZero_ShouldFail),
            PseudonymLength = 0
        };

        var act = () => sut.Create(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.OutOfRange);
    }

    [Fact]
    public async void Delete_WithExistingNamespace_ShouldDeleteNamespace()
    {
        var createRequest = new NamespaceServiceCreateRequest
        {
            Name = "toBeDeleted",
            PseudonymLength = 16,
        };

        await sut.Create(createRequest, TestServerCallContext.Create());

        var deleteRequest = new NamespaceServiceDeleteRequest
        {
            Name = "toBeDeleted",
        };

        await sut.Delete(deleteRequest, TestServerCallContext.Create());

        InMemoryPseudonymContext.Namespaces.Should().NotContain(n => n.Name == deleteRequest.Name);
    }

    [Fact]
    public async void Delete_WithNonExistingNamespace_ShouldThrowNotFoundException()
    {
        var request = new NamespaceServiceDeleteRequest
        {
            Name = "notExisting",
        };

        var act = () => sut.Delete(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }
}
