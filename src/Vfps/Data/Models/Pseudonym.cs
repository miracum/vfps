namespace Vfps.Data.Models;

public class Pseudonym : TracksCreationAndUpdates
{
    public string OriginalValue { get; set; }
    public string PseudonymValue { get; set; }
    public string NamespaceName { get; set; }
}
