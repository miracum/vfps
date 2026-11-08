using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Vfps.AppServices;

namespace Vfps.Fhir.Mii.V2026;

/// <summary>
/// Version 2026 of the MII pseudonymization interface, the FHIR API the Medizininformatik-Initiative
/// specifies for pseudonymization services
/// (https://medizininformatik-initiative.github.io/mii-interface-module-pseudonymization/). A
/// pseudonymization context is a vfps namespace.
/// </summary>
/// <remarks>
/// <para>
/// Each major version of the IG gets its own base URL and its own folder like this one, holding
/// only what that version defines: the operations' parameters (see OperationParameters.cs) and how
/// they map onto the app services. Everything below that is shared - the app services and their
/// permission checks, the error mapping, batch processing - so a new major version is a copy of
/// this folder with the mapping changed, served next to this one until 2026 is retired.
/// </para>
/// <para>
/// Not an [ApiController], unlike <see cref="FhirController"/>: that attribute answers a body the
/// input formatter couldn't read with a ProblemDetails 400 before the action runs, and every error
/// this API answers with is meant to be an OperationOutcome.
/// </para>
/// </remarks>
[Route(BasePath)]
[Produces(FhirJson)]
[ProducesResponseType(typeof(OperationOutcome), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(OperationOutcome), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(OperationOutcome), StatusCodes.Status404NotFound)]
[ProducesResponseType(typeof(OperationOutcome), StatusCodes.Status422UnprocessableEntity)]
[ProducesResponseType(typeof(OperationOutcome), StatusCodes.Status500InternalServerError)]
public class MiiPseudonymizationController(
    ILogger<MiiPseudonymizationController> logger,
    IPseudonymAppService pseudonymAppService
) : ControllerBase
{
    /// <summary>The FHIR base URL this version is served from.</summary>
    public const string BasePath = "fhir/mii/2026";

    // Declared per action that takes a body rather than on the class: applied to GET metadata
    // too, the constraint rejects a request carrying no Content-Type - which a GET never does -
    // and routing answers it with a 404.
    private const string FhirJson = "application/fhir+json";

    /// <summary>The IG's canonical URL, which its OperationDefinitions' canonical URLs extend.</summary>
    public const string ImplementationGuideUrl =
        "https://www.medizininformatik-initiative.de/fhir/ext/mii-interface-module-pseudonymization";

    /// <summary>
    /// The IG release this controller implements. The IG bumps its major version - the 2026 in
    /// <see cref="BasePath"/> - for every breaking change, so a later 2026.x release is served
    /// from the same base URL, and only the CapabilityStatement names the exact release.
    /// </summary>
    public const string ImplementationGuideVersion = "2026.1.0";

    /// <summary>
    /// The most pseudonyms one <c>$pseudonymize-multiple</c> call may add. Each is stored by its
    /// own Append - which is what keeps two concurrent calls from being handed the same new
    /// pseudonym - and each Append reads every pseudonym already stored for the value, so the
    /// cost of one call grows faster than its count.
    /// </summary>
    public const int MaxPseudonymizeMultipleCount = 100;

    // The operations the CapabilityStatement advertises, with the id of the IG's OperationDefinition
    // for each. $delete-pseudonym and $anonymize-original are answered but not implemented, so a
    // client reading the metadata doesn't come to rely on them.
    private static readonly (string Code, string DefinitionId)[] AdvertisedOperations =
    [
        ("pseudonymize", "Pseudonymize"),
        ("pseudonymize-multiple", "PseudonymizeMultiple"),
        ("get-pseudonym", "GetPseudonym"),
        ("de-pseudonymize", "DePseudonymize"),
    ];

    /// <summary>
    /// Processes a batch Bundle whose entries each invoke one of the operations below, with
    /// <c>request.url</c> set to the operation's name (<c>$pseudonymize</c>). Every entry is
    /// processed independently and answered with its own status and resource. Transaction
    /// Bundles are not supported.
    /// </summary>
    /// <param name="bundle">A FHIR Bundle of type <c>batch</c>.</param>
    /// <param name="cancellationToken">A cancellation token to abort the request.</param>
    /// <returns>A batch-response Bundle, or an OperationOutcome if the Bundle can't be processed.</returns>
    [HttpPost]
    [Consumes(FhirJson, "application/json")]
    [ProducesResponseType(typeof(Bundle), StatusCodes.Status200OK)]
    public async Task<ObjectResult> ProcessBatch(
        [FromBody] Bundle? bundle,
        CancellationToken cancellationToken = default
    )
    {
        if (bundle is null)
        {
            return ToActionResult(MalformedBody(nameof(Bundle)));
        }

        // Read once, here: the entries run concurrently, and HttpContext isn't meant to be used
        // from several threads at a time.
        var user = User;

        var operations = new Dictionary<string, FhirOperation>(StringComparer.Ordinal)
        {
            ["$pseudonymize"] = (p, ct) => PseudonymizeAsync(p, user, ct),
            ["$pseudonymize-multiple"] = (p, ct) => PseudonymizeMultipleAsync(p, user, ct),
            ["$get-pseudonym"] = (p, ct) => GetPseudonymAsync(p, user, ct),
            ["$de-pseudonymize"] = (p, ct) => DePseudonymizeAsync(p, user, ct),
            ["$delete-pseudonym"] = (_, _) =>
                System.Threading.Tasks.Task.FromResult(NotImplementedYet("$delete-pseudonym")),
            ["$anonymize-original"] = (_, _) =>
                System.Threading.Tasks.Task.FromResult(NotImplementedYet("$anonymize-original")),
        };

        return ToActionResult(
            await FhirBatch.ProcessAsync(bundle, operations, logger, cancellationToken)
        );
    }

    /// <summary>
    /// Returns the pseudonym for an original value in a single-pseudonym context, creating it if
    /// the context holds none yet. Requires write access to the context.
    /// </summary>
    /// <param name="parameters">A Parameters resource with the <c>context</c> and <c>original</c> Identifiers.</param>
    /// <param name="cancellationToken">A cancellation token to abort the request.</param>
    /// <returns>The context, the original and the <c>pseudonym</c> Identifier.</returns>
    [HttpPost("$pseudonymize")]
    [Consumes(FhirJson, "application/json")]
    [ProducesResponseType(typeof(Parameters), StatusCodes.Status200OK)]
    public Task<ObjectResult> Pseudonymize(
        [FromBody] Parameters? parameters,
        CancellationToken cancellationToken = default
    ) => RunAsync(parameters, PseudonymizeAsync, cancellationToken);

    /// <summary>
    /// Adds <c>count</c> new pseudonyms for an original value in a multi-pseudonym context, or,
    /// with a <c>count</c> of 0, returns every pseudonym the context already holds for it.
    /// Requires write access to the context.
    /// </summary>
    /// <param name="parameters">
    /// A Parameters resource with the <c>context</c> and <c>original</c> Identifiers and the
    /// <c>count</c> integer.
    /// </param>
    /// <param name="cancellationToken">A cancellation token to abort the request.</param>
    /// <returns>
    /// One <c>pseudonym</c> parameter per pseudonym - only the ones just added, for a count above 0.
    /// </returns>
    [HttpPost("$pseudonymize-multiple")]
    [Consumes(FhirJson, "application/json")]
    [ProducesResponseType(typeof(Parameters), StatusCodes.Status200OK)]
    public Task<ObjectResult> PseudonymizeMultiple(
        [FromBody] Parameters? parameters,
        CancellationToken cancellationToken = default
    ) => RunAsync(parameters, PseudonymizeMultipleAsync, cancellationToken);

    /// <summary>
    /// Returns the pseudonym already stored for an original value in a single-pseudonym context,
    /// creating nothing. Requires write access to the context.
    /// </summary>
    /// <param name="parameters">A Parameters resource with the <c>context</c> and <c>original</c> Identifiers.</param>
    /// <param name="cancellationToken">A cancellation token to abort the request.</param>
    /// <returns>The context, the original and the <c>pseudonym</c> Identifier, or a 404.</returns>
    [HttpPost("$get-pseudonym")]
    [Consumes(FhirJson, "application/json")]
    [ProducesResponseType(typeof(Parameters), StatusCodes.Status200OK)]
    public Task<ObjectResult> GetPseudonym(
        [FromBody] Parameters? parameters,
        CancellationToken cancellationToken = default
    ) => RunAsync(parameters, GetPseudonymAsync, cancellationToken);

    /// <summary>
    /// Returns the original value of a pseudonym. Requires reverse-lookup access to the context.
    /// </summary>
    /// <param name="parameters">A Parameters resource with the <c>context</c> and <c>pseudonym</c> Identifiers.</param>
    /// <param name="cancellationToken">A cancellation token to abort the request.</param>
    /// <returns>An <c>original</c> parameter holding the original value, or a 404.</returns>
    [HttpPost("$de-pseudonymize")]
    [Consumes(FhirJson, "application/json")]
    [ProducesResponseType(typeof(Parameters), StatusCodes.Status200OK)]
    public Task<ObjectResult> DePseudonymize(
        [FromBody] Parameters? parameters,
        CancellationToken cancellationToken = default
    ) => RunAsync(parameters, DePseudonymizeAsync, cancellationToken);

    /// <summary>Not implemented yet: always answers 501 Not Implemented.</summary>
    /// <returns>An OperationOutcome saying the operation is not implemented yet.</returns>
    [HttpPost("$delete-pseudonym")]
    [ProducesResponseType(typeof(OperationOutcome), StatusCodes.Status501NotImplemented)]
    public ObjectResult DeletePseudonym() => ToActionResult(NotImplementedYet("$delete-pseudonym"));

    /// <summary>Not implemented yet: always answers 501 Not Implemented.</summary>
    /// <returns>An OperationOutcome saying the operation is not implemented yet.</returns>
    [HttpPost("$anonymize-original")]
    [ProducesResponseType(typeof(OperationOutcome), StatusCodes.Status501NotImplemented)]
    public ObjectResult AnonymizeOriginal() =>
        ToActionResult(NotImplementedYet("$anonymize-original"));

    /// <summary>
    /// Returns the CapabilityStatement of this version of the API, naming the exact IG release it
    /// implements and the operations it supports.
    /// </summary>
    /// <returns>The server's FHIR CapabilityStatement.</returns>
    [HttpGet("metadata")]
    [ProducesResponseType(typeof(CapabilityStatement), StatusCodes.Status200OK)]
    public CapabilityStatement GetMetadata() =>
        new()
        {
            Status = PublicationStatus.Active,
            Date = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:sszzz",
                CultureInfo.InvariantCulture
            ),
            Kind = CapabilityStatementKind.Instance,
            Software = new CapabilityStatement.SoftwareComponent
            {
                Name = "vfps",
                Version = typeof(MiiPseudonymizationController)
                    .Assembly.GetName()
                    .Version?.ToString(3),
            },
            Implementation = new CapabilityStatement.ImplementationComponent
            {
                Description =
                    "vfps implementation of the MII pseudonymization interface, version 2026",
            },
            FhirVersion = FHIRVersion.N4_0_1,
            Format = ["application/fhir+json"],
            ImplementationGuide = [$"{ImplementationGuideUrl}|{ImplementationGuideVersion}"],
            Rest =
            [
                new CapabilityStatement.RestComponent
                {
                    Mode = CapabilityStatement.RestfulCapabilityMode.Server,
                    Interaction =
                    [
                        new CapabilityStatement.SystemInteractionComponent
                        {
                            Code = CapabilityStatement.SystemRestfulInteraction.Batch,
                        },
                    ],
                    Operation =
                    [
                        .. AdvertisedOperations.Select(
                            operation => new CapabilityStatement.OperationComponent
                            {
                                Name = operation.Code,
                                Definition =
                                    $"{ImplementationGuideUrl}/OperationDefinition/{operation.DefinitionId}",
                            }
                        ),
                    ],
                },
            ],
        };

    private async Task<FhirOperationResult> PseudonymizeAsync(
        Parameters parameters,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryParse(
                parameters,
                SinglePseudonymRequest.FromFhirParameters,
                out var request,
                out var error
            )
            || !TryRequire(request.Context, "context", out var context, out error)
            || !TryRequire(request.Original, "original", out var original, out error)
        )
        {
            return error;
        }

        try
        {
            var @namespace = await pseudonymAppService.GetNamespaceForWriteAsync(
                context.Value,
                user,
                cancellationToken
            );

            if (@namespace.AllowsMultiplePseudonyms)
            {
                return SinglePseudonymContextRequired(
                    "$pseudonymize",
                    @namespace.Name,
                    "$pseudonymize-multiple"
                );
            }

            var pseudonyms = await pseudonymAppService.CreateTrustedAsync(
                @namespace,
                original.Value,
                1,
                cancellationToken
            );

            return SinglePseudonymResult(context, original, pseudonyms[0].PseudonymValue);
        }
        catch (Exception ex) when (AppServiceErrors.TryMap(ex, out var mapped))
        {
            return mapped;
        }
    }

    private async Task<FhirOperationResult> PseudonymizeMultipleAsync(
        Parameters parameters,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryParse(
                parameters,
                PseudonymizeMultipleRequest.FromFhirParameters,
                out var request,
                out var error
            )
            || !TryRequire(request.Context, "context", out var context, out error)
            || !TryRequire(request.Original, "original", out var original, out error)
            || !TryRequireCount(request.Count, out var count, out error)
        )
        {
            return error;
        }

        try
        {
            var @namespace = await pseudonymAppService.GetNamespaceForWriteAsync(
                context.Value,
                user,
                cancellationToken
            );

            if (!@namespace.AllowsMultiplePseudonyms)
            {
                return FhirOperationResult.Error(
                    StatusCodes.Status422UnprocessableEntity,
                    OperationOutcome.IssueType.BusinessRule,
                    "$pseudonymize-multiple requires a multi-pseudonym context, and "
                        + $"'{@namespace.Name}' holds only one pseudonym per original value. "
                        + "Use $pseudonymize or $get-pseudonym instead."
                );
            }

            IReadOnlyList<Data.Models.Pseudonym> pseudonyms;

            if (count == 0)
            {
                pseudonyms = await pseudonymAppService.ResolveTrustedAsync(
                    @namespace,
                    original.Value,
                    cancellationToken
                );

                if (pseudonyms.Count == 0)
                {
                    return NoPseudonymFound(@namespace.Name);
                }
            }
            else
            {
                // One Append per pseudonym rather than a single grow-to-N Create: two calls
                // racing for the same original value would each read the same stored count, grow
                // the set to the same size, and hand both callers the same "new" pseudonyms -
                // the very thing secondary pseudonyms exist to prevent. Not atomic: should one
                // Append fail, the ones before it stay stored without being returned, which only
                // ever leaves unused pseudonyms behind, never a shared one.
                var appended = new List<Data.Models.Pseudonym>(count);
                for (var i = 0; i < count; i++)
                {
                    appended.Add(
                        await pseudonymAppService.AppendTrustedAsync(
                            @namespace,
                            original.Value,
                            null,
                            cancellationToken
                        )
                    );
                }

                pseudonyms = appended;
            }

            return FhirOperationResult.Ok(
                new PseudonymizeMultipleResponse
                {
                    Pseudonym =
                    [
                        .. pseudonyms.Select(pseudonym => new PseudonymizeMultipleResponsePseudonym
                        {
                            Context = context.Identifier,
                            Original = original.Identifier,
                            Value = PseudonymIdentifier(context, pseudonym.PseudonymValue),
                        }),
                    ],
                }.ToFhirParameters()
            );
        }
        catch (Exception ex) when (AppServiceErrors.TryMap(ex, out var mapped))
        {
            return mapped;
        }
    }

    private async Task<FhirOperationResult> GetPseudonymAsync(
        Parameters parameters,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryParse(
                parameters,
                SinglePseudonymRequest.FromFhirParameters,
                out var request,
                out var error
            )
            || !TryRequire(request.Context, "context", out var context, out error)
            || !TryRequire(request.Original, "original", out var original, out error)
        )
        {
            return error;
        }

        try
        {
            // Write access, as for the Resolve this is: telling whether a value already has a
            // pseudonym is an existence check over the context's original values (see
            // IPseudonymAppService.ResolveAsync).
            var @namespace = await pseudonymAppService.GetNamespaceForWriteAsync(
                context.Value,
                user,
                cancellationToken
            );

            if (@namespace.AllowsMultiplePseudonyms)
            {
                return SinglePseudonymContextRequired(
                    "$get-pseudonym",
                    @namespace.Name,
                    "$pseudonymize-multiple with a count of 0"
                );
            }

            var pseudonyms = await pseudonymAppService.ResolveTrustedAsync(
                @namespace,
                original.Value,
                cancellationToken
            );

            if (pseudonyms.Count == 0)
            {
                return NoPseudonymFound(@namespace.Name);
            }

            return SinglePseudonymResult(context, original, pseudonyms[0].PseudonymValue);
        }
        catch (Exception ex) when (AppServiceErrors.TryMap(ex, out var mapped))
        {
            return mapped;
        }
    }

    private async Task<FhirOperationResult> DePseudonymizeAsync(
        Parameters parameters,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (
            !TryParse(
                parameters,
                DePseudonymizeRequest.FromFhirParameters,
                out var request,
                out var error
            )
            || !TryRequire(request.Context, "context", out var context, out error)
            || !TryRequire(request.Pseudonym, "pseudonym", out var pseudonym, out error)
        )
        {
            return error;
        }

        try
        {
            var stored = await pseudonymAppService.ReverseLookupAsync(
                context.Value,
                pseudonym.Value,
                user,
                cancellationToken
            );

            // A context that doesn't exist answers the same as a pseudonym it doesn't hold: the
            // reverse lookup doesn't tell them apart, and the IG answers both with a 404.
            if (stored is null)
            {
                return FhirOperationResult.Error(
                    StatusCodes.Status404NotFound,
                    OperationOutcome.IssueType.NotFound,
                    $"The pseudonymization context '{context.Value}' holds no such pseudonym."
                );
            }

            return FhirOperationResult.Ok(
                new DePseudonymizeResponse
                {
                    Original = new DePseudonymizeResponseOriginal
                    {
                        Context = context.Identifier,
                        // Without a system: vfps stores the original value alone, and whatever
                        // system it was pseudonymized with isn't known here.
                        Value = new Identifier { Value = stored.OriginalValue },
                        Pseudonym = pseudonym.Identifier,
                    },
                }.ToFhirParameters()
            );
        }
        catch (Exception ex) when (AppServiceErrors.TryMap(ex, out var mapped))
        {
            return mapped;
        }
    }

    private async Task<ObjectResult> RunAsync(
        Parameters? parameters,
        Func<Parameters, ClaimsPrincipal, CancellationToken, Task<FhirOperationResult>> operation,
        CancellationToken cancellationToken
    )
    {
        if (parameters is null)
        {
            return ToActionResult(MalformedBody(nameof(Parameters)));
        }

        return ToActionResult(await operation(parameters, User, cancellationToken));
    }

    private ObjectResult ToActionResult(FhirOperationResult result) =>
        StatusCode(result.StatusCode, result.Resource);

    /// <summary>
    /// Reads <paramref name="parameters"/> with an operation's generated
    /// <c>FromFhirParameters</c>. That reads each parameter with <see cref="Parameters.GetSingle"/>,
    /// which throws for a name given more than once - every operation takes exactly one of each
    /// input, and the IG processes lists as batch Bundles instead.
    /// </summary>
    private static bool TryParse<TRequest>(
        Parameters parameters,
        Func<Parameters, TRequest> fromFhirParameters,
        [NotNullWhen(true)] out TRequest? request,
        [NotNullWhen(false)] out FhirOperationResult? error
    )
        where TRequest : class
    {
        try
        {
            request = fromFhirParameters(parameters);
            error = null;
            return true;
        }
        catch (InvalidOperationException)
        {
            request = null;
            error = InvalidParameter(
                "Every parameter must be given at most once. To process several values, post a "
                    + "batch Bundle with one entry per value to the base URL."
            );
            return false;
        }
    }

    /// <summary>
    /// Requires an Identifier parameter to be present - the generated parser leaves one that is
    /// missing or of another type null - and to have a value. The <c>system</c> is optional in
    /// every one of them; the value is what names the context, or holds the original value or the
    /// pseudonym.
    /// </summary>
    private static bool TryRequire(
        Identifier? identifier,
        string name,
        [NotNullWhen(true)] out IdentifierParameter? parameter,
        [NotNullWhen(false)] out FhirOperationResult? error
    )
    {
        if (identifier?.Value is { } value && !string.IsNullOrWhiteSpace(value))
        {
            parameter = new IdentifierParameter(identifier, value);
            error = null;
            return true;
        }

        parameter = null;
        error = InvalidParameter(
            $"The '{name}' parameter is required, as an Identifier (valueIdentifier) with a "
                + "non-blank value."
        );
        return false;
    }

    private static bool TryRequireCount(
        Integer? countParameter,
        out int count,
        [NotNullWhen(false)] out FhirOperationResult? error
    )
    {
        count = 0;

        if (countParameter?.Value is not { } value)
        {
            error = InvalidParameter(
                "The 'count' parameter is required, as an integer (valueInteger)."
            );
            return false;
        }

        if (value is < 0 or > MaxPseudonymizeMultipleCount)
        {
            error = InvalidParameter(
                $"The 'count' parameter must be between 0 and {MaxPseudonymizeMultipleCount}."
            );
            return false;
        }

        count = value;
        error = null;
        return true;
    }

    /// <summary>
    /// An Identifier parameter as received - returned unchanged wherever the IG echoes it - and
    /// its value, known not to be blank.
    /// </summary>
    private sealed record IdentifierParameter(Identifier Identifier, string Value);

    /// <summary>
    /// The Identifier a pseudonym is returned as. It takes the context's system, as in the IG's
    /// examples - a pseudonym is only meaningful within its context.
    /// </summary>
    private static Identifier PseudonymIdentifier(
        IdentifierParameter context,
        string pseudonymValue
    ) => new(context.Identifier.System, pseudonymValue);

    // context and original are returned exactly as received, which the IG requires of an
    // implementation that echoes them.
    private static FhirOperationResult SinglePseudonymResult(
        IdentifierParameter context,
        IdentifierParameter original,
        string pseudonymValue
    ) =>
        FhirOperationResult.Ok(
            new SinglePseudonymResponse
            {
                Context = context.Identifier,
                Original = original.Identifier,
                Pseudonym = PseudonymIdentifier(context, pseudonymValue),
            }.ToFhirParameters()
        );

    private static FhirOperationResult SinglePseudonymContextRequired(
        string operation,
        string namespaceName,
        string alternative
    ) =>
        FhirOperationResult.Error(
            StatusCodes.Status422UnprocessableEntity,
            OperationOutcome.IssueType.BusinessRule,
            $"{operation} requires a single-pseudonym context, and '{namespaceName}' allows "
                + $"several pseudonyms per original value. Use {alternative} instead."
        );

    // Deliberately without the original value: an OperationOutcome travels into the caller's own
    // logs and error reports, and the original value is what this service keeps out of both.
    private static FhirOperationResult NoPseudonymFound(string namespaceName) =>
        FhirOperationResult.Error(
            StatusCodes.Status404NotFound,
            OperationOutcome.IssueType.NotFound,
            $"The pseudonymization context '{namespaceName}' holds no pseudonym for the original value."
        );

    private static FhirOperationResult InvalidParameter(string diagnostics) =>
        FhirOperationResult.Error(
            StatusCodes.Status400BadRequest,
            OperationOutcome.IssueType.Invalid,
            diagnostics
        );

    private static FhirOperationResult MalformedBody(string expectedResourceType) =>
        FhirOperationResult.Error(
            StatusCodes.Status400BadRequest,
            OperationOutcome.IssueType.Invalid,
            $"The request body is missing or is not a valid FHIR {expectedResourceType} resource in JSON."
        );

    private static FhirOperationResult NotImplementedYet(string operation) =>
        FhirOperationResult.Error(
            StatusCodes.Status501NotImplemented,
            OperationOutcome.IssueType.NotSupported,
            $"{operation} is not implemented yet."
        );
}
