using System.ComponentModel.DataAnnotations;
using Vfps.Protos;

namespace Vfps.Data.Models;

public class Namespace : TracksCreationAndUpdates
{
    [Key]
    public string Name { get; set; }
    public string? Description { get; set; }
    public PseudonymGenerationMethod PseudonymGenerationMethod { get; set; }
    public uint PseudonymLength { get; set; }
    public string? PseudonymPrefix { get; set; } = string.Empty;
    public string? PseudonymSuffix { get; set; } = string.Empty;

    public ICollection<Pseudonym> Pseudonyms { get; set; }
}
