using Grpc.Core;

namespace Vfps.Tests.ServiceTests;

public class PseudonymServiceDeleteTests : ServiceTestBase
{
    private readonly Services.PseudonymService sut;

    public PseudonymServiceDeleteTests()
    {
        sut = new Services.PseudonymService(
            InMemoryPseudonymContext,
            new PseudonymGenerators.PseudonymizationMethodsLookup(),
            new NamespaceRepository(InMemoryPseudonymContext),
            new PseudonymRepository(InMemoryPseudonymContext)
        );
    }

    [Fact]
    public async Task Delete_WithExistingPseudonym_ShouldRemoveIt()
    {
        var request = new PseudonymServiceDeleteRequest
        {
            Namespace = "existingNamespace",
            PseudonymValue = "existingPseudonym",
        };

        var response = await sut.Delete(request, TestServerCallContext.Create());

        response.Should().NotBeNull();
        InMemoryPseudonymContext
            .Pseudonyms.Should()
            .NotContain(p => p.PseudonymValue == "existingPseudonym");
    }

    [Fact]
    public async Task Delete_WithNonExistingPseudonym_ShouldThrowNotFoundException()
    {
        var request = new PseudonymServiceDeleteRequest
        {
            Namespace = "existingNamespace",
            PseudonymValue = "doesNotExist",
        };

        var act = () => sut.Delete(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_WithPseudonymInDifferentNamespace_ShouldThrowNotFoundException()
    {
        var request = new PseudonymServiceDeleteRequest
        {
            Namespace = "emptyNamespace",
            PseudonymValue = "existingPseudonym",
        };

        var act = () => sut.Delete(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }
}
