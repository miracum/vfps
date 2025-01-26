namespace Vfps.Data.Models;

public class Pseudonym : TracksCreationAndUpdates
{
    public required string OriginalValue { get; set; }
    public required string PseudonymValue { get; set; }
    public required string NamespaceName { get; set; }
}
