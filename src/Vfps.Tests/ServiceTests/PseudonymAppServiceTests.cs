using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Vfps.Config;

namespace Vfps.Tests.ServiceTests;

public class PseudonymAppServiceTests : ServiceTestBase
{
    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim("roles", r))));

    [Fact]
    public async Task CreateTrustedAsync_WithResolvedNamespace_ShouldCreateWithoutLookingItUp()
    {
        // The trusted create takes an already-resolved namespace and must not look it up again
        // (or even need it to exist in the "Namespaces" table under that exact instance).
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
        };

        var created = (
            await sut.CreateTrustedAsync(
                @namespace,
                "resolved-namespace-value",
                1,
                CancellationToken.None
            )
        ).Single();

        created.OriginalValue.Should().Be("resolved-namespace-value");
        created.PseudonymValue.Should().HaveLength(16);
    }

    [Fact]
    public async Task CreateTrustedAsync_WithNonMatchingValidationRegex_ShouldThrowOriginalValueValidationException()
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
            OriginalValueValidationRegex = "^[0-9]+$",
        };

        var act = () =>
            sut.CreateTrustedAsync(@namespace, "not-a-number", 1, CancellationToken.None);

        await act.Should().ThrowAsync<OriginalValueValidationException>();
    }

    [Fact]
    public async Task CreateTrustedAsync_WithMatchingValidationRegex_ShouldCreate()
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
            OriginalValueValidationRegex = "^[0-9]+$",
        };

        var created = (
            await sut.CreateTrustedAsync(@namespace, "12345", 1, CancellationToken.None)
        ).Single();

        created.OriginalValue.Should().Be("12345");
    }

    [Fact]
    public async Task CreateTrustedBatchAsync_WithNonMatchingValidationRegex_ShouldThrowOriginalValueValidationException()
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
            OriginalValueValidationRegex = "^[0-9]+$",
        };

        var act = () =>
            sut.CreateTrustedBatchAsync([(@namespace, "not-a-number")], CancellationToken.None);

        await act.Should().ThrowAsync<OriginalValueValidationException>();
    }

    // "existingNamespace" already contains the pseudonym value "existingPseudonym" (see
    // ServiceTestBase) - that's the parent value a child namespace chains from here. The row has
    // to actually exist, since pseudonyms.namespace_name is a foreign key; the returned instance
    // is what the trusted overloads read their configuration from.
    private async Task<Data.Models.Namespace> CreateChildNamespaceAsync(
        ParentValidationMode mode = ParentValidationMode.EnsureExists,
        string name = "childNamespace",
        string parentName = "existingNamespace"
    )
    {
        var child = new Data.Models.Namespace
        {
            Name = name,
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
            ParentName = parentName,
            ParentValidationMode = mode,
            CreatedAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
        };

        await new NamespaceRepository(ContextFactory).CreateAsync(child, CancellationToken.None);

        return child;
    }

    [Fact]
    public async Task CreateTrustedAsync_WithValueExistingInParent_ShouldCreate()
    {
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            new PseudonymRepository(ContextFactory)
        );

        var created = (
            await sut.CreateTrustedAsync(
                await CreateChildNamespaceAsync(),
                "existingPseudonym",
                1,
                CancellationToken.None
            )
        ).Single();

        created.OriginalValue.Should().Be("existingPseudonym");
        created.PseudonymValue.Should().HaveLength(16);
    }

    [Fact]
    public async Task CreateTrustedAsync_WithValueMissingFromParent_ShouldThrowParentPseudonymNotFoundException()
    {
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            new PseudonymRepository(ContextFactory)
        );
        var child = await CreateChildNamespaceAsync();

        var act = () =>
            sut.CreateTrustedAsync(
                child,
                "never-pseudonymized-upstream",
                1,
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ParentPseudonymNotFoundException>();
    }

    [Fact]
    public async Task CreateTrustedAsync_WithValidationOffAndValueMissingFromParent_ShouldCreate()
    {
        // A parent link on its own is only metadata - without EnsureExists, nothing is checked,
        // which is what keeps this off the hot path for namespaces that didn't opt in.
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            new PseudonymRepository(ContextFactory)
        );

        var created = (
            await sut.CreateTrustedAsync(
                await CreateChildNamespaceAsync(ParentValidationMode.Unspecified),
                "never-pseudonymized-upstream",
                1,
                CancellationToken.None
            )
        ).Single();

        created.OriginalValue.Should().Be("never-pseudonymized-upstream");
    }

    [Fact]
    public async Task CreateTrustedAsync_WithMultiPsnParent_ShouldAcceptAnyOfItsPseudonyms()
    {
        // A multi-psn parent stores several pseudonyms per original value, distinguished by
        // sequence number. The existence check looks values up by pseudonym_value alone, so every
        // one of them is a valid input to the child - no special-casing for sequence numbers.
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            new PseudonymRepository(ContextFactory)
        );
        var parent = await namespaceRepository.FindAsync(
            "multiPsnNamespace",
            CancellationToken.None
        );
        var parentPseudonyms = await sut.CreateTrustedAsync(
            parent!,
            "multi-psn-original",
            3,
            CancellationToken.None
        );
        var child = await CreateChildNamespaceAsync(
            name: "childOfMultiPsn",
            parentName: "multiPsnNamespace"
        );

        foreach (var parentPseudonym in parentPseudonyms)
        {
            var created = (
                await sut.CreateTrustedAsync(
                    child,
                    parentPseudonym.PseudonymValue,
                    1,
                    CancellationToken.None
                )
            ).Single();

            created.OriginalValue.Should().Be(parentPseudonym.PseudonymValue);
        }
    }

    [Fact]
    public async Task CreateAsync_WithValueMissingFromParent_ShouldThrowParentPseudonymNotFoundException()
    {
        // The permission-checked entry point delegates to the same validation, so a child
        // namespace is enforced identically whether the caller is gRPC/Blazor or the job runner.
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            new PseudonymRepository(ContextFactory)
        );
        await CreateChildNamespaceAsync();

        var act = () =>
            sut.CreateAsync(
                "childNamespace",
                "never-pseudonymized-upstream",
                1,
                new ClaimsPrincipal(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ParentPseudonymNotFoundException>();
    }

    [Fact]
    public async Task CreateTrustedBatchAsync_WithAnyValueMissingFromParent_ShouldThrowAndCreateNothing()
    {
        // Whole-batch rejection, matching how the regex validation already behaves: a CSV job
        // referencing values that were never pseudonymized upstream is a misconfigured job.
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var child = await CreateChildNamespaceAsync();

        var act = () =>
            sut.CreateTrustedBatchAsync(
                [(child, "existingPseudonym"), (child, "never-pseudonymized-upstream")],
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ParentPseudonymNotFoundException>();
        // The valid row must not have been created either - validation runs before any generation.
        (
            await pseudonymRepository.FindAllByOriginalValueAsync(
                "childNamespace",
                "existingPseudonym",
                CancellationToken.None
            )
        )
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task CreateTrustedBatchAsync_WithAllValuesExistingInParent_ShouldCreateAll()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            new PseudonymRepository(ContextFactory)
        );
        // A second value in the parent, so the batch covers more than one row.
        var parent = await namespaceRepository.FindAsync(
            "existingNamespace",
            CancellationToken.None
        );
        var second = (
            await sut.CreateTrustedAsync(
                parent!,
                "another-original-value",
                1,
                CancellationToken.None
            )
        ).Single();
        var child = await CreateChildNamespaceAsync();

        var created = await sut.CreateTrustedBatchAsync(
            [(child, "existingPseudonym"), (child, second.PseudonymValue)],
            CancellationToken.None
        );

        created.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateTrustedBatchAsync_WithMixedNamespaces_ShouldOnlyValidateTheValidatingOnes()
    {
        // A CSV chunk's column mappings can span namespaces: a value that isn't in the parent is
        // fine for a namespace that doesn't validate, and must not be rejected on its behalf.
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            new PseudonymRepository(ContextFactory)
        );
        var root = await namespaceRepository.FindAsync("emptyNamespace", CancellationToken.None);
        var child = await CreateChildNamespaceAsync();

        var created = await sut.CreateTrustedBatchAsync(
            [(child, "existingPseudonym"), (root!, "anything-goes-here")],
            CancellationToken.None
        );

        created.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateTrustedAsync_CalledManyTimesConcurrently_ShouldNotThrowAndShouldCreateAll()
    {
        // One app service instance is shared by everything in its DI scope - for a Blazor circuit,
        // every handler on the page for as long as the tab is open. DbContext instances aren't
        // safe for concurrent use, so this only works because each repository call opens its own
        // context rather than sharing one - if that regressed, this would throw a DbContext
        // concurrency exception instead of completing cleanly.
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            new PseudonymRepository(ContextFactory)
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
        };

        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(i =>
                    sut.CreateTrustedAsync(
                        @namespace,
                        $"concurrent-value-{i}",
                        1,
                        CancellationToken.None
                    )
                )
        );

        var created = results.Select(r => r.Single()).ToList();
        created
            .Select(r => r.OriginalValue)
            .Should()
            .BeEquivalentTo(Enumerable.Range(0, 20).Select(i => $"concurrent-value-{i}"));
        created.Select(r => r.PseudonymValue).Distinct().Should().HaveCount(20);
    }

    [Fact]
    public async Task CreateTrustedBatchAsync_WithMultipleNamespacesAndDuplicates_ShouldResolveAllInOneBatch()
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var namespaceA = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
        };
        var namespaceB = new Data.Models.Namespace
        {
            Name = "emptyNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
        };

        var result = await sut.CreateTrustedBatchAsync(
            [
                (namespaceA, "value1"),
                (namespaceB, "value1"),
                // A duplicate (namespace, originalValue) pair within the same batch must not be
                // generated/stored twice, and both occurrences must resolve to the same pseudonym.
                (namespaceA, "value1"),
                (namespaceA, "value2"),
            ],
            CancellationToken.None
        );

        result.Should().HaveCount(3);
        result[("existingNamespace", "value1")]
            .PseudonymValue.Should()
            .Be(result[("existingNamespace", "value1")].PseudonymValue);
        result[("existingNamespace", "value1")]
            .PseudonymValue.Should()
            .NotBe(result[("emptyNamespace", "value1")].PseudonymValue);
        result[("existingNamespace", "value2")]
            .PseudonymValue.Should()
            .NotBe(result[("existingNamespace", "value1")].PseudonymValue);

        // Idempotent across separate calls too - same as CreateTrustedAsync's upsert semantics.
        var second = await sut.CreateTrustedBatchAsync(
            [(namespaceA, "value1")],
            CancellationToken.None
        );
        second[("existingNamespace", "value1")]
            .PseudonymValue.Should()
            .Be(result[("existingNamespace", "value1")].PseudonymValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTrustedBatchAsync_WithBlankOriginalValue_ShouldThrowArgumentException(
        string blankValue
    )
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
        };

        var act = () =>
            sut.CreateTrustedBatchAsync([(@namespace, blankValue)], CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateTrustedBatchAsync_WithEmptyRequestList_ShouldReturnEmptyResult()
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );

        var result = await sut.CreateTrustedBatchAsync([], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTrustedAsync_WithResolvedNamespaceAndBlankOriginalValue_ShouldThrowArgumentException(
        string blankValue
    )
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
        };

        var act = () => sut.CreateTrustedAsync(@namespace, blankValue, 1, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WithPseudonymCaching_ShouldServeARepeatFromTheCache()
    {
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            new CachingPseudonymRepository(
                ContextFactory,
                new MemoryCache(new MemoryCacheOptions { SizeLimit = 2048 }),
                new CacheConfig()
            )
        );
        var first = await sut.CreateAsync(
            "existingNamespace",
            "an original value",
            1,
            UserWithRoles(),
            CancellationToken.None
        );

        // Gone from the database, so only a cache hit can still return it.
        InMemoryPseudonymContext.Pseudonyms.Remove(
            InMemoryPseudonymContext.Pseudonyms.Single(p => p.OriginalValue == "an original value")
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var second = await sut.CreateAsync(
            "existingNamespace",
            "an original value",
            1,
            UserWithRoles(),
            CancellationToken.None
        );

        first.Single().PseudonymValue.Should().Be("existingPseudonym");
        second.Single().PseudonymValue.Should().Be("existingPseudonym");
    }

    [Fact]
    public async Task CreateAsync_WithBlankOriginalValue_ShouldThrowArgumentExceptionBeforeUpsert()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(namespaceRepository, pseudonymRepository);

        var act = () =>
            sut.CreateAsync("existingNamespace", " ", 1, UserWithRoles(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_WithCountGreaterThanOneOnOrdinaryNamespace_ShouldThrowMultiplePseudonymsNotAllowedException()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(namespaceRepository, pseudonymRepository);

        var act = () =>
            sut.CreateAsync(
                "existingNamespace",
                "some value",
                2,
                UserWithRoles(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<MultiplePseudonymsNotAllowedException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateTrustedAsync_WithCountLessThanOne_ShouldThrowArgumentOutOfRangeException(
        long count
    )
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "existingNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
        };

        var act = () =>
            sut.CreateTrustedAsync(@namespace, "some value", count, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CreateTrustedAsync_WithCountOnMultiPsnNamespace_ShouldCreateThatManyDistinctPseudonyms()
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "multiPsnNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
            AllowsMultiplePseudonyms = true,
        };

        var created = await sut.CreateTrustedAsync(
            @namespace,
            "shared value",
            3,
            CancellationToken.None
        );

        created.Should().HaveCount(3);
        created.Select(p => p.SequenceNumber).Should().BeEquivalentTo([0, 1, 2]);
        created.Select(p => p.PseudonymValue).Distinct().Should().HaveCount(3);
        created.Should().OnlyContain(p => p.OriginalValue == "shared value");
    }

    [Fact]
    public async Task CreateTrustedAsync_WithLargerCountThanExisting_ShouldOnlyAddTheMissingOnes()
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "multiPsnNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
            AllowsMultiplePseudonyms = true,
        };

        var first = await sut.CreateTrustedAsync(
            @namespace,
            "shared value",
            3,
            CancellationToken.None
        );
        var grown = await sut.CreateTrustedAsync(
            @namespace,
            "shared value",
            5,
            CancellationToken.None
        );

        grown.Should().HaveCount(5);
        // The first 3 must be untouched - same pseudonym values at the same sequence numbers.
        grown
            .Where(p => p.SequenceNumber < 3)
            .Select(p => p.PseudonymValue)
            .Should()
            .BeEquivalentTo(first.Select(p => p.PseudonymValue));
        grown.Select(p => p.PseudonymValue).Distinct().Should().HaveCount(5);
    }

    [Fact]
    public async Task CreateTrustedAsync_WithSmallerOrEqualCountThanExisting_ShouldReturnExistingSetUnchanged()
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "multiPsnNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
            AllowsMultiplePseudonyms = true,
        };

        var first = await sut.CreateTrustedAsync(
            @namespace,
            "shared value",
            5,
            CancellationToken.None
        );
        var repeatSameCount = await sut.CreateTrustedAsync(
            @namespace,
            "shared value",
            5,
            CancellationToken.None
        );
        var repeatSmallerCount = await sut.CreateTrustedAsync(
            @namespace,
            "shared value",
            2,
            CancellationToken.None
        );

        repeatSameCount
            .Select(p => p.PseudonymValue)
            .Should()
            .BeEquivalentTo(first.Select(p => p.PseudonymValue));
        // Not truncated to 2 - the full, already-larger set is still returned.
        repeatSmallerCount.Should().HaveCount(5);
        repeatSmallerCount
            .Select(p => p.PseudonymValue)
            .Should()
            .BeEquivalentTo(first.Select(p => p.PseudonymValue));
    }

    [Fact]
    public async Task CreateTrustedBatchAsync_AgainstMultiPsnNamespaceWithExistingSequences_ShouldOnlyEverTouchSequenceZero()
    {
        // CreateTrustedBatchAsync (the CSV job path) has no `count` concept - it always operates
        // on sequence 0 only, so it must not disturb (or be confused by) additional sequences
        // already created via the dedicated multi-psn create path.
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );
        var @namespace = new Data.Models.Namespace
        {
            Name = "multiPsnNamespace",
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
            AllowsMultiplePseudonyms = true,
        };
        var multi = await sut.CreateTrustedAsync(
            @namespace,
            "shared value",
            3,
            CancellationToken.None
        );

        var batchResult = await sut.CreateTrustedBatchAsync(
            [(@namespace, "shared value")],
            CancellationToken.None
        );

        batchResult.Should().HaveCount(1);
        var batchPseudonym = batchResult[(@namespace.Name, "shared value")];
        batchPseudonym.SequenceNumber.Should().Be(0);
        batchPseudonym
            .PseudonymValue.Should()
            .Be(multi.Single(p => p.SequenceNumber == 0).PseudonymValue);

        var allForValue = await pseudonymRepository.FindAllByOriginalValueAsync(
            @namespace.Name,
            "shared value",
            CancellationToken.None
        );
        allForValue.Should().HaveCount(3);
    }

    [Fact]
    public async Task ListAsync_WithAuthorizationEnabledAndNoReadAccess_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true }
        );

        var act = () =>
            sut.ListAsync(
                "existingNamespace",
                25,
                null,
                includeTotalSize: false,
                UserWithRoles(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task SearchAsync_WithoutReadAccess_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true }
        );

        var act = () =>
            sut.SearchAsync(
                "existingNamespace",
                null,
                0,
                25,
                UserWithRoles(),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task SearchAsync_WithReadAccessButNotReverseLookupAccess_ShouldOmitOriginalValues()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "read-only", read: true)
        );

        var page = await sut.SearchAsync(
            "existingNamespace",
            null,
            0,
            25,
            UserWithRoles("read-only"),
            CancellationToken.None
        );

        page.Items.Should().ContainSingle();
        page.Items[0].OriginalValue.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_WithReadAndReverseLookupAccess_ShouldIncludeOriginalValues()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "read-only", read: true),
            Grants.ForRole("existingNamespace", "can-reverse-lookup", reverseLookup: true)
        );

        var page = await sut.SearchAsync(
            "existingNamespace",
            null,
            0,
            25,
            UserWithRoles("read-only", "can-reverse-lookup"),
            CancellationToken.None
        );

        page.Items.Should().ContainSingle();
        page.Items[0].PseudonymValue.Should().Be("existingPseudonym");
        page.Items[0].OriginalValue.Should().Be("an original value");
        page.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_WithSearchTextMatchingOriginalValue_ShouldOnlyMatchWhenPermitted()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "read-only", read: true),
            Grants.ForRole("existingNamespace", "can-reverse-lookup", reverseLookup: true)
        );

        // Searching for a substring of the original value must not surface it as a match unless
        // the caller also has reverse-lookup access - otherwise a read-only user could confirm an
        // original value is present in the namespace without ReverseLookupAsync ever letting them
        // see it.
        var withoutReverseLookup = await sut.SearchAsync(
            "existingNamespace",
            "original value",
            0,
            25,
            UserWithRoles("read-only"),
            CancellationToken.None
        );
        withoutReverseLookup.Items.Should().BeEmpty();

        var withReverseLookup = await sut.SearchAsync(
            "existingNamespace",
            "original value",
            0,
            25,
            UserWithRoles("read-only", "can-reverse-lookup"),
            CancellationToken.None
        );
        withReverseLookup.Items.Should().ContainSingle();
        withReverseLookup.Items[0].PseudonymValue.Should().Be("existingPseudonym");
    }

    [Fact]
    public async Task SearchAsync_WithSearchTextMatchingPseudonymValue_ShouldMatchWithoutReverseLookupAccess()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "read-only", read: true)
        );

        var page = await sut.SearchAsync(
            "existingNamespace",
            "existingPseudo",
            0,
            25,
            UserWithRoles("read-only"),
            CancellationToken.None
        );

        page.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_WithMorePseudonymsThanTake_ShouldPageAndReportTotalCount()
    {
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            pseudonymRepository
        );

        // Beyond the base fixture's single "existingPseudonym", seed a few more so paging
        // (skip/take) and the total count actually have something to page across.
        InMemoryPseudonymContext.Pseudonyms.AddRange(
            Enumerable
                .Range(0, 4)
                .Select(i => new Data.Models.Pseudonym
                {
                    NamespaceName = "existingNamespace",
                    OriginalValue = $"paging-original-{i}",
                    PseudonymValue = $"paging-pseudonym-{i}",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i),
                    LastUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(i),
                })
        );
        await InMemoryPseudonymContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var firstPage = await sut.SearchAsync(
            "existingNamespace",
            null,
            0,
            2,
            UserWithRoles(),
            CancellationToken.None
        );

        firstPage.Items.Should().HaveCount(2);
        firstPage.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task ReverseLookupAsync_WithReadAccessButNotReverseLookupAccess_ShouldThrowForbidden()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "read-only", read: true)
        );

        var act = () =>
            sut.ReverseLookupAsync(
                "existingNamespace",
                "existingPseudonym",
                UserWithRoles("read-only"),
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task ReverseLookupAsync_WithReverseLookupAccess_ShouldRevealOriginalValue()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var pseudonymRepository = new PseudonymRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            pseudonymRepository,
            new AuthorizationConfig { IsEnabled = true },
            Grants.ForRole("existingNamespace", "can-reverse-lookup", reverseLookup: true)
        );

        var result = await sut.ReverseLookupAsync(
            "existingNamespace",
            "existingPseudonym",
            UserWithRoles("can-reverse-lookup"),
            CancellationToken.None
        );

        result.Should().NotBeNull();
        result!.OriginalValue.Should().Be("an original value");
    }

    [Fact]
    public async Task ReverseLookupTrustedBatchAsync_WithNoPermissionCheckerAccess_ShouldStillRevealOriginalValue()
    {
        // Deliberately skips the permission check - it's only called by the CSV job runner, which
        // already verified reverse-lookup access to every namespace a de-pseudonymization job's
        // mappings reference, up front at job creation time.
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            new PseudonymRepository(ContextFactory),
            new AuthorizationConfig { IsEnabled = true }
        );
        var @namespace = await namespaceRepository.FindAsync(
            "existingNamespace",
            CancellationToken.None
        );

        var result = await sut.ReverseLookupTrustedBatchAsync(
            [(@namespace!, "existingPseudonym")],
            CancellationToken.None
        );

        result[("existingNamespace", "existingPseudonym")]
            .OriginalValue.Should()
            .Be("an original value");
    }

    [Fact]
    public async Task ReverseLookupTrustedBatchAsync_WithUnknownPseudonym_ShouldLeaveItOut()
    {
        var namespaceRepository = new NamespaceRepository(ContextFactory);
        var sut = CreatePseudonymAppService(
            namespaceRepository,
            new PseudonymRepository(ContextFactory)
        );
        var @namespace = await namespaceRepository.FindAsync(
            "existingNamespace",
            CancellationToken.None
        );

        var result = await sut.ReverseLookupTrustedBatchAsync(
            [(@namespace!, "no-such-pseudonym")],
            CancellationToken.None
        );

        result.Should().BeEmpty();
    }

    // --- ImportTrustedBatchAsync -------------------------------------------------------------
    //
    // Exercised against the real (SQLite) upsert rather than a faked repository: what these are
    // actually about is which rows land in the store and which are refused, and that's decided by
    // the "insert if not exists" semantics of the upsert itself.

    private PseudonymAppService CreateImportSut() =>
        CreatePseudonymAppService(
            new NamespaceRepository(ContextFactory),
            new PseudonymRepository(ContextFactory)
        );

    private static Data.Models.Namespace NamespaceNamed(
        string name,
        bool allowsMultiplePseudonyms = false,
        string? originalValueValidationRegex = null
    ) =>
        new()
        {
            Name = name,
            PseudonymLength = 16,
            PseudonymGenerationMethod = Protos.PseudonymGenerationMethod.FullRandomHexEncoded,
            AllowsMultiplePseudonyms = allowsMultiplePseudonyms,
            OriginalValueValidationRegex = originalValueValidationRegex,
        };

    private async Task<Data.Models.Pseudonym?> FindStoredAsync(
        string namespaceName,
        string pseudonymValue
    ) =>
        await new PseudonymRepository(ContextFactory).FindByPseudonymValueAsync(
            namespaceName,
            pseudonymValue,
            CancellationToken.None
        );

    [Fact]
    public async Task ImportTrustedBatchAsync_WithNewPairs_ShouldStoreThemVerbatim()
    {
        var sut = CreateImportSut();

        var results = await sut.ImportTrustedBatchAsync(
            NamespaceNamed("emptyNamespace"),
            [
                new PseudonymImportEntry("alice", "imported-psn-1"),
                new PseudonymImportEntry("bob", "imported-psn-2"),
            ],
            CancellationToken.None
        );

        results.Select(r => r.Outcome).Should().AllBeEquivalentTo(PseudonymImportOutcome.Imported);

        // Stored exactly as given - no prefix/suffix, no generation, nothing derived from the
        // namespace's own pseudonym settings.
        var stored = await FindStoredAsync("emptyNamespace", "imported-psn-1");
        stored.Should().NotBeNull();
        stored!.OriginalValue.Should().Be("alice");
    }

    [Fact]
    public async Task ImportTrustedBatchAsync_WithTheSamePairTwice_ShouldReportItAsAlreadyPresent()
    {
        // Re-running the same import file has to be a clean no-op, not a pile of conflicts.
        var sut = CreateImportSut();
        var entries = new[] { new PseudonymImportEntry("alice", "idempotent-psn") };

        await sut.ImportTrustedBatchAsync(
            NamespaceNamed("emptyNamespace"),
            entries,
            CancellationToken.None
        );
        var second = await sut.ImportTrustedBatchAsync(
            NamespaceNamed("emptyNamespace"),
            entries,
            CancellationToken.None
        );

        second[0].Outcome.Should().Be(PseudonymImportOutcome.AlreadyPresent);
    }

    [Fact]
    public async Task ImportTrustedBatchAsync_WithADuplicatePairInTheSameBatch_ShouldReportTheSecondAsAlreadyPresent()
    {
        var sut = CreateImportSut();

        var results = await sut.ImportTrustedBatchAsync(
            NamespaceNamed("emptyNamespace"),
            [
                new PseudonymImportEntry("alice", "dup-psn"),
                new PseudonymImportEntry("alice", "dup-psn"),
            ],
            CancellationToken.None
        );

        results[0].Outcome.Should().Be(PseudonymImportOutcome.Imported);
        results[1].Outcome.Should().Be(PseudonymImportOutcome.AlreadyPresent);
    }

    [Fact]
    public async Task ImportTrustedBatchAsync_WithAnOriginalValueThatAlreadyHasAPseudonym_ShouldKeepTheStoredOne()
    {
        // "an original value" -> "existingPseudonym" is seeded by ServiceTestBase.
        var sut = CreateImportSut();

        var results = await sut.ImportTrustedBatchAsync(
            NamespaceNamed("existingNamespace"),
            [new PseudonymImportEntry("an original value", "a-different-pseudonym")],
            CancellationToken.None
        );

        results[0].Outcome.Should().Be(PseudonymImportOutcome.OriginalValueConflict);
        (await FindStoredAsync("existingNamespace", "a-different-pseudonym")).Should().BeNull();
        (await FindStoredAsync("existingNamespace", "existingPseudonym")).Should().NotBeNull();
    }

    [Fact]
    public async Task ImportTrustedBatchAsync_WithAPseudonymAlreadyUsedByAnotherOriginalValue_ShouldRefuseIt()
    {
        // Storing it would leave two original values behind one pseudonym, making that
        // pseudonym's reverse lookup ambiguous.
        var sut = CreateImportSut();

        var results = await sut.ImportTrustedBatchAsync(
            NamespaceNamed("existingNamespace"),
            [new PseudonymImportEntry("someone else", "existingPseudonym")],
            CancellationToken.None
        );

        results[0].Outcome.Should().Be(PseudonymImportOutcome.PseudonymValueConflict);
        var stored = await FindStoredAsync("existingNamespace", "existingPseudonym");
        stored!.OriginalValue.Should().Be("an original value");
    }

    [Fact]
    public async Task ImportTrustedBatchAsync_WithTwoOriginalValuesClaimingOnePseudonymInOneBatch_ShouldOnlyStoreTheFirst()
    {
        var sut = CreateImportSut();

        var results = await sut.ImportTrustedBatchAsync(
            NamespaceNamed("emptyNamespace"),
            [
                new PseudonymImportEntry("alice", "contested-psn"),
                new PseudonymImportEntry("bob", "contested-psn"),
            ],
            CancellationToken.None
        );

        results[0].Outcome.Should().Be(PseudonymImportOutcome.Imported);
        results[1].Outcome.Should().Be(PseudonymImportOutcome.PseudonymValueConflict);
        var stored = await FindStoredAsync("emptyNamespace", "contested-psn");
        stored!.OriginalValue.Should().Be("alice");
    }

    [Fact]
    public async Task ImportTrustedBatchAsync_WithAValueFailingTheValidationRegex_ShouldRefuseOnlyThatRow()
    {
        // One bad row in a file must not take the rest of the batch down with it - the whole
        // reason this reports outcomes rather than throwing the way the generating paths do.
        var sut = CreateImportSut();

        var results = await sut.ImportTrustedBatchAsync(
            NamespaceNamed("emptyNamespace", originalValueValidationRegex: "^[0-9]+$"),
            [
                new PseudonymImportEntry("not-a-number", "regex-psn-1"),
                new PseudonymImportEntry("12345", "regex-psn-2"),
            ],
            CancellationToken.None
        );

        results[0].Outcome.Should().Be(PseudonymImportOutcome.InvalidOriginalValue);
        results[1].Outcome.Should().Be(PseudonymImportOutcome.Imported);
    }

    [Fact]
    public async Task ImportTrustedBatchAsync_IntoAMultiPsnNamespace_ShouldAppendAtTheNextSequenceNumber()
    {
        // A namespace that allows several pseudonyms per original value grows rather than
        // conflicting - the same semantics the generating create path has.
        var sut = CreateImportSut();
        var @namespace = NamespaceNamed("multiPsnNamespace", allowsMultiplePseudonyms: true);

        await sut.ImportTrustedBatchAsync(
            @namespace,
            [new PseudonymImportEntry("alice", "multi-psn-1")],
            CancellationToken.None
        );
        var second = await sut.ImportTrustedBatchAsync(
            @namespace,
            [new PseudonymImportEntry("alice", "multi-psn-2")],
            CancellationToken.None
        );

        second[0].Outcome.Should().Be(PseudonymImportOutcome.Imported);
        var stored = await new PseudonymRepository(ContextFactory).FindAllByOriginalValueAsync(
            "multiPsnNamespace",
            "alice",
            CancellationToken.None
        );
        stored.Select(p => p.PseudonymValue).Should().Equal("multi-psn-1", "multi-psn-2");
        stored.Select(p => p.SequenceNumber).Should().Equal(0, 1);
    }

    [Fact]
    public async Task ImportTrustedBatchAsync_WithNoEntries_ShouldReturnNothing()
    {
        var sut = CreateImportSut();

        var results = await sut.ImportTrustedBatchAsync(
            NamespaceNamed("emptyNamespace"),
            [],
            CancellationToken.None
        );

        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "psn")]
    [InlineData("   ", "psn")]
    [InlineData("original", "")]
    [InlineData("original", "   ")]
    public async Task ImportTrustedBatchAsync_WithABlankValue_ShouldThrowArgumentException(
        string originalValue,
        string pseudonymValue
    )
    {
        // The import job runner drops blank cells before they get here, so one reaching this
        // point means a caller built the batch wrong - that's a bug, not a data problem to
        // report per row.
        var sut = CreateImportSut();

        var act = () =>
            sut.ImportTrustedBatchAsync(
                NamespaceNamed("emptyNamespace"),
                [new PseudonymImportEntry(originalValue, pseudonymValue)],
                CancellationToken.None
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
