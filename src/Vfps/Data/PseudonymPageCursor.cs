namespace Vfps.Data;

/// <summary>
/// Keyset/seek pagination cursor for listing pseudonyms in a namespace: the position of the
/// last-seen row in the (created_at DESC, original_value DESC) ordering that the
/// `(namespace_name, created_at, original_value)` index supports. Deliberately not offset-based -
/// see ListByNamespaceAsync for why.
/// </summary>
public record PseudonymPageCursor(DateTimeOffset CreatedAt, string OriginalValue);
