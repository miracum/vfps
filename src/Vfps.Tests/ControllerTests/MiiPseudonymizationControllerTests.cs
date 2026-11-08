using System.Security.Claims;
using FakeItEasy;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Vfps.Config;
using Vfps.Fhir.Mii;
using Vfps.Fhir.Mii.V2026;
using Vfps.Tests.ServiceTests;
using Task = System.Threading.Tasks.Task;

namespace Vfps.Tests.ControllerTests;

public class MiiPseudonymizationControllerTests : ServiceTestBase
{
    private const string ContextSystem = "https://example.org/psn-contexts";

    private readonly MiiPseudonymizationController sut;

    // A connection per context: batch entries run concurrently, each on contexts of its own.
    public MiiPseudonymizationControllerTests()
        : base(connectionPerContext: true)
    {
        sut = CreateSut();
    }

    private MiiPseudonymizationController CreateSut(
        AuthorizationConfig? config = null,
        ClaimsPrincipal? user = null,
        params Data.Models.NamespaceAccessGrant[] grants
    ) =>
        new(
            A.Fake<ILogger<MiiPseudonymizationController>>(),
            CreatePseudonymAppService(
                new NamespaceRepository(ContextFactory),
                new PseudonymRepository(ContextFactory),
                config,
                grants
            )
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal() },
            },
        };

    private static ClaimsPrincipal UserWithRoles(params string[] roles) =>
        new(
            new ClaimsIdentity(
                roles.Select(r => new System.Security.Claims.Claim("roles", r)),
                "test"
            )
        );

    private static AuthorizationConfig AuthorizationEnabled =>
        new() { IsEnabled = true, AdminRoles = ["admin"] };

    private static Parameters Request(params (string Name, DataType Value)[] parameters) =>
        new()
        {
            Parameter =
            [
                .. parameters.Select(p => new Parameters.ParameterComponent
                {
                    Name = p.Name,
                    Value = p.Value,
                }),
            ],
        };

    private static Identifier Context(string namespaceName) => new(ContextSystem, namespaceName);

    private static Parameters ContextAndOriginal(string namespaceName, string originalValue) =>
        Request(
            ("context", Context(namespaceName)),
            ("original", new Identifier("https://example.org/patient-ids", originalValue))
        );

    private static Parameters PseudonymizeMultipleRequest(
        string namespaceName,
        string originalValue,
        int count
    )
    {
        var request = ContextAndOriginal(namespaceName, originalValue);
        request.Add("count", new Integer(count));
        return request;
    }

    private static T ResourceOf<T>(ObjectResult result, int expectedStatusCode)
        where T : Resource
    {
        result.StatusCode.Should().Be(expectedStatusCode);
        return result.Value.Should().BeOfType<T>().Which;
    }

    private static OperationOutcome.IssueComponent IssueOf(
        ObjectResult result,
        int expectedStatusCode
    ) =>
        ResourceOf<OperationOutcome>(result, expectedStatusCode)
            .Issue.Should()
            .ContainSingle()
            .Which;

    [Fact]
    public async Task Pseudonymize_WithAStoredOriginalValue_ShouldReturnItsPseudonymAndEchoTheInputs()
    {
        var request = ContextAndOriginal("existingNamespace", "an original value");

        var result = await sut.Pseudonymize(request, TestContext.Current.CancellationToken);

        var response = ResourceOf<Parameters>(result, 200);
        response
            .GetSingleValue<Identifier>("context")
            .Should()
            .BeSameAs(request.Parameter[0].Value);
        response
            .GetSingleValue<Identifier>("original")
            .Should()
            .BeSameAs(request.Parameter[1].Value);
        var pseudonym = response.GetSingleValue<Identifier>("pseudonym")!;
        pseudonym.Value.Should().Be("existingPseudonym");
        pseudonym.System.Should().Be(ContextSystem);
    }

    [Fact]
    public async Task Pseudonymize_WithANewOriginalValue_ShouldCreateOnePseudonymAndReturnItAgainOnRepeat()
    {
        var request = ContextAndOriginal("emptyNamespace", "new value");

        var first = await sut.Pseudonymize(request, TestContext.Current.CancellationToken);
        var second = await sut.Pseudonymize(request, TestContext.Current.CancellationToken);

        var firstValue = ResourceOf<Parameters>(first, 200)
            .GetSingleValue<Identifier>("pseudonym")!
            .Value;
        firstValue.Should().StartWith("pre-").And.EndWith("-suf");
        ResourceOf<Parameters>(second, 200)
            .GetSingleValue<Identifier>("pseudonym")!
            .Value.Should()
            .Be(firstValue);
        InMemoryPseudonymContext
            .Pseudonyms.Count(p => p.NamespaceName == "emptyNamespace")
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Pseudonymize_InAMultiPseudonymContext_ShouldReturnUnprocessableAndCreateNothing()
    {
        var result = await sut.Pseudonymize(
            ContextAndOriginal("multiPsnNamespace", "a value"),
            TestContext.Current.CancellationToken
        );

        var issue = IssueOf(result, 422);
        issue.Code.Should().Be(OperationOutcome.IssueType.BusinessRule);
        issue.Diagnostics.Should().Contain("$pseudonymize-multiple");
        InMemoryPseudonymContext
            .Pseudonyms.Any(p => p.NamespaceName == "multiPsnNamespace")
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task Pseudonymize_WithAnUnknownContext_ShouldReturnNotFound()
    {
        var result = await sut.Pseudonymize(
            ContextAndOriginal("doesNotExist", "a value"),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 404).Code.Should().Be(OperationOutcome.IssueType.NotFound);
    }

    [Fact]
    public async Task Pseudonymize_WithANonMatchingValidationRegex_ShouldReturnUnprocessable()
    {
        InMemoryPseudonymContext.Namespaces.Add(
            new Data.Models.Namespace
            {
                Name = "namespaceWithRegex",
                PseudonymLength = 16,
                CreatedAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                OriginalValueValidationRegex = "^[0-9]+$",
            }
        );
        InMemoryPseudonymContext.SaveChanges();
        InMemoryPseudonymContext.ChangeTracker.Clear();

        var result = await sut.Pseudonymize(
            ContextAndOriginal("namespaceWithRegex", "not-a-number"),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 422).Code.Should().Be(OperationOutcome.IssueType.BusinessRule);
    }

    [Fact]
    public async Task Pseudonymize_WithoutWriteAccess_ShouldReturnForbidden()
    {
        var restricted = CreateSut(
            AuthorizationEnabled,
            UserWithRoles("reader"),
            Grants.ForRole("existingNamespace", "reader", read: true, reverseLookup: true)
        );

        var result = await restricted.Pseudonymize(
            ContextAndOriginal("existingNamespace", "a value"),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 403).Code.Should().Be(OperationOutcome.IssueType.Forbidden);
    }

    [Fact]
    public async Task Pseudonymize_WithoutAnyGrant_ShouldReturnForbiddenEvenForAnUnknownContext()
    {
        // Forbidden rather than not found: the permission check comes first, so a caller without
        // access can't probe which contexts exist.
        var restricted = CreateSut(AuthorizationEnabled, UserWithRoles("nobody"));

        var result = await restricted.Pseudonymize(
            ContextAndOriginal("doesNotExist", "a value"),
            TestContext.Current.CancellationToken
        );

        result.StatusCode.Should().Be(403);
    }

    public static TheoryData<string, Parameters> MalformedRequests =>
        new()
        {
            { "missing original", Request(("context", Context("existingNamespace"))) },
            { "missing context", Request(("original", new Identifier(null, "a value"))) },
            {
                "original of another type",
                Request(
                    ("context", Context("existingNamespace")),
                    ("original", new FhirString("a value"))
                )
            },
            {
                "blank original",
                Request(
                    ("context", Context("existingNamespace")),
                    ("original", new Identifier(null, "  "))
                )
            },
            {
                "original without a value",
                Request(
                    ("context", Context("existingNamespace")),
                    ("original", new Identifier { System = "https://example.org/patient-ids" })
                )
            },
            {
                "original given twice",
                Request(
                    ("context", Context("existingNamespace")),
                    ("original", new Identifier(null, "one")),
                    ("original", new Identifier(null, "two"))
                )
            },
        };

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public async Task Pseudonymize_WithMalformedParameters_ShouldReturnBadRequestAndCreateNothing(
        string because,
        Parameters request
    )
    {
        var countBefore = InMemoryPseudonymContext.Pseudonyms.Count();

        var result = await sut.Pseudonymize(request, TestContext.Current.CancellationToken);

        IssueOf(result, 400).Code.Should().Be(OperationOutcome.IssueType.Invalid, because);
        InMemoryPseudonymContext.Pseudonyms.Count().Should().Be(countBefore);
    }

    [Fact]
    public async Task Pseudonymize_WithoutABody_ShouldReturnBadRequest()
    {
        var result = await sut.Pseudonymize(null, TestContext.Current.CancellationToken);

        IssueOf(result, 400).Code.Should().Be(OperationOutcome.IssueType.Invalid);
    }

    [Fact]
    public async Task GetPseudonym_WithAStoredOriginalValue_ShouldReturnItsPseudonym()
    {
        var result = await sut.GetPseudonym(
            ContextAndOriginal("existingNamespace", "an original value"),
            TestContext.Current.CancellationToken
        );

        ResourceOf<Parameters>(result, 200)
            .GetSingleValue<Identifier>("pseudonym")!
            .Value.Should()
            .Be("existingPseudonym");
    }

    [Fact]
    public async Task GetPseudonym_WithAnUnknownOriginalValue_ShouldReturnNotFoundAndCreateNothing()
    {
        var countBefore = InMemoryPseudonymContext.Pseudonyms.Count();

        var result = await sut.GetPseudonym(
            ContextAndOriginal("existingNamespace", "never stored"),
            TestContext.Current.CancellationToken
        );

        var issue = IssueOf(result, 404);
        issue.Code.Should().Be(OperationOutcome.IssueType.NotFound);
        issue.Diagnostics.Should().NotContain("never stored");
        InMemoryPseudonymContext.Pseudonyms.Count().Should().Be(countBefore);
    }

    [Fact]
    public async Task GetPseudonym_InAMultiPseudonymContext_ShouldReturnUnprocessable()
    {
        var result = await sut.GetPseudonym(
            ContextAndOriginal("multiPsnNamespace", "a value"),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 422).Code.Should().Be(OperationOutcome.IssueType.BusinessRule);
    }

    [Fact]
    public async Task PseudonymizeMultiple_WithACount_ShouldAddThatManyNewPseudonymsEachCall()
    {
        var first = await sut.PseudonymizeMultiple(
            PseudonymizeMultipleRequest("multiPsnNamespace", "a value", 3),
            TestContext.Current.CancellationToken
        );
        var second = await sut.PseudonymizeMultiple(
            PseudonymizeMultipleRequest("multiPsnNamespace", "a value", 2),
            TestContext.Current.CancellationToken
        );

        var firstValues = PseudonymValues(ResourceOf<Parameters>(first, 200));
        var secondValues = PseudonymValues(ResourceOf<Parameters>(second, 200));
        firstValues.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        secondValues.Should().HaveCount(2).And.NotIntersectWith(firstValues);
        InMemoryPseudonymContext
            .Pseudonyms.Count(p => p.NamespaceName == "multiPsnNamespace")
            .Should()
            .Be(5);
    }

    [Fact]
    public async Task PseudonymizeMultiple_ShouldEchoContextAndOriginalInEveryPseudonym()
    {
        var request = PseudonymizeMultipleRequest("multiPsnNamespace", "a value", 2);

        var result = await sut.PseudonymizeMultiple(request, TestContext.Current.CancellationToken);

        var pseudonyms = ResourceOf<Parameters>(result, 200).Parameter;
        pseudonyms.Should().HaveCount(2).And.OnlyContain(p => p.Name == "pseudonym");
        foreach (var pseudonym in pseudonyms)
        {
            pseudonym.Part.Select(p => p.Name).Should().Equal("context", "original", "value");
            pseudonym.Part[0].Value.Should().BeSameAs(request.Parameter[0].Value);
            pseudonym.Part[1].Value.Should().BeSameAs(request.Parameter[1].Value);
            pseudonym
                .Part[2]
                .Value.Should()
                .BeOfType<Identifier>()
                .Which.System.Should()
                .Be(ContextSystem);
        }
    }

    [Fact]
    public async Task PseudonymizeMultiple_WithACountOfZero_ShouldReturnEveryStoredPseudonymAndAddNone()
    {
        var created = PseudonymValues(
            ResourceOf<Parameters>(
                await sut.PseudonymizeMultiple(
                    PseudonymizeMultipleRequest("multiPsnNamespace", "a value", 3),
                    TestContext.Current.CancellationToken
                ),
                200
            )
        );

        var result = await sut.PseudonymizeMultiple(
            PseudonymizeMultipleRequest("multiPsnNamespace", "a value", 0),
            TestContext.Current.CancellationToken
        );

        PseudonymValues(ResourceOf<Parameters>(result, 200)).Should().Equal(created);
        InMemoryPseudonymContext
            .Pseudonyms.Count(p => p.NamespaceName == "multiPsnNamespace")
            .Should()
            .Be(3);
    }

    [Fact]
    public async Task PseudonymizeMultiple_WithACountOfZeroAndNothingStored_ShouldReturnNotFound()
    {
        var result = await sut.PseudonymizeMultiple(
            PseudonymizeMultipleRequest("multiPsnNamespace", "never stored", 0),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 404).Code.Should().Be(OperationOutcome.IssueType.NotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PseudonymizeMultiple_InASinglePseudonymContext_ShouldReturnUnprocessable(
        int count
    )
    {
        var result = await sut.PseudonymizeMultiple(
            PseudonymizeMultipleRequest("existingNamespace", "an original value", count),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 422).Code.Should().Be(OperationOutcome.IssueType.BusinessRule);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(MiiPseudonymizationController.MaxPseudonymizeMultipleCount + 1)]
    public async Task PseudonymizeMultiple_WithACountOutOfRange_ShouldReturnBadRequestAndCreateNothing(
        int count
    )
    {
        var result = await sut.PseudonymizeMultiple(
            PseudonymizeMultipleRequest("multiPsnNamespace", "a value", count),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 400).Code.Should().Be(OperationOutcome.IssueType.Invalid);
        InMemoryPseudonymContext
            .Pseudonyms.Any(p => p.NamespaceName == "multiPsnNamespace")
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task PseudonymizeMultiple_WithoutACount_ShouldReturnBadRequest()
    {
        var result = await sut.PseudonymizeMultiple(
            ContextAndOriginal("multiPsnNamespace", "a value"),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 400).Code.Should().Be(OperationOutcome.IssueType.Invalid);
    }

    [Fact]
    public async Task DePseudonymize_WithAStoredPseudonym_ShouldReturnItsOriginalValue()
    {
        var request = Request(
            ("context", Context("existingNamespace")),
            ("pseudonym", new Identifier(ContextSystem, "existingPseudonym"))
        );

        var result = await sut.DePseudonymize(request, TestContext.Current.CancellationToken);

        var original = ResourceOf<Parameters>(result, 200).Parameter.Should().ContainSingle().Which;
        original.Name.Should().Be("original");
        original.Part.Select(p => p.Name).Should().Equal("context", "value", "pseudonym");
        original.Part[0].Value.Should().BeSameAs(request.Parameter[0].Value);
        original
            .Part[1]
            .Value.Should()
            .BeOfType<Identifier>()
            .Which.Value.Should()
            .Be("an original value");
        original.Part[2].Value.Should().BeSameAs(request.Parameter[1].Value);
    }

    [Theory]
    [InlineData("existingNamespace", "notAPseudonym")]
    [InlineData("doesNotExist", "existingPseudonym")]
    public async Task DePseudonymize_WithAnUnknownPseudonymOrContext_ShouldReturnNotFound(
        string namespaceName,
        string pseudonymValue
    )
    {
        var result = await sut.DePseudonymize(
            Request(
                ("context", Context(namespaceName)),
                ("pseudonym", new Identifier(null, pseudonymValue))
            ),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 404).Code.Should().Be(OperationOutcome.IssueType.NotFound);
    }

    [Fact]
    public async Task DePseudonymize_WithWriteButNoReverseLookupAccess_ShouldReturnForbidden()
    {
        var restricted = CreateSut(
            AuthorizationEnabled,
            UserWithRoles("writer"),
            Grants.ForRole("existingNamespace", "writer", read: true, write: true)
        );

        var result = await restricted.DePseudonymize(
            Request(
                ("context", Context("existingNamespace")),
                ("pseudonym", new Identifier(null, "existingPseudonym"))
            ),
            TestContext.Current.CancellationToken
        );

        IssueOf(result, 403).Code.Should().Be(OperationOutcome.IssueType.Forbidden);
    }

    [Fact]
    public void DeletePseudonymAndAnonymizeOriginal_ShouldReturnNotImplemented()
    {
        IssueOf(sut.DeletePseudonym(), 501)
            .Code.Should()
            .Be(OperationOutcome.IssueType.NotSupported);
        IssueOf(sut.AnonymizeOriginal(), 501)
            .Code.Should()
            .Be(OperationOutcome.IssueType.NotSupported);
    }

    private static Bundle.EntryComponent BatchEntry(string url, Resource? resource) =>
        new()
        {
            Resource = resource,
            Request = new Bundle.RequestComponent { Method = Bundle.HTTPVerb.POST, Url = url },
        };

    [Fact]
    public async Task ProcessBatch_ShouldAnswerEveryEntryIndependentlyAndInOrder()
    {
        var batch = new Bundle
        {
            Type = Bundle.BundleType.Batch,
            Entry =
            [
                BatchEntry(
                    "$pseudonymize",
                    ContextAndOriginal("existingNamespace", "an original value")
                ),
                BatchEntry(
                    "$get-pseudonym",
                    ContextAndOriginal("existingNamespace", "never stored")
                ),
                BatchEntry("$pseudonymize", ContextAndOriginal("doesNotExist", "a value")),
                BatchEntry(
                    "$pseudonymize-multiple",
                    PseudonymizeMultipleRequest("multiPsnNamespace", "a value", 2)
                ),
                BatchEntry("$delete-pseudonym", ContextAndOriginal("existingNamespace", "a value")),
                BatchEntry(
                    "$unknown-operation",
                    ContextAndOriginal("existingNamespace", "a value")
                ),
                BatchEntry("$pseudonymize", new Patient()),
                new()
                {
                    Resource = ContextAndOriginal("existingNamespace", "a value"),
                    Request = new Bundle.RequestComponent
                    {
                        Method = Bundle.HTTPVerb.GET,
                        Url = "$pseudonymize",
                    },
                },
            ],
        };

        var result = await sut.ProcessBatch(batch, TestContext.Current.CancellationToken);

        var response = ResourceOf<Bundle>(result, 200);
        response.Type.Should().Be(Bundle.BundleType.BatchResponse);
        response
            .Entry.Select(e => e.Response!.Status)
            .Should()
            .Equal(
                "200 OK",
                "404 Not Found",
                "404 Not Found",
                "200 OK",
                "501 Not Implemented",
                "404 Not Found",
                "400 Bad Request",
                "405 Method Not Allowed"
            );
        response
            .Entry[0]
            .Resource.Should()
            .BeOfType<Parameters>()
            .Which.GetSingleValue<Identifier>("pseudonym")!
            .Value.Should()
            .Be("existingPseudonym");
        response.Entry[1].Resource.Should().BeOfType<OperationOutcome>();
        response
            .Entry[3]
            .Resource.Should()
            .BeOfType<Parameters>()
            .Which.Parameter.Should()
            .HaveCount(2);
    }

    [Fact]
    public async Task ProcessBatch_WithManyEntries_ShouldAnswerThemInTheRequestsOrder()
    {
        // Several times as many entries as run at once, so that they finish out of order.
        var originalValues = Enumerable
            .Range(0, FhirBatch.MaxConcurrentEntries * 10)
            .Select(i => $"value {i}")
            .ToList();
        var batch = new Bundle
        {
            Type = Bundle.BundleType.Batch,
            Entry =
            [
                .. originalValues.Select(value =>
                    BatchEntry("$pseudonymize", ContextAndOriginal("emptyNamespace", value))
                ),
            ],
        };

        var result = await sut.ProcessBatch(batch, TestContext.Current.CancellationToken);

        var response = ResourceOf<Bundle>(result, 200);
        response.Entry.Select(e => e.Response!.Status).Should().AllBe("200 OK");
        response
            .Entry.Select(e =>
                ((Parameters)e.Resource!).GetSingleValue<Identifier>("original")!.Value
            )
            .Should()
            .Equal(originalValues);
        InMemoryPseudonymContext
            .Pseudonyms.Count(p => p.NamespaceName == "emptyNamespace")
            .Should()
            .Be(originalValues.Count);
    }

    [Fact]
    public async Task ProcessBatch_WithATransactionBundle_ShouldReturnBadRequestAndRunNothing()
    {
        var transaction = new Bundle
        {
            Type = Bundle.BundleType.Transaction,
            Entry = [BatchEntry("$pseudonymize", ContextAndOriginal("emptyNamespace", "a value"))],
        };

        var result = await sut.ProcessBatch(transaction, TestContext.Current.CancellationToken);

        IssueOf(result, 400).Code.Should().Be(OperationOutcome.IssueType.NotSupported);
        InMemoryPseudonymContext
            .Pseudonyms.Any(p => p.NamespaceName == "emptyNamespace")
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task ProcessBatch_WithMoreEntriesThanAllowed_ShouldReturnBadRequest()
    {
        var batch = new Bundle
        {
            Type = Bundle.BundleType.Batch,
            Entry =
            [
                .. Enumerable
                    .Range(0, FhirBatch.MaxEntries + 1)
                    .Select(i =>
                        BatchEntry(
                            "$get-pseudonym",
                            ContextAndOriginal("existingNamespace", $"{i}")
                        )
                    ),
            ],
        };

        var result = await sut.ProcessBatch(batch, TestContext.Current.CancellationToken);

        IssueOf(result, 400).Code.Should().Be(OperationOutcome.IssueType.TooCostly);
    }

    [Fact]
    public async Task ProcessBatch_WithoutABody_ShouldReturnBadRequest()
    {
        var result = await sut.ProcessBatch(null, TestContext.Current.CancellationToken);

        IssueOf(result, 400).Code.Should().Be(OperationOutcome.IssueType.Invalid);
    }

    [Fact]
    public void GetMetadata_ShouldNameTheImplementationGuideReleaseAndTheImplementedOperations()
    {
        var metadata = sut.GetMetadata();

        metadata
            .ImplementationGuide.Should()
            .Equal(
                "https://www.medizininformatik-initiative.de/fhir/ext/mii-interface-module-pseudonymization|2026.1.0"
            );
        metadata.FhirVersion.Should().Be(FHIRVersion.N4_0_1);
        var rest = metadata.Rest.Should().ContainSingle().Which;
        rest.Interaction.Select(i => i.Code)
            .Should()
            .Equal(CapabilityStatement.SystemRestfulInteraction.Batch);
        rest.Operation.Select(o => o.Name)
            .Should()
            .Equal("pseudonymize", "pseudonymize-multiple", "get-pseudonym", "de-pseudonymize");
        rest.Operation[0]
            .Definition.Should()
            .Be(
                "https://www.medizininformatik-initiative.de/fhir/ext/mii-interface-module-pseudonymization/OperationDefinition/Pseudonymize"
            );
    }

    private static List<string?> PseudonymValues(Parameters response) =>
        [
            .. response
                .Parameter.Where(p => p.Name == "pseudonym")
                .Select(p =>
                    (p.Part.Single(part => part.Name == "value").Value as Identifier)?.Value
                ),
        ];
}
