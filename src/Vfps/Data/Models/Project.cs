using System.ComponentModel.DataAnnotations;

namespace Vfps.Data.Models;

/// <summary>
/// A named group of namespaces that belong together - e.g. the namespaces holding the patient-id
/// and encounter-id pseudonyms of one study.
///
/// Purely organizational: a project carries no behavior and no access rules of its own. Access is
/// still granted per namespace (see <see cref="NamespaceAccessGrant"/>), and which namespaces a
/// caller sees in a project is decided by those grants. It is also unrelated to the
/// parent/child hierarchy (<see cref="Namespace.ParentName"/>), which says that one namespace's
/// original values are another's pseudonyms - a project just says "these go together". A
/// namespace may belong to any number of projects, or none.
/// </summary>
public class Project : TracksCreationAndUpdates
{
    [Key]
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>The namespaces in this project.</summary>
    public ICollection<ProjectNamespace> Memberships { get; set; } = [];
}

/// <summary>
/// One namespace's membership of one project. Deleting either side deletes the membership (ON
/// DELETE CASCADE) and nothing else: removing a project never touches its namespaces or their
/// pseudonyms.
/// </summary>
public class ProjectNamespace
{
    public required string ProjectName { get; set; }
    public required string NamespaceName { get; set; }
}
