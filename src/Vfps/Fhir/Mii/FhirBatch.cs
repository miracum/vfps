using Hl7.Fhir.Model;
using Microsoft.AspNetCore.WebUtilities;

namespace Vfps.Fhir.Mii;

/// <summary>
/// One FHIR operation's implementation, as <see cref="FhirBatch"/> calls it for a batch entry and
/// a controller action calls it for a direct request.
/// </summary>
internal delegate Task<FhirOperationResult> FhirOperation(
    Parameters parameters,
    CancellationToken cancellationToken
);

/// <summary>
/// Runs a FHIR batch Bundle (<c>POST [base]</c>) by handing each entry to the operation its
/// <c>request.url</c> names. The MII IG limits every operation to a single input and processes
/// lists this way, leaving it to the implementation whether a list is processed as a batch (every
/// entry independently) or a transaction (all or nothing). This one processes batches only, up
/// to <see cref="MaxConcurrentEntries"/> entries at a time.
/// </summary>
internal static class FhirBatch
{
    /// <summary>
    /// The most entries one batch may hold, which bounds how long one request can take and how
    /// much it holds in memory - a client with more values to process splits them across several
    /// batches.
    /// </summary>
    public const int MaxEntries = 10_000;

    /// <summary>
    /// How many entries of one batch run at a time. An entry's time goes almost entirely to its
    /// few sequential database round trips, so running several at once is what makes a large
    /// batch fast; bounded, because each running entry can hold a connection from the pool every
    /// other request on this replica draws from too. FHIR forbids the entries of a batch to depend
    /// on each other, which is what allows running them in any order.
    /// </summary>
    public const int MaxConcurrentEntries = 8;

    /// <param name="bundle">The posted Bundle.</param>
    /// <param name="operations">
    /// The operations an entry may call, keyed by the <c>request.url</c> that calls them
    /// (<c>$pseudonymize</c>). FHIR resolves that URL against the base the batch was posted to,
    /// which is what makes it the operation's name and nothing more.
    /// </param>
    /// <param name="logger">Where an entry's unexpected failure is logged.</param>
    /// <param name="cancellationToken">Aborts the whole batch, not just the current entry.</param>
    /// <returns>
    /// A 200 with the batch-response Bundle - whatever its entries' own statuses - or a 400 when
    /// the Bundle itself can't be processed.
    /// </returns>
    public static async Task<FhirOperationResult> ProcessAsync(
        Bundle bundle,
        IReadOnlyDictionary<string, FhirOperation> operations,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        if (bundle.Type == Bundle.BundleType.Transaction)
        {
            return FhirOperationResult.Error(
                StatusCodes.Status400BadRequest,
                OperationOutcome.IssueType.NotSupported,
                "Transaction Bundles are not supported. Send a batch Bundle instead: each of its "
                    + "entries is processed independently and answered with its own status."
            );
        }

        if (bundle.Type != Bundle.BundleType.Batch)
        {
            return FhirOperationResult.Error(
                StatusCodes.Status400BadRequest,
                OperationOutcome.IssueType.Invalid,
                "Only a Bundle of type 'batch' can be posted to the base URL."
            );
        }

        if (bundle.Entry.Count > MaxEntries)
        {
            return FhirOperationResult.Error(
                StatusCodes.Status400BadRequest,
                OperationOutcome.IssueType.TooCostly,
                $"A batch Bundle may hold at most {MaxEntries} entries, this one holds "
                    + $"{bundle.Entry.Count}. Split it into several batches."
            );
        }

        // Indexed rather than collected as they complete, so the response lists its entries in
        // the request's order however the concurrent ones finish.
        var results = new FhirOperationResult[bundle.Entry.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, bundle.Entry.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentEntries,
                CancellationToken = cancellationToken,
            },
            async (index, entryCancellationToken) =>
                results[index] = await ProcessEntryAsync(
                    bundle.Entry[index],
                    operations,
                    logger,
                    entryCancellationToken
                )
        );

        return FhirOperationResult.Ok(
            new Bundle
            {
                Type = Bundle.BundleType.BatchResponse,
                Timestamp = DateTimeOffset.UtcNow,
                // The outcome of a failed entry goes in entry.resource, where the MII IG's
                // batch-response examples put it, rather than in entry.response.outcome.
                Entry =
                [
                    .. results.Select(result => new Bundle.EntryComponent
                    {
                        Resource = result.Resource,
                        Response = new Bundle.ResponseComponent
                        {
                            Status =
                                $"{result.StatusCode} {ReasonPhrases.GetReasonPhrase(result.StatusCode)}",
                        },
                    }),
                ],
            }
        );
    }

    private static async Task<FhirOperationResult> ProcessEntryAsync(
        Bundle.EntryComponent entry,
        IReadOnlyDictionary<string, FhirOperation> operations,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        if (entry.Request is null)
        {
            return FhirOperationResult.Error(
                StatusCodes.Status400BadRequest,
                OperationOutcome.IssueType.Required,
                "Every entry of a batch Bundle needs a request naming the operation to run."
            );
        }

        if (entry.Request.Method != Bundle.HTTPVerb.POST)
        {
            return FhirOperationResult.Error(
                StatusCodes.Status405MethodNotAllowed,
                OperationOutcome.IssueType.NotSupported,
                "The operations are invoked with POST only."
            );
        }

        if (entry.Request.Url is not { } url || !operations.TryGetValue(url, out var operation))
        {
            return FhirOperationResult.Error(
                StatusCodes.Status404NotFound,
                OperationOutcome.IssueType.NotSupported,
                $"Unknown operation '{entry.Request.Url}'. The request url is the operation's "
                    + $"name, relative to the base URL: one of {string.Join(", ", operations.Keys)}."
            );
        }

        if (entry.Resource is not Parameters parameters)
        {
            return FhirOperationResult.Error(
                StatusCodes.Status400BadRequest,
                OperationOutcome.IssueType.Invalid,
                "The entry's resource must be the operation's Parameters resource."
            );
        }

        try
        {
            return await operation(parameters, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Every refusal the app services raise is already an OperationOutcome by now, so
            // this is the unexpected kind - which, in a batch, fails its own entry rather than
            // every other one with it. A cancelled request still aborts the whole batch.
            logger.LogError(ex, "A batch entry calling {Operation} failed unexpectedly", url);
            return FhirOperationResult.Error(
                StatusCodes.Status500InternalServerError,
                OperationOutcome.IssueType.Exception,
                "An unexpected error occurred while processing this entry."
            );
        }
    }
}
