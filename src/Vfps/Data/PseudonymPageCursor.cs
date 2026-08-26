namespace Vfps.Data;

/// <summary>
/// Keyset/seek pagination cursor for listing pseudonyms in a namespace: the position of the
/// last-seen row in the (created_at DESC, original_value DESC, sequence_number DESC) ordering
/// that the `(namespace_name, created_at, original_value, sequence_number)` index supports.
/// SequenceNumber is the final tie-breaker - needed because a multi-psn namespace (see
/// Namespace.AllowsMultiplePseudonyms) can store several rows sharing the same
/// (CreatedAt, OriginalValue). Deliberately not offset-based - see ListByNamespaceAsync for why.
/// </summary>
public record PseudonymPageCursor(
    DateTimeOffset CreatedAt,
    string OriginalValue,
    long SequenceNumber
);
