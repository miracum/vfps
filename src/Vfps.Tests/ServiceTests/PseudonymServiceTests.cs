using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;
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
    public async void Create_WithNonExistingNamespace_ShouldThrowNotFoundError()
    {
        var request = new PseudonymServiceCreateRequest
        {
            Namespace = "nonExistingNamespace",
            OriginalValue = nameof(Create_WithNonExistingNamespace_ShouldThrowNotFoundError),
        };

        await sut.Invoking(async s => await s.Create(request, TestServerCallContext.Create()))
            .Should()
            .ThrowAsync<RpcException>().Where(e => e.StatusCode == StatusCode.NotFound);
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

    [Fact]
    public async void Create_WithCachingNamespaceRepository_ShouldBeFaster()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 });

        var cachingSut = new Services.PseudonymService(
            InMemoryPseudonymContext,
            new PseudonymGenerators.PseudonymizationMethodsLookup(),
            new CachingNamespaceRepository(InMemoryPseudonymContext, cache, new Config.CacheConfig()));

        var request = new PseudonymServiceCreateRequest
        {
            Namespace = "existingNamespace",
            OriginalValue = nameof(Create_WithCachingNamespaceRepository_ShouldBeFaster),
        };

        var stopwatch = new Stopwatch();

        stopwatch.Start();
        await cachingSut.Create(request, TestServerCallContext.Create());
        stopwatch.Stop();
        var firstExecutionTime = stopwatch.Elapsed;

        stopwatch.Restart();
        await cachingSut.Create(request, TestServerCallContext.Create());
        stopwatch.Stop();
        var secondExecutionTime = stopwatch.Elapsed;

        secondExecutionTime.Should().BeLessThan(firstExecutionTime);
    }
}
