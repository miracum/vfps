using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Vfps.Config;

namespace Vfps.Tests.ServiceTests;

public class NamespaceServiceTests : ServiceTestBase
{
    private readonly Services.NamespaceService sut;

    public NamespaceServiceTests()
    {
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        sut = new Services.NamespaceService(CreateNamespaceAppService(namespaceRepository));
    }

    [Fact]
    public async Task Create_WithExistingNamespace_ShouldThrowAlreadyExistsError()
    {
        var request = new NamespaceServiceCreateRequest
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
        };

        await sut.Invoking(async s => await s.Create(request, TestServerCallContext.Create()))
            .Should()
            .ThrowAsync<RpcException>()
            .Where(exc => exc.StatusCode == StatusCode.AlreadyExists);
    }

    [Fact]
    public async Task Get_WithExistingNamespace_ShouldReturnNamespace()
    {
        var request = new NamespaceServiceGetRequest { Name = "existingNamespace" };

        var response = await sut.Get(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Namespace.Name.Should().Be(request.Name);
    }

    [Fact]
    public async Task Get_WithAuthorizationEnabledAndNoReadAccess_ShouldThrowPermissionDenied()
    {
        // Regression test: NamespaceService.Get used to call INamespaceRepository directly,
        // bypassing INamespaceAppService (and therefore every permission check) entirely - any
        // caller could read any namespace's metadata regardless of read access. This is the
        // gRPC-layer half of NamespaceAppServiceTests' GetAsync coverage; TestServerCallContext
        // has no HttpContext, so ServerCallContextExtensions.GetUser() always resolves to an
        // anonymous principal here, which is sufficient to exercise the denied path.
        var namespaceRepository = new NamespaceRepository(InMemoryPseudonymContext);
        var restrictedSut = new Services.NamespaceService(
            CreateNamespaceAppService(
                namespaceRepository,
                new AuthorizationConfig
                {
                    IsEnabled = true,
                    NamespaceRules =
                    [
                        new NamespaceRule
                        {
                            Namespace = "existingNamespace",
                            ReadRoles = ["can-read-existing"],
                        },
                    ],
                }
            )
        );
        var request = new NamespaceServiceGetRequest { Name = "existingNamespace" };

        var act = () => restrictedSut.Get(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task GetAll_ShouldReturnAllNamespace()
    {
        var request = new NamespaceServiceGetAllRequest();

        var response = await sut.GetAll(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Namespaces.Should().HaveSameCount(InMemoryPseudonymContext.Namespaces);
    }

    [Fact]
    public async Task Get_WithNonExistingNamespace_ShouldThrowNotFoundException()
    {
        var request = new NamespaceServiceGetRequest { Name = "notExisting" };

        var act = () => sut.Get(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [Fact]
    public async Task Create_ShouldSaveNewNamespace()
    {
        var request = new NamespaceServiceCreateRequest
        {
            Name = nameof(Create_ShouldSaveNewNamespace),
            PseudonymLength = 16,
        };

        var response = await sut.Create(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Namespace.Name.Should().Be(request.Name);

        InMemoryPseudonymContext.Namespaces.Should().Contain(n => n.Name == request.Name);
    }

    [Fact]
    public async Task Create_WithPseudonymLengthZero_ShouldFail()
    {
        var request = new NamespaceServiceCreateRequest
        {
            Name = nameof(Create_WithPseudonymLengthZero_ShouldFail),
            PseudonymLength = 0,
        };

        var act = () => sut.Create(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.OutOfRange);
    }

    [Fact]
    public async Task Delete_WithExistingNamespace_ShouldDeleteNamespace()
    {
        var createRequest = new NamespaceServiceCreateRequest
        {
            Name = "toBeDeleted",
            PseudonymLength = 16,
        };

        await sut.Create(
            createRequest,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        var deleteRequest = new NamespaceServiceDeleteRequest { Name = "toBeDeleted" };

        await sut.Delete(
            deleteRequest,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        InMemoryPseudonymContext.Namespaces.Should().NotContain(n => n.Name == deleteRequest.Name);
    }

    [Fact]
    public async Task Delete_WithNamespaceContainingPseudonyms_ShouldDeleteNamespaceAndAllPseudonyms()
    {
        var createRequest = new NamespaceServiceCreateRequest
        {
            Name = nameof(
                Delete_WithNamespaceContainingPseudonyms_ShouldDeleteNamespaceAndAllPseudonyms
            ),
            PseudonymLength = 16,
        };

        await sut.Create(
            createRequest,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        var pseudonymRepository = new PseudonymRepository(InMemoryPseudonymContext);
        var namespaceRepositoryForPseudonymService = new NamespaceRepository(
            InMemoryPseudonymContext
        );
        var pseudonymService = new Services.PseudonymService(
            CreatePseudonymAppService(namespaceRepositoryForPseudonymService, pseudonymRepository)
        );

        var pseudonymsToCreateCount = 100;
        for (int i = 0; i < pseudonymsToCreateCount; i++)
        {
            var createPseudonymRequest = new PseudonymServiceCreateRequest
            {
                Namespace = createRequest.Name,
                OriginalValue =
                    nameof(
                        Delete_WithNamespaceContainingPseudonyms_ShouldDeleteNamespaceAndAllPseudonyms
                    ) + i,
            };

            await pseudonymService.Create(
                createPseudonymRequest,
                TestServerCallContext.Create(
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );
        }

        var pseudonymCount = await InMemoryPseudonymContext
            .Pseudonyms.AsNoTracking()
            .Where(p => p.NamespaceName == createRequest.Name)
            .CountAsync(TestContext.Current.CancellationToken);

        pseudonymCount.Should().Be(pseudonymsToCreateCount);

        var deleteRequest = new NamespaceServiceDeleteRequest { Name = createRequest.Name };

        await sut.Delete(
            deleteRequest,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        InMemoryPseudonymContext
            .Namespaces.AsNoTracking()
            .Where(n => n.Name == deleteRequest.Name)
            .Should()
            .BeEmpty();

        pseudonymCount = await InMemoryPseudonymContext
            .Pseudonyms.AsNoTracking()
            .Where(p => p.NamespaceName == createRequest.Name)
            .CountAsync(TestContext.Current.CancellationToken);

        pseudonymCount.Should().Be(0);
    }

    [Fact]
    public async Task Delete_WithNonExistingNamespace_ShouldThrowNotFoundException()
    {
        var request = new NamespaceServiceDeleteRequest { Name = "notExisting" };

        var act = () => sut.Delete(request, TestServerCallContext.Create());

        var result = await act.Should().ThrowAsync<RpcException>();
        result.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }
}
