using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Vfps.Config;

namespace Vfps.Tests.ServiceTests;

public class NamespaceServiceTests : ServiceTestBase
{
    private readonly Services.NamespaceService sut;

    public NamespaceServiceTests()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        sut = new Services.NamespaceService(CreateNamespaceAppService(namespaceRepository));
    }

    /// <summary>
    /// Stores a namespace with every nullable column actually NULL - which is what the admin UI's
    /// create form leaves behind for a description nobody typed, and what the columns have always
    /// permitted. Written through the context rather than the app service so the row is exactly
    /// as null as the database allows, with no defaulting in between.
    /// </summary>
    private async Task<string> AddNamespaceWithNullColumnsAsync()
    {
        var name = $"allNullColumns-{Guid.NewGuid():N}";
        InMemoryPseudonymContext.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = name,
                Description = null,
                PseudonymLength = 16,
                PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Unspecified,
                PseudonymPrefix = null,
                PseudonymSuffix = null,
                OriginalValueValidationRegex = null,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
            }
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        return name;
    }

    [Fact]
    public async Task Get_WithNullColumns_ShouldReturnThemRatherThanThrow()
    {
        // Regression test: every string setter on the generated proto type rejects null, so
        // mapping a nullable column straight across threw ArgumentNullException - a 500 on a row
        // the database considers perfectly valid.
        var name = await AddNamespaceWithNullColumnsAsync();

        var response = await sut.Get(
            new NamespaceServiceGetRequest { Name = name },
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        // Every one of them is `optional`, so a NULL column comes back absent rather than empty -
        // which a client can tell apart from a deliberately empty one.
        response.Namespace.HasDescription.Should().BeFalse();
        response.Namespace.HasPseudonymPrefix.Should().BeFalse();
        response.Namespace.HasPseudonymSuffix.Should().BeFalse();
        response.Namespace.HasOriginalValueValidationRegex.Should().BeFalse();
        response.Namespace.HasParentName.Should().BeFalse();
    }

    [Fact]
    public async Task Get_WithEmptyButNotNullColumns_ShouldStillReportThemAsPresent()
    {
        // The other half of the distinction above: an empty string was stored, so it is not
        // absent. This is what every namespace created through the app service looks like, and
        // the fix must not quietly turn those into absent fields.
        var name = $"emptyColumns-{Guid.NewGuid():N}";
        InMemoryPseudonymContext.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = name,
                Description = "",
                PseudonymLength = 16,
                PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Unspecified,
                PseudonymPrefix = "",
                PseudonymSuffix = "",
                OriginalValueValidationRegex = "",
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
            }
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var response = await sut.Get(
            new NamespaceServiceGetRequest { Name = name },
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Namespace.HasDescription.Should().BeTrue();
        response.Namespace.Description.Should().BeEmpty();
        response.Namespace.HasPseudonymPrefix.Should().BeTrue();
        response.Namespace.HasPseudonymSuffix.Should().BeTrue();
        response.Namespace.HasOriginalValueValidationRegex.Should().BeTrue();
    }

    [Fact]
    public async Task GetAll_WithOneNullColumnRow_ShouldStillReturnEveryNamespace()
    {
        // The part that made this worth more than one bad response: GetAll projects the whole
        // list, so the throwing row took every other namespace down with it - a single namespace
        // created without a description was enough to make listing them fail for everyone.
        var name = await AddNamespaceWithNullColumnsAsync();

        var response = await sut.GetAll(
            new NamespaceServiceGetAllRequest(),
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Namespaces.Should().Contain(n => n.Name == name);
        response.Namespaces.Should().Contain(n => n.Name == "existingNamespace");
    }

    [Fact]
    public async Task ListChildren_WithANullColumnChild_ShouldStillReturnIt()
    {
        var parentName = $"nullChildParent-{Guid.NewGuid():N}";
        InMemoryPseudonymContext.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = parentName,
                Description = "parent",
                PseudonymLength = 16,
                PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Unspecified,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
            }
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var childName = $"nullChild-{Guid.NewGuid():N}";
        InMemoryPseudonymContext.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = childName,
                Description = null,
                PseudonymLength = 16,
                PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Unspecified,
                PseudonymPrefix = null,
                PseudonymSuffix = null,
                OriginalValueValidationRegex = null,
                ParentName = parentName,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
            }
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var response = await sut.ListChildren(
            new NamespaceServiceListChildrenRequest { Name = parentName },
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Namespaces.Should().ContainSingle(n => n.Name == childName);
        response.Namespaces.Single().ParentName.Should().Be(parentName);
    }

    [Fact]
    public async Task Get_AfterAnAdminUiCreateWithNoDescription_ShouldReturnIt()
    {
        // How the NULL rows actually get there, end to end. The gRPC surface cannot produce one -
        // `description` is a plain proto3 string, so an omitted one arrives as "" - but the admin
        // UI's form model types it as `string?` with no initializer and hands the app service a
        // null for a field nobody typed into, which is stored verbatim. Every namespace created
        // that way then broke the API's read side.
        var name = $"uiCreated-{Guid.NewGuid():N}";
        await CreateNamespaceAppService(new NamespaceRepository(ContextFactory))
            .CreateAsync(
                new Data.Models.Namespace
                {
                    Name = name,
                    Description = null,
                    PseudonymLength = 16,
                    PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.Unspecified,
                },
                new System.Security.Claims.ClaimsPrincipal(),
                CancellationToken.None
            );
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var response = await sut.Get(
            new NamespaceServiceGetRequest { Name = name },
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Namespace.Name.Should().Be(name);
        response.Namespace.HasDescription.Should().BeFalse();
    }

    [Fact]
    public async Task Create_WithoutADescription_ShouldStoreNoneRatherThanAnEmptyOne()
    {
        // The request field is `optional` too, so the distinction survives a round trip: omitting
        // it stores NULL and reads back absent, rather than quietly becoming an empty description
        // that nothing can tell apart from a deliberate one.
        var name = $"noDescription-{Guid.NewGuid():N}";

        var created = await sut.Create(
            new NamespaceServiceCreateRequest { Name = name, PseudonymLength = 16 },
            TestServerCallContext.Create()
        );

        created.Namespace.HasDescription.Should().BeFalse();
        InMemoryPseudonymContext.ChangeTracker.Clear();
        InMemoryPseudonymContext
            .Namespaces.Single(n => n.Name == name)
            .Description.Should()
            .BeNull();
    }

    [Fact]
    public async Task Create_WithAnExplicitlyEmptyDescription_ShouldKeepItEmptyRatherThanAbsent()
    {
        var name = $"emptyDescription-{Guid.NewGuid():N}";

        var created = await sut.Create(
            new NamespaceServiceCreateRequest
            {
                Name = name,
                PseudonymLength = 16,
                Description = "",
            },
            TestServerCallContext.Create()
        );

        created.Namespace.HasDescription.Should().BeTrue();
        created.Namespace.Description.Should().BeEmpty();
        InMemoryPseudonymContext.ChangeTracker.Clear();
        InMemoryPseudonymContext
            .Namespaces.Single(n => n.Name == name)
            .Description.Should()
            .BeEmpty();
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
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var restrictedSut = new Services.NamespaceService(
            CreateNamespaceAppService(
                namespaceRepository,
                new AuthorizationConfig { IsEnabled = true },
                Grants.ForRole("existingNamespace", "can-read-existing", read: true)
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
    public async Task Create_WithAllowsMultiplePseudonyms_ShouldPersistAndReturnIt()
    {
        var request = new NamespaceServiceCreateRequest
        {
            Name = nameof(Create_WithAllowsMultiplePseudonyms_ShouldPersistAndReturnIt),
            PseudonymLength = 16,
            AllowsMultiplePseudonyms = true,
        };

        var response = await sut.Create(
            request,
            TestServerCallContext.Create(cancellationToken: TestContext.Current.CancellationToken)
        );

        response.Namespace.AllowsMultiplePseudonyms.Should().BeTrue();
        InMemoryPseudonymContext
            .Namespaces.Should()
            .Contain(n => n.Name == request.Name && n.AllowsMultiplePseudonyms);
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

        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var namespaceRepositoryForPseudonymService = new NamespaceRepository(ContextFactory);
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
