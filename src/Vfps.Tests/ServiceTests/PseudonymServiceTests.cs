using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;

namespace Vfps.Tests.ServiceTests;

public class PseudonymServiceTests : ServiceTestBase
{
    private readonly Services.PseudonymService sut;

    public PseudonymServiceTests()
    {
        sut = new Services.PseudonymService(
            InMemoryPseudonymContext,
            new PseudonymGenerators.PseudonymizationMethodsLookup(),
            new NamespaceRepository(InMemoryPseudonymContext),
            new PseudonymRepository(InMemoryPseudonymContext)
        );
    }

    [Fact]
    public async Task Get_WithNonExistingPseudonymValue_ShouldThrowNotFoundException()
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
    public async Task Get_WithExistingPseudonymValue_ShouldReturnIt()
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
    public async Task Create_ShouldSaveNewPseudonym()
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

        InMemoryPseudonymContext
            .Pseudonyms
            .Should()
            .Contain(
                p =>
                    p.OriginalValue == request.OriginalValue && p.NamespaceName == request.Namespace
            );
    }

    [Fact]
    public async Task Create_WithNonExistingNamespace_ShouldThrowNotFoundError()
    {
        var request = new PseudonymServiceCreateRequest
        {
            Namespace = "nonExistingNamespace",
            OriginalValue = nameof(Create_WithNonExistingNamespace_ShouldThrowNotFoundError),
        };

        await sut.Invoking(async s => await s.Create(request, TestServerCallContext.Create()))
            .Should()
            .ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task Create_CalledMultipleTimesWithTheSameOriginalValue_ShouldOnlyStoreOnePseudonym()
    {
        var request = new PseudonymServiceCreateRequest
        {
            Namespace = "emptyNamespace",
            OriginalValue = nameof(
                Create_CalledMultipleTimesWithTheSameOriginalValue_ShouldOnlyStoreOnePseudonym
            ),
        };

        var response = await sut.Create(request, TestServerCallContext.Create());
        var firstCreatedPseudonym = response.Pseudonym.PseudonymValue;
        InMemoryPseudonymContext
            .Pseudonyms
            .Where(p => p.NamespaceName == request.Namespace)
            .Should()
            .HaveCount(1);

        response = await sut.Create(request, TestServerCallContext.Create());
        response.Pseudonym.PseudonymValue.Should().Be(firstCreatedPseudonym);
        InMemoryPseudonymContext
            .Pseudonyms
            .Where(p => p.NamespaceName == request.Namespace)
            .Should()
            .HaveCount(1);

        response = await sut.Create(request, TestServerCallContext.Create());
        response.Pseudonym.PseudonymValue.Should().Be(firstCreatedPseudonym);
        InMemoryPseudonymContext
            .Pseudonyms
            .Where(p => p.NamespaceName == request.Namespace)
            .Should()
            .HaveCount(1);
    }

    [Fact]
    public async Task Create_WithCachingNamespaceRepository_ShouldBeFaster()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 });

        var cachingSut = new Services.PseudonymService(
            InMemoryPseudonymContext,
            new PseudonymGenerators.PseudonymizationMethodsLookup(),
            new CachingNamespaceRepository(
                InMemoryPseudonymContext,
                cache,
                new Config.CacheConfig()
            ),
            new PseudonymRepository(InMemoryPseudonymContext)
        );

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

    [Fact]
    public async Task Create_WithCachingNamespaceRepositoryAndCachingPseudonymRepository_ShouldBeFaster()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 32 });

        var cachingSut = new Services.PseudonymService(
            InMemoryPseudonymContext,
            new PseudonymGenerators.PseudonymizationMethodsLookup(),
            new CachingNamespaceRepository(
                InMemoryPseudonymContext,
                cache,
                new Config.CacheConfig()
            ),
            new CachingPseudonymRepository(
                InMemoryPseudonymContext,
                cache,
                new Config.CacheConfig()
            )
        );

        var request = new PseudonymServiceCreateRequest
        {
            Namespace = "existingNamespace",
            OriginalValue = nameof(
                Create_WithCachingNamespaceRepositoryAndCachingPseudonymRepository_ShouldBeFaster
            ),
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

    [Fact]
    public async Task List_WithEmptyNamespace_ShouldReturnEmptyList()
    {
        var request = new PseudonymServiceListRequest { Namespace = "emptyNamespace", };

        var response = await sut.List(request, TestServerCallContext.Create());

        response.Namespace.Should().Be(request.Namespace);
        response.Pseudonyms.Should().BeEmpty();
    }

    [Fact]
    public async Task List_WithNonExistingNamespace_ShouldThrowNotFoundError()
    {
        var request = new PseudonymServiceListRequest { Namespace = "nonExistingNamespace", };

        await sut.Invoking(async s => await s.List(request, TestServerCallContext.Create()))
            .Should()
            .ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task List_WithExistingNonEmptyNamespace_ShouldReturnAllPseudonyms()
    {
        var request = new PseudonymServiceListRequest { Namespace = "existingNamespace", };

        var response = await sut.List(request, TestServerCallContext.Create());

        response.Pseudonyms.Should().HaveSameCount(InMemoryPseudonymContext.Pseudonyms);
    }

    [Fact(
        Skip = "there's an issue with timezones in SQLite vs. DateTimeOffset.UtcNow. This causes L152 in PseudonymService.cs to never find any value."
    )]
    public async Task List_WithMoreItemsThanPageSize_ShouldReturnAllPseudonymsViaPaging()
    {
        var namespaceName = "emptyNamespace";
        var pseudonymsToCreateCount = 99;

        for (int i = 0; i < pseudonymsToCreateCount; i++)
        {
            var createRequest = new PseudonymServiceCreateRequest
            {
                Namespace = namespaceName,
                OriginalValue =
                    nameof(List_WithMoreItemsThanPageSize_ShouldReturnAllPseudonymsViaPaging) + i,
            };

            await sut.Create(createRequest, TestServerCallContext.Create());
        }

        InMemoryPseudonymContext
            .Pseudonyms
            .Where(p => p.NamespaceName == namespaceName)
            .Count()
            .Should()
            .Be(pseudonymsToCreateCount);

        var request = new PseudonymServiceListRequest
        {
            Namespace = namespaceName,
            IncludeTotalSize = true,
            PageSize = 5,
        };

        var response = await sut.List(request, TestServerCallContext.Create());

        var allPseudonyms = new List<Pseudonym>();

        response.TotalSize.Should().Be(pseudonymsToCreateCount);
        response.Pseudonyms.Should().HaveCount(request.PageSize);

        allPseudonyms.AddRange(response.Pseudonyms);

        while (!string.IsNullOrEmpty(response.NextPageToken))
        {
            request.PageToken = response.NextPageToken;

            response = await sut.List(request, TestServerCallContext.Create());

            allPseudonyms.AddRange(response.Pseudonyms);
        }

        allPseudonyms.Should().HaveCount(pseudonymsToCreateCount);
    }
}
