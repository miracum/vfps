namespace Vfps.Data.Models;

public class Pseudonym : TracksCreationAndUpdates
{
    public required string OriginalValue { get; set; }
    public required string PseudonymValue { get; set; }
    public required string NamespaceName { get; set; }

    /// <summary>
    /// Distinguishes multiple pseudonyms stored for the same (NamespaceName, OriginalValue) in a
    /// multi-psn namespace (see <see cref="Namespace.AllowsMultiplePseudonyms"/>), assigned in
    /// creation order starting at 0. Always 0 for a namespace that never allows more than one
    /// pseudonym per original value - part of the primary key so that case is unaffected.
    /// </summary>
    public long SequenceNumber { get; set; }

    /// <summary>
    /// The client-supplied request id of the Append call that stored this pseudonym, if it carried
    /// one (see <see cref="AppServices.IPseudonymAppService.AppendAsync"/> and AIP-155). Unique per
    /// namespace, which is what makes a retried Append return this row instead of storing another.
    /// Null for every pseudonym stored any other way. Written in the same INSERT as the pseudonym
    /// itself, so there is no window in which one exists without the other.
    /// </summary>
    public Guid? AppendRequestId { get; set; }
}
