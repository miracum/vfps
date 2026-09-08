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

    /// <summary>
    /// The name of this namespace's parent, if it is a child in a pseudonymization hierarchy -
    /// i.e. its original values are pseudonym values produced by that parent namespace. Null
    /// means this namespace is a root. Set at creation and never changed afterwards, like every
    /// other field here; that also makes hierarchy cycles impossible, since a namespace can only
    /// ever point at one that already existed.
    /// </summary>
    public string? ParentName { get; set; }

    /// <summary>
    /// Whether an original value must already exist as a pseudonym value in
    /// <see cref="ParentName"/>'s namespace before a pseudonym is generated for it here.
    /// Only meaningful when <see cref="ParentName"/> is set; off by default.
    /// </summary>
    public ParentValidationMode ParentValidationMode { get; set; }

    public Namespace? Parent { get; set; }
    public ICollection<Namespace> Children { get; set; } = [];

    public ICollection<Pseudonym> Pseudonyms { get; set; } = [];
}
