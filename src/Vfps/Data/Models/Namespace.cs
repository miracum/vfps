using System.ComponentModel.DataAnnotations;
using Vfps.Protos;

namespace Vfps.Data.Models;

public class Namespace : TracksCreationAndUpdates
{
    [Key]
    public required string Name { get; set; }
    public string? Description { get; set; }
    public PseudonymGenerationMethod PseudonymGenerationMethod { get; set; }
    public uint PseudonymLength { get; set; }
    public string? PseudonymPrefix { get; set; } = string.Empty;
    public string? PseudonymSuffix { get; set; } = string.Empty;

    /// <summary>
    /// An optional regular expression original values must match before a pseudonym is generated
    /// for them. Null/blank means no validation is performed.
    /// </summary>
    public string? OriginalValueValidationRegex { get; set; } = string.Empty;

    /// <summary>
    /// When true, a single pseudonym Create call for this namespace may store more than one
    /// distinct pseudonym for the same original value (see PseudonymAppService.CreateAsync's
    /// `count` parameter). When false (the default), behavior is unchanged: at most one
    /// pseudonym per original value.
    /// </summary>
    public bool AllowsMultiplePseudonyms { get; set; }

    public ICollection<Pseudonym> Pseudonyms { get; set; } = [];
}
