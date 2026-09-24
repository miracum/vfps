using System.Security.Claims;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.WebUtilities;
using Vfps.Authorization;
using Vfps.Data;
using Vfps.Protos;
using Vfps.PseudonymGenerators;

namespace Vfps.AppServices;

/// <inheritdoc cref="IPseudonymAppService"/>
public class PseudonymAppService(
    INamespaceRepository namespaceRepository,
    IPseudonymRepository pseudonymRepository,
    INamespacePermissionChecker permissionChecker,
    PseudonymizationMethodsLookup methodsLookup
) : IPseudonymAppService
{
    private const int DefaultPageSize = 25;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Data.Models.Pseudonym>> CreateAsync(
        string namespaceName,
        string originalValue,
        long count,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (!await permissionChecker.HasWriteAccessAsync(user, namespaceName, cancellationToken))
        {
            throw new ForbiddenException(
                $"Write access to namespace '{namespaceName}' is required."
            );
        }

        var @namespace =
            await namespaceRepository.FindAsync(namespaceName, cancellationToken)
            ?? throw new NamespaceNotFoundException(namespaceName);

        return await CreateTrustedAsync(@namespace, originalValue, count, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Data.Models.Pseudonym>> ResolveAsync(
        string namespaceName,
        string originalValue,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // Write access, not read - see the interface for why looking a mapping up is gated as
        // though it were creating one.
        if (!await permissionChecker.HasWriteAccessAsync(user, namespaceName, cancellationToken))
        {
            throw new ForbiddenException(
                $"Write access to namespace '{namespaceName}' is required."
            );
        }

        var @namespace =
            await namespaceRepository.FindAsync(namespaceName, cancellationToken)
            ?? throw new NamespaceNotFoundException(namespaceName);

        if (string.IsNullOrWhiteSpace(originalValue))
        {
            throw new ArgumentException(
                "The original value must not be blank.",
                nameof(originalValue)
            );
        }

        // Ahead of the lookup, so a value this namespace could never have stored is rejected as
        // invalid rather than reported as merely absent - the same ordering, and the same reason,
        // as the validation in CreateTrustedAsync.
        ValidateOriginalValue(@namespace, originalValue);

        return await pseudonymRepository.FindAllByOriginalValueAsync(
            namespaceName,
            originalValue,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<Data.Models.Pseudonym> CreateTrustedAsync(
        string namespaceName,
        string originalValue,
        CancellationToken cancellationToken
    )
    {
        var @namespace =
            await namespaceRepository.FindAsync(namespaceName, cancellationToken)
            ?? throw new NamespaceNotFoundException(namespaceName);

        return await CreateTrustedAsync(@namespace, originalValue, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Data.Models.Pseudonym> CreateTrustedAsync(
        Data.Models.Namespace @namespace,
        string originalValue,
        CancellationToken cancellationToken
    )
    {
        var created = await CreateTrustedAsync(@namespace, originalValue, 1, cancellationToken);
        return created[0];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Data.Models.Pseudonym>> CreateTrustedAsync(
        Data.Models.Namespace @namespace,
        string originalValue,
        long count,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(originalValue))
        {
            throw new ArgumentException(
                "The original value must not be blank.",
                nameof(originalValue)
            );
        }

        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "count must be at least 1.");
        }

        if (count > 1 && !@namespace.AllowsMultiplePseudonyms)
        {
            throw new MultiplePseudonymsNotAllowedException(@namespace.Name);
        }

        ValidateOriginalValue(@namespace, originalValue);

        // Checked before the grow-to-N short circuit below, so an invalid original value is
        // rejected the same way whether or not a pseudonym for it happens to exist already.
        await ValidateParentAsync(@namespace, [originalValue], cancellationToken);

        // Grow-to-N idempotency: a count at or below what's already stored is always a no-op -
        // existing pseudonyms are never regenerated or truncated, only ever added to.
        var existing = await pseudonymRepository.FindAllByOriginalValueAsync(
            @namespace.Name,
            originalValue,
            cancellationToken
        );
        if (existing.Count >= count)
        {
            return existing;
        }

        var knownPseudonymValues = new HashSet<string>(
            existing.Select(p => p.PseudonymValue),
            StringComparer.Ordinal
        );
        var newSequenceCandidates = new List<Data.Models.Pseudonym>((int)(count - existing.Count));

        if (PseudonymizationMethodsLookup.IsValueDependent(@namespace.PseudonymGenerationMethod))
        {
            // Exactly one pseudonym exists for a given value under a value-dependent method, so
            // there is no growing to do and nothing to retry against a collision. Namespace
            // creation refuses to pair such a method with AllowsMultiplePseudonyms, which is what
            // keeps count above 1 from reaching here; the guard stays because an existing
            // namespace's method could change meaning under a future migration, and silently
            // storing the same value twice under different sequence numbers would be worse than
            // failing.
            if (count > 1)
            {
                throw new MultiplePseudonymsNotAllowedException(@namespace.Name);
            }

            newSequenceCandidates.Add(
                new Data.Models.Pseudonym
                {
                    NamespaceName = @namespace.Name,
                    OriginalValue = originalValue,
                    PseudonymValue = await GenerateFromValueAsync(
                        @namespace,
                        originalValue,
                        cancellationToken
                    ),
                    SequenceNumber = existing.Count,
                }
            );
        }
        else
        {
            for (var sequenceNumber = existing.Count; sequenceNumber < count; sequenceNumber++)
            {
                newSequenceCandidates.Add(
                    new Data.Models.Pseudonym
                    {
                        NamespaceName = @namespace.Name,
                        OriginalValue = originalValue,
                        PseudonymValue = GenerateUniquePseudonymValue(
                            @namespace,
                            knownPseudonymValues
                        ),
                        SequenceNumber = sequenceNumber,
                    }
                );
            }
        }

        return await pseudonymRepository.CreateSetIfNotExistAsync(
            newSequenceCandidates,
            cancellationToken
        );
    }

    /// <summary>
    /// Resolves every held-back value-dependent entry in as few round trips as possible.
    /// </summary>
    /// <remarks>
    /// Deduplicated by original value rather than by (namespace, value): the generator's output
    /// depends only on the value, so the same value needed by two namespaces costs one evaluation
    /// and differs only in the prefix and suffix each namespace adds afterwards.
    /// </remarks>
    private async Task GenerateDeferredAsync(
        List<(
            (string Namespace, string OriginalValue) Key,
            Data.Models.Namespace Namespace
        )> deferred,
        Dictionary<(string Namespace, string OriginalValue), Data.Models.Pseudonym> distinctByKey,
        CancellationToken cancellationToken
    )
    {
        using var activity = Program.ActivitySource.StartActivity("GeneratePseudonymBatchDerived");

        var distinctValues = new List<string>();
        var indexByValue = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, _) in deferred)
        {
            if (indexByValue.TryAdd(key.OriginalValue, distinctValues.Count))
            {
                distinctValues.Add(key.OriginalValue);
            }
        }

        activity?.SetTag("DistinctValueCount", distinctValues.Count);

        // Every value-dependent method shares the one generator, so this is a single call
        // regardless of how many namespaces the chunk spans.
        var generator = methodsLookup.GetValueDependentGenerator(
            deferred[0].Namespace.PseudonymGenerationMethod
        );
        var generated = await generator.GeneratePseudonymsAsync(distinctValues, cancellationToken);

        if (generated.Count != distinctValues.Count)
        {
            throw new PseudonymUpsertFailedException(deferred[0].Namespace.Name);
        }

        foreach (var (key, @namespace) in deferred)
        {
            var value = generated[indexByValue[key.OriginalValue]];

            distinctByKey[key] = new Data.Models.Pseudonym
            {
                NamespaceName = @namespace.Name,
                OriginalValue = key.OriginalValue,
                PseudonymValue = @namespace.PseudonymPrefix + value + @namespace.PseudonymSuffix,
            };
        }
    }

    /// <summary>
    /// One pseudonym derived from the original value itself, by whatever service backs the
    /// namespace's method. Unlike the random generators this can fail for reasons unrelated to
    /// the value - the VOPRF server being unreachable, or answering under a key the configured
    /// public key does not verify - and those failures propagate rather than being retried here.
    /// </summary>
    private async Task<string> GenerateFromValueAsync(
        Data.Models.Namespace @namespace,
        string originalValue,
        CancellationToken cancellationToken
    )
    {
        using var activity = Program.ActivitySource.StartActivity("GeneratePseudonym");
        activity?.SetTag("Method", @namespace.PseudonymGenerationMethod.ToString());

        var generator = methodsLookup.GetValueDependentGenerator(
            @namespace.PseudonymGenerationMethod
        );
        var generated = await generator.GeneratePseudonymsAsync([originalValue], cancellationToken);

        return @namespace.PseudonymPrefix + generated[0] + @namespace.PseudonymSuffix;
    }

    // Bounded retries against an in-batch collision - astronomically unlikely for any registered
    // (all non-deterministic) generator at a realistic length, but a real possibility now that
    // several values are generated for the same original value in one call, rather than each
    // value being generated independently across the whole table.
    private const int MaxGenerationAttempts = 5;

    private string GenerateUniquePseudonymValue(
        Data.Models.Namespace @namespace,
        HashSet<string> knownPseudonymValues
    )
    {
        for (var attempt = 0; attempt < MaxGenerationAttempts; attempt++)
        {
            string pseudonymValue;
            using (var activity = Program.ActivitySource.StartActivity("GeneratePseudonym"))
            {
                activity?.SetTag("Method", @namespace.PseudonymGenerationMethod.ToString());
                pseudonymValue = methodsLookup.Generate(
                    @namespace.PseudonymGenerationMethod,
                    @namespace.PseudonymLength
                );
            }
            pseudonymValue =
                @namespace.PseudonymPrefix + pseudonymValue + @namespace.PseudonymSuffix;

            if (knownPseudonymValues.Add(pseudonymValue))
            {
                return pseudonymValue;
            }
        }

        throw new PseudonymUpsertFailedException(@namespace.Name);
    }

    /// <inheritdoc/>
    public async Task<
        IReadOnlyDictionary<(string Namespace, string OriginalValue), Data.Models.Pseudonym>
    > CreateTrustedBatchAsync(
        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)> requests,
        CancellationToken cancellationToken
    )
    {
        if (requests.Count == 0)
        {
            return new Dictionary<(string, string), Data.Models.Pseudonym>();
        }

        // Before the generation loop rather than after it: the parent-existence check has to
        // run - and reject - before any pseudonym is generated. One round trip per distinct
        // validating parent namespace rather than one per row: a CSV chunk is typically thousands
        // of rows against a handful of namespaces. Blank values are excluded so they still fail
        // with the blank-value ArgumentException below rather than being reported as missing
        // from the parent.
        foreach (var group in requests.GroupBy(r => r.Namespace.Name, StringComparer.Ordinal))
        {
            await ValidateParentAsync(
                group.First().Namespace,
                [
                    .. group
                        .Select(r => r.OriginalValue)
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.Ordinal),
                ],
                cancellationToken
            );
        }

        // Dedupe up front - a CSV chunk routinely repeats the same value (e.g. a patient ID
        // column), and there's no reason to generate a candidate pseudonym or send a duplicate
        // row over the wire more than once per chunk. The first candidate generated for a given
        // key wins; duplicates just look it up once resolved below.
        var distinctByKey =
            new Dictionary<(string Namespace, string OriginalValue), Data.Models.Pseudonym>();

        // Deferred entries are not in distinctByKey yet, so they need their own set to dedupe
        // against - a CSV chunk repeats the same value constantly.
        var deferredKeys = new HashSet<(string Namespace, string OriginalValue)>();

        // One span for the whole batch's generation, tagged with how many values it covered -
        // deliberately not one per value as the single-value path does (see
        // GenerateUniquePseudonymValue). This loop runs once per CSV chunk, so at the default
        // batch size a per-value span would be a thousand spans per chunk and millions over a
        // large job: enough to swamp the collector, and enough to make the job's own trace
        // unreadable, all to time an in-memory call that has never been the thing worth looking
        // at here. The aggregate is what that question actually needs, and the per-method
        // breakdown already exists as a continuously-run microbenchmark (Vfps.Benchmarks).
        // Value-dependent methods are held back from the loop below and resolved together
        // afterwards: their generator talks to another service, and a call per value would turn
        // one chunk into a thousand round trips.
        var deferred =
            new List<(
                (string Namespace, string OriginalValue) Key,
                Data.Models.Namespace Namespace
            )>();

        using (var activity = Program.ActivitySource.StartActivity("GeneratePseudonymBatch"))
        {
            foreach (var (@namespace, originalValue) in requests)
            {
                if (string.IsNullOrWhiteSpace(originalValue))
                {
                    throw new ArgumentException(
                        "The original value must not be blank.",
                        nameof(requests)
                    );
                }

                var key = (@namespace.Name, originalValue);
                if (distinctByKey.ContainsKey(key) || deferredKeys.Contains(key))
                {
                    continue;
                }

                ValidateOriginalValue(@namespace, originalValue);

                if (
                    PseudonymizationMethodsLookup.IsValueDependent(
                        @namespace.PseudonymGenerationMethod
                    )
                )
                {
                    deferredKeys.Add(key);
                    deferred.Add((key, @namespace));
                    continue;
                }

                var pseudonymValue = methodsLookup.Generate(
                    @namespace.PseudonymGenerationMethod,
                    @namespace.PseudonymLength
                );
                pseudonymValue =
                    @namespace.PseudonymPrefix + pseudonymValue + @namespace.PseudonymSuffix;

                // SequenceNumber left at its default (0) - this batch path always targets the first
                // pseudonym for an original value, same as CreateTrustedAsync's single-value overload.
                // For a multi-psn namespace that already has additional (sequence > 0) pseudonyms
                // stored via the dedicated count-aware create path, those are left untouched; this
                // path only ever creates/reads sequence 0.
                distinctByKey[key] = new Data.Models.Pseudonym
                {
                    NamespaceName = @namespace.Name,
                    OriginalValue = originalValue,
                    PseudonymValue = pseudonymValue,
                };
            }

            activity?.SetTag("GeneratedCount", distinctByKey.Count);
        }

        if (deferred.Count > 0)
        {
            await GenerateDeferredAsync(deferred, distinctByKey, cancellationToken);
        }

        var upserted = await pseudonymRepository.CreateIfNotExistBatchAsync(
            [.. distinctByKey.Values],
            cancellationToken
        );

        var result = new Dictionary<(string, string), Data.Models.Pseudonym>();
        foreach (var pseudonym in upserted)
        {
            result[(pseudonym.NamespaceName, pseudonym.OriginalValue)] = pseudonym;
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PseudonymImportResult>> ImportTrustedBatchAsync(
        Data.Models.Namespace @namespace,
        IReadOnlyList<PseudonymImportEntry> entries,
        CancellationToken cancellationToken
    )
    {
        if (entries.Count == 0)
        {
            return [];
        }

        // Blank values are the caller's bug rather than a data problem to report per row - the
        // import job runner drops blank/placeholder cells before ever getting here (see
        // CsvPseudonymizationJobRunner.IsMissingValue), so reaching this means something else
        // built the batch wrong. Same reasoning as CreateTrustedBatchAsync's own blank check.
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.OriginalValue))
            {
                throw new ArgumentException(
                    "The original value must not be blank.",
                    nameof(entries)
                );
            }

            if (string.IsNullOrWhiteSpace(entry.PseudonymValue))
            {
                throw new ArgumentException(
                    "The pseudonym value must not be blank.",
                    nameof(entries)
                );
            }
        }

        var distinctOriginalValues = entries
            .Select(e => e.OriginalValue)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Everything already stored for the original values this batch touches, so each row can
        // be classified against it without a round trip of its own. Also seeds the next free
        // sequence number per original value for a multi-psn namespace, exactly the way
        // CreateTrustedAsync derives it from existing.Count.
        var storedPseudonymValuesByOriginal = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal
        );
        var nextSequenceByOriginal = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (
            var stored in await pseudonymRepository.FindAllByOriginalValuesAsync(
                @namespace.Name,
                distinctOriginalValues,
                cancellationToken
            )
        )
        {
            if (!storedPseudonymValuesByOriginal.TryGetValue(stored.OriginalValue, out var values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                storedPseudonymValuesByOriginal[stored.OriginalValue] = values;
            }

            values.Add(stored.PseudonymValue);
            nextSequenceByOriginal[stored.OriginalValue] =
                Math.Max(
                    nextSequenceByOriginal.GetValueOrDefault(stored.OriginalValue),
                    stored.SequenceNumber
                ) + 1;
        }

        // Which of the pseudonym values being imported are already in use in this namespace.
        // Deliberately only asks *whether* they exist rather than what they map to: knowing which
        // original value holds one is a reverse lookup, and this path is gated on write access.
        var takenPseudonymValues = await pseudonymRepository.FilterExistingPseudonymValuesAsync(
            @namespace.Name,
            [.. entries.Select(e => e.PseudonymValue).Distinct(StringComparer.Ordinal)],
            cancellationToken
        );

        var missingFromParent = await FindValuesMissingFromParentAsync(
            @namespace,
            distinctOriginalValues,
            cancellationToken
        );

        var outcomes = new PseudonymImportOutcome?[entries.Count];
        var candidates = new List<Data.Models.Pseudonym>();
        var candidateEntryIndexes = new List<int>();
        var claimedPseudonymValues = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < entries.Count; i++)
        {
            var (originalValue, pseudonymValue) = entries[i];

            if (missingFromParent.Contains(originalValue))
            {
                outcomes[i] = PseudonymImportOutcome.ParentValueMissing;
                continue;
            }

            if (!MatchesOriginalValueValidation(@namespace, originalValue))
            {
                outcomes[i] = PseudonymImportOutcome.InvalidOriginalValue;
                continue;
            }

            if (!storedPseudonymValuesByOriginal.TryGetValue(originalValue, out var knownForValue))
            {
                knownForValue = new HashSet<string>(StringComparer.Ordinal);
                storedPseudonymValuesByOriginal[originalValue] = knownForValue;
            }

            // Checked before anything else below, so re-running the same import file - or a file
            // repeating the same pair - is a clean no-op rather than a pile of conflicts.
            if (knownForValue.Contains(pseudonymValue))
            {
                outcomes[i] = PseudonymImportOutcome.AlreadyPresent;
                continue;
            }

            var nextSequence = nextSequenceByOriginal.GetValueOrDefault(originalValue);
            if (nextSequence > 0 && !@namespace.AllowsMultiplePseudonyms)
            {
                outcomes[i] = PseudonymImportOutcome.OriginalValueConflict;
                continue;
            }

            // Both halves of the same rule: the pseudonym is already in the namespace, or an
            // earlier row in this very batch just claimed it for a different original value.
            // Either way storing it would leave two original values behind one pseudonym, making
            // that pseudonym's reverse lookup ambiguous.
            if (
                takenPseudonymValues.Contains(pseudonymValue)
                || !claimedPseudonymValues.Add(pseudonymValue)
            )
            {
                outcomes[i] = PseudonymImportOutcome.PseudonymValueConflict;
                continue;
            }

            knownForValue.Add(pseudonymValue);
            nextSequenceByOriginal[originalValue] = nextSequence + 1;
            candidateEntryIndexes.Add(i);
            candidates.Add(
                new Data.Models.Pseudonym
                {
                    NamespaceName = @namespace.Name,
                    OriginalValue = originalValue,
                    PseudonymValue = pseudonymValue,
                    SequenceNumber = nextSequence,
                }
            );
        }

        if (candidates.Count > 0)
        {
            var upserted = await pseudonymRepository.CreateIfNotExistBatchAsync(
                candidates,
                cancellationToken
            );
            var storedByKey = upserted.ToDictionary(
                p => (p.OriginalValue, p.SequenceNumber),
                p => p.PseudonymValue
            );

            for (var c = 0; c < candidates.Count; c++)
            {
                var candidate = candidates[c];

                // The upsert never overwrites, so a concurrent writer that got to this exact key
                // first leaves it returning *their* value instead of ours - report the collision
                // rather than claiming an import that didn't happen.
                outcomes[candidateEntryIndexes[c]] =
                    storedByKey.TryGetValue(
                        (candidate.OriginalValue, candidate.SequenceNumber),
                        out var storedValue
                    )
                    && string.Equals(
                        storedValue,
                        candidate.PseudonymValue,
                        StringComparison.Ordinal
                    )
                        ? PseudonymImportOutcome.Imported
                        : PseudonymImportOutcome.OriginalValueConflict;
            }
        }

        return
        [
            .. entries.Select(
                (entry, i) =>
                    new PseudonymImportResult(
                        entry,
                        outcomes[i]
                            // Unreachable: every branch of the loop above assigns an outcome, and
                            // every index it skipped is covered by candidateEntryIndexes. Asserted
                            // rather than defaulted, so a future edit that misses a path shows up
                            // as a failed job instead of a silent "Imported" for a row that never
                            // was.
                            ?? throw new InvalidOperationException(
                                $"Import entry {i} was left unclassified."
                            )
                    )
            ),
        ];
    }

    /// <inheritdoc/>
    public async Task<PseudonymPageDto> ListAsync(
        string namespaceName,
        int pageSize,
        string? pageToken,
        bool includeTotalSize,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var _ =
            await namespaceRepository.FindAsync(namespaceName, cancellationToken)
            ?? throw new NamespaceNotFoundException(namespaceName);
        if (!await permissionChecker.HasReadAccessAsync(user, namespaceName, cancellationToken))
        {
            throw new ForbiddenException(
                $"Read access to namespace '{namespaceName}' is required."
            );
        }

        var effectivePageSize = pageSize <= 0 ? DefaultPageSize : pageSize;
        var cursor = DecodeCursor(pageToken);

        var pseudonyms = await pseudonymRepository.ListByNamespaceAsync(
            namespaceName,
            cursor,
            effectivePageSize,
            cancellationToken
        );

        // Same "did we get a full page" heuristic as before this rewrite: if fewer than a full
        // page came back there's no next page. Imprecise only when the total count is an exact
        // multiple of the page size (one extra empty-result round trip) - an existing, accepted
        // trade-off, not a new one.
        string? nextPageToken = null;
        if (pseudonyms.Count == effectivePageSize)
        {
            var last = pseudonyms[^1];
            nextPageToken = EncodeCursor(
                new PseudonymPageCursor(last.CreatedAt, last.OriginalValue, last.SequenceNumber)
            );
        }

        long? totalSize = includeTotalSize
            ? await pseudonymRepository.CountByNamespaceAsync(namespaceName, cancellationToken)
            : null;

        var items = pseudonyms
            .Select(p => new PseudonymSummaryDto(
                p.NamespaceName,
                p.PseudonymValue,
                p.CreatedAt,
                p.LastUpdatedAt
            ))
            .ToList();

        return new PseudonymPageDto(items, nextPageToken, totalSize);
    }

    /// <inheritdoc/>
    public async Task<PseudonymSearchPageDto> SearchAsync(
        string namespaceName,
        string? searchText,
        int skip,
        int take,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var _ =
            await namespaceRepository.FindAsync(namespaceName, cancellationToken)
            ?? throw new NamespaceNotFoundException(namespaceName);
        // Both checks come from one resolve: read gates the search at all, reverse-lookup
        // decides whether the original values are included in what comes back.
        var permissions = await permissionChecker.ResolveAsync(user, cancellationToken);
        if (!permissions.HasReadAccess(namespaceName))
        {
            throw new ForbiddenException(
                $"Read access to namespace '{namespaceName}' is required."
            );
        }

        var canRevealOriginalValues = permissions.HasReverseLookupAccess(namespaceName);
        var effectiveTake = take <= 0 ? DefaultPageSize : take;

        var (pseudonyms, totalCount) = await pseudonymRepository.SearchByNamespaceAsync(
            namespaceName,
            searchText,
            canRevealOriginalValues,
            Math.Max(skip, 0),
            effectiveTake,
            cancellationToken
        );

        var items = pseudonyms
            .Select(p => new PseudonymSearchItemDto(
                p.NamespaceName,
                p.PseudonymValue,
                canRevealOriginalValues ? p.OriginalValue : null,
                p.CreatedAt,
                p.LastUpdatedAt
            ))
            .ToList();

        return new PseudonymSearchPageDto(items, totalCount);
    }

    /// <inheritdoc/>
    public async Task<Data.Models.Pseudonym?> ReverseLookupAsync(
        string namespaceName,
        string pseudonymValue,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (
            !await permissionChecker.HasReverseLookupAccessAsync(
                user,
                namespaceName,
                cancellationToken
            )
        )
        {
            throw new ForbiddenException(
                $"Reverse-lookup access to namespace '{namespaceName}' is required."
            );
        }

        return await pseudonymRepository.FindByPseudonymValueAsync(
            namespaceName,
            pseudonymValue,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<Data.Models.Pseudonym?> ReverseLookupTrustedAsync(
        string namespaceName,
        string pseudonymValue,
        CancellationToken cancellationToken
    )
    {
        return await pseudonymRepository.FindByPseudonymValueAsync(
            namespaceName,
            pseudonymValue,
            cancellationToken
        );
    }

    /// <inheritdoc/>
    public async Task<
        IReadOnlyDictionary<(string Namespace, string PseudonymValue), Data.Models.Pseudonym>
    > ReverseLookupTrustedBatchAsync(
        IReadOnlyList<(Data.Models.Namespace Namespace, string PseudonymValue)> requests,
        CancellationToken cancellationToken
    )
    {
        var resolved =
            new Dictionary<(string Namespace, string PseudonymValue), Data.Models.Pseudonym>();

        if (requests.Count == 0)
        {
            return resolved;
        }

        // One round trip per distinct namespace rather than per value: the lookup is
        // namespace-scoped (it rides the (namespace_name, pseudonym_value) index), so a chunk
        // touching a single namespace - the overwhelmingly common case - costs exactly one.
        // Distinct values only, so a value repeated across the chunk is not asked for twice.
        foreach (var group in requests.GroupBy(r => r.Namespace.Name, StringComparer.Ordinal))
        {
            var values = group
                .Select(r => r.PseudonymValue)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (
                var pseudonym in await pseudonymRepository.FindAllByPseudonymValuesAsync(
                    group.Key,
                    values,
                    cancellationToken
                )
            )
            {
                // A namespace cannot hold the same pseudonym value twice (the import path rejects
                // that as a PseudonymValueConflict, and generation checks for it), so the first
                // row for a key is the only one - indexer assignment rather than Add, purely so a
                // stored duplicate predating those checks can't throw mid-job.
                resolved[(group.Key, pseudonym.PseudonymValue)] = pseudonym;
            }
        }

        return resolved;
    }

    /// <inheritdoc/>
    public async Task<
        IReadOnlyDictionary<(string Namespace, string OriginalValue), Data.Models.Pseudonym>
    > ResolveTrustedBatchAsync(
        IReadOnlyList<(Data.Models.Namespace Namespace, string OriginalValue)> requests,
        CancellationToken cancellationToken
    )
    {
        var resolved =
            new Dictionary<(string Namespace, string OriginalValue), Data.Models.Pseudonym>();

        if (requests.Count == 0)
        {
            return resolved;
        }

        // Structurally identical to ReverseLookupTrustedBatchAsync above, down to the grouping and
        // the absent-means-not-found contract - only the index it rides and the column it matches
        // on differ.
        foreach (var group in requests.GroupBy(r => r.Namespace.Name, StringComparer.Ordinal))
        {
            var values = group
                .Select(r => r.OriginalValue)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (
                var pseudonym in (
                    await pseudonymRepository.FindAllByOriginalValuesAsync(
                        group.Key,
                        values,
                        cancellationToken
                    )
                ).Where(pseudonym => pseudonym.SequenceNumber == 0)
            )
            {
                // Sequence 0 only. A multi-psn namespace returns every stored sequence number for
                // an original value, and the one a pseudonymizing job substitutes into a cell is
                // the first - matching the single-value CreateTrustedAsync overload the create
                // path uses. TryAdd rather than the indexer keeps that first-wins regardless of
                // the ordering the repository returned.
                resolved.TryAdd((group.Key, pseudonym.OriginalValue), pseudonym);
            }
        }

        return resolved;
    }

    private static void ValidateOriginalValue(
        Data.Models.Namespace @namespace,
        string originalValue
    )
    {
        if (!MatchesOriginalValueValidation(@namespace, originalValue))
        {
            throw new OriginalValueValidationException(
                @namespace.Name,
                @namespace.OriginalValueValidationRegex!
            );
        }
    }

    /// <summary>
    /// The same check as <see cref="ValidateOriginalValue"/> without the exception - the import
    /// path reports a rejected value as one row's outcome rather than failing the whole batch, so
    /// it can't use exceptions for what is an expected, per-row result there.
    /// </summary>
    private static bool MatchesOriginalValueValidation(
        Data.Models.Namespace @namespace,
        string originalValue
    )
    {
        var pattern = @namespace.OriginalValueValidationRegex;

        return string.IsNullOrEmpty(pattern)
            || OriginalValueValidation.IsMatch(pattern, originalValue);
    }

    /// <summary>
    /// Enforces a child namespace's <see cref="Data.Models.Namespace.ParentValidationMode"/>: every
    /// original value must already exist as a pseudonym value in the parent namespace. A no-op for
    /// a root namespace, or one that doesn't opt in - so the extra round trip is only paid by the
    /// namespaces that asked for it.
    /// </summary>
    private async Task ValidateParentAsync(
        Data.Models.Namespace @namespace,
        IReadOnlyCollection<string> originalValues,
        CancellationToken cancellationToken
    )
    {
        var missing = await FindValuesMissingFromParentAsync(
            @namespace,
            originalValues,
            cancellationToken
        );

        if (missing.Count > 0)
        {
            throw new ParentPseudonymNotFoundException(@namespace.Name, @namespace.ParentName!);
        }
    }

    /// <summary>
    /// The <see cref="ValidateParentAsync"/> check without the exception: which of
    /// <paramref name="originalValues"/> are not present as pseudonym values in the parent
    /// namespace, empty when the namespace doesn't opt in. Split out for
    /// <see cref="ImportTrustedBatchAsync"/>, which reports a missing parent value as one row's
    /// outcome instead of failing every row in the batch alongside it.
    /// </summary>
    private async Task<IReadOnlySet<string>> FindValuesMissingFromParentAsync(
        Data.Models.Namespace @namespace,
        IReadOnlyCollection<string> originalValues,
        CancellationToken cancellationToken
    )
    {
        if (
            @namespace.ParentValidationMode != ParentValidationMode.EnsureExists
            || string.IsNullOrEmpty(@namespace.ParentName)
            || originalValues.Count == 0
        )
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var existing = await pseudonymRepository.FilterExistingPseudonymValuesAsync(
            @namespace.ParentName,
            originalValues,
            cancellationToken
        );

        // Membership rather than a count comparison, so a caller passing the same value twice
        // can't be mistaken for a missing one.
        return originalValues
            .Where(value => !existing.Contains(value))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static PseudonymPageCursor? DecodeCursor(string? pageToken)
    {
        if (string.IsNullOrEmpty(pageToken))
        {
            return null;
        }

        var token = new PseudonymListPaginationToken();
        token.MergeFrom(WebEncoders.Base64UrlDecode(pageToken));

        return new PseudonymPageCursor(
            token.CreatedAt.ToDateTimeOffset(),
            token.OriginalValue,
            token.SequenceNumber
        );
    }

    private static string EncodeCursor(PseudonymPageCursor cursor)
    {
        var token = new PseudonymListPaginationToken
        {
            CreatedAt = Timestamp.FromDateTimeOffset(cursor.CreatedAt),
            OriginalValue = cursor.OriginalValue,
            SequenceNumber = cursor.SequenceNumber,
        };

        return WebEncoders.Base64UrlEncode(token.ToByteArray());
    }
}
