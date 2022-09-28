using FluentAssertions;
using Grpc.Core;
using Vfps.Data;
using Vfps.Protos;

namespace Vfps.Tests.ServiceTests;

public class PseudonymServiceTests : ServiceTestBase
{
    private readonly Services.PseudonymService sut;

    public PseudonymServiceTests() : base()
    {
        sut = new Services.PseudonymService(
            InMemoryPseudonymContext,
            new PseudonymGenerators.PseudonymizationMethodsLookup(),
            new NamespaceRepository(InMemoryPseudonymContext));
    }

    [Fact]
    public async void Get_WithNonExistingPseudonymValue_ShouldThrowNotFoundException()
    {
        var request = new PseudonymServiceGetRequest
        {
            Namespace = "existingNamespace",
            PseudonymValue = "notExisting",
        };

        var act = () => sut.Get(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async void Get_WithExistingPseudonymValue_ShouldReturnIt()
    {
        var request = new PseudonymServiceGetRequest
        {
            Namespace = "existingNamespace",
            PseudonymValue = "existingPseudonym",
        };

        var response = await sut.Get(request, TestServerCallContext.Create());

        response.Pseudonym.PseudonymValue.Should().Be(request.PseudonymValue);
        response.Pseudonym.OriginalValue.Should().Be("an original value");
    }

    [Fact]
    public async void Create_ShouldSaveNewPseudonym()
    {
        var request = new PseudonymServiceCreateRequest
        {
            Namespace = "existingNamespace",
            OriginalValue = nameof(Create_ShouldSaveNewPseudonym),
        };

        var response = await sut.Create(request, TestServerCallContext.Create());

        response.Pseudonym.Namespace.Should().Be(request.Namespace);
        response.Pseudonym.OriginalValue.Should().Be(request.OriginalValue);
        response.Pseudonym.PseudonymValue.Should().NotBeNull(request.OriginalValue);

        InMemoryPseudonymContext.Pseudonyms
            .Should()
            .Contain(p => p.OriginalValue == request.OriginalValue && p.NamespaceName == request.Namespace);
    }

    [Fact]
    public async void Create_CalledMultipleTimesWithTheSameOriginalValue_ShouldOnlyStoreOnePseudonym()
    {
        var request = new PseudonymServiceCreateRequest
        {
            Namespace = "emptyNamespace",
            OriginalValue = nameof(Create_CalledMultipleTimesWithTheSameOriginalValue_ShouldOnlyStoreOnePseudonym),
        };

        var response = await sut.Create(request, TestServerCallContext.Create());
        var firstCreatedPseudonym = response.Pseudonym.PseudonymValue;
        InMemoryPseudonymContext.Pseudonyms.Where(p => p.NamespaceName == request.Namespace).Should().HaveCount(1);

        response = await sut.Create(request, TestServerCallContext.Create());
        response.Pseudonym.PseudonymValue.Should().Be(firstCreatedPseudonym);
        InMemoryPseudonymContext.Pseudonyms.Where(p => p.NamespaceName == request.Namespace).Should().HaveCount(1);

        response = await sut.Create(request, TestServerCallContext.Create());
        response.Pseudonym.PseudonymValue.Should().Be(firstCreatedPseudonym);
        InMemoryPseudonymContext.Pseudonyms.Where(p => p.NamespaceName == request.Namespace).Should().HaveCount(1);
    }
}
