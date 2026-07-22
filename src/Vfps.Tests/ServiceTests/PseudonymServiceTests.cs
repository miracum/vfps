using Grpc.Core;

namespace Vfps.Tests.ServiceTests;

public class PseudonymServiceTests : ServiceTestBase
{
    private readonly Services.PseudonymService sut;

    public PseudonymServiceTests()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        sut = new Services.PseudonymService(
            CreatePseudonymAppService(namespaceRepository, pseudonymRepository)
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

        var response = await sut.Get(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

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

        var response = await sut.Create(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Pseudonym.Namespace.Should().Be(request.Namespace);
        response.Pseudonym.OriginalValue.Should().Be(request.OriginalValue);
        response.Pseudonym.PseudonymValue.Should().NotBeNull(request.OriginalValue);

        InMemoryPseudonymContext
            .Pseudonyms.Should()
            .Contain(p =>
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
    public async Task Create_WithBlankOriginalValue_ShouldThrowInvalidArgumentError()
    {
        var request = new PseudonymServiceCreateRequest
        {
            Namespace = "existingNamespace",
            OriginalValue = "   ",
        };

        await sut.Invoking(async s => await s.Create(request, TestServerCallContext.Create()))
            .Should()
            .ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
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

        var response = await sut.Create(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );
        var firstCreatedPseudonym = response.Pseudonym.PseudonymValue;
        InMemoryPseudonymContext
            .Pseudonyms.Where(p => p.NamespaceName == request.Namespace)
            .Should()
            .HaveCount(1);

        response = await sut.Create(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );
        response.Pseudonym.PseudonymValue.Should().Be(firstCreatedPseudonym);
        InMemoryPseudonymContext
            .Pseudonyms.Where(p => p.NamespaceName == request.Namespace)
            .Should()
            .HaveCount(1);

        response = await sut.Create(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );
        response.Pseudonym.PseudonymValue.Should().Be(firstCreatedPseudonym);
        InMemoryPseudonymContext
            .Pseudonyms.Where(p => p.NamespaceName == request.Namespace)
            .Should()
            .HaveCount(1);
    }

    [Fact]
    public async Task List_WithEmptyNamespace_ShouldReturnEmptyList()
    {
        var request = new PseudonymServiceListRequest { Namespace = "emptyNamespace" };

        var response = await sut.List(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Namespace.Should().Be(request.Namespace);
        response.Pseudonyms.Should().BeEmpty();
    }

    [Fact]
    public async Task List_WithNonExistingNamespace_ShouldThrowNotFoundError()
    {
        var request = new PseudonymServiceListRequest { Namespace = "nonExistingNamespace" };

        await sut.Invoking(async s => await s.List(request, TestServerCallContext.Create()))
            .Should()
            .ThrowAsync<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public async Task List_WithExistingNonEmptyNamespace_ShouldReturnAllPseudonyms()
    {
        var request = new PseudonymServiceListRequest { Namespace = "existingNamespace" };

        var response = await sut.List(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Pseudonyms.Should().HaveSameCount(InMemoryPseudonymContext.Pseudonyms);
    }

    [Fact]
    public async Task List_ShouldNeverPopulateOriginalValue()
    {
        // this is a privacy invariant, not just a today-it-happens-to-be-true fact: the pseudonym
        // list/browse path must never expose original values in bulk. Reverse lookup (Get) is
        // the only place original_value is allowed to appear, one record at a time. Checking
        // HasOriginalValue (proto field-presence), not OriginalValue itself, since the generated
        // getter returns "" rather than null when the optional field is unset.
        var request = new PseudonymServiceListRequest { Namespace = "existingNamespace" };

        var response = await sut.List(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Pseudonyms.Should().NotBeEmpty();
        response.Pseudonyms.Should().OnlyContain(p => !p.HasOriginalValue);
    }

    [Fact]
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

            await sut.Create(
                createRequest,
                TestServerCallContext.Create(
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );
        }

        InMemoryPseudonymContext
            .Pseudonyms.Where(p => p.NamespaceName == namespaceName)
            .Count()
            .Should()
            .Be(pseudonymsToCreateCount);

        var request = new PseudonymServiceListRequest
        {
            Namespace = namespaceName,
            IncludeTotalSize = true,
            PageSize = 5,
        };

        var response = await sut.List(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        var allPseudonyms = new List<Pseudonym>();

        response.TotalSize.Should().Be(pseudonymsToCreateCount);
        response.Pseudonyms.Should().HaveCount(request.PageSize);

        allPseudonyms.AddRange(response.Pseudonyms);

        while (!string.IsNullOrEmpty(response.NextPageToken))
        {
            request.PageToken = response.NextPageToken;

            response = await sut.List(
                request,
                TestServerCallContext.Create(
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );

            allPseudonyms.AddRange(response.Pseudonyms);
        }

        allPseudonyms.Should().HaveCount(pseudonymsToCreateCount);
    }
}
