using Microsoft.Extensions.Localization;
using Vfps.Data.Models;

namespace Vfps.Components;

/// <summary>
/// How a CSV job is described in the UI. Shared by the CSV Jobs page's grid and the home page's
/// recent-jobs list so the two can't drift apart - see <see cref="CsvJobStatusBadge"/> for the
/// status half.
/// </summary>
public static class CsvJobDisplay
{
    /// <summary>
    /// What identifies the job to a person: the uploaded file's name. An export has no uploaded
    /// file, so it's identified by the namespace it reads instead - the same thing its downloaded
    /// file is named after. Read defensively rather than via
    /// PseudonymizationJobAppService.ResolveSingleNamespaceMapping: this ends up in a list or grid
    /// cell, and a malformed row should render as "—" rather than throw the whole page's render
    /// away.
    /// </summary>
    public static string? Subject(PseudonymizationJob job) =>
        job.Direction == PseudonymizationJobDirection.Export
            ? job.ColumnMappings.FirstOrDefault()?.Namespace
            : job.OriginalFileName;

    public static string DirectionLabel(
        PseudonymizationJobDirection direction,
        IStringLocalizer localizer
    ) =>
        direction switch
        {
            PseudonymizationJobDirection.Depseudonymize => localizer[
                "App.CsvJobs.DirectionDepseudonymize"
            ],
            PseudonymizationJobDirection.Import => localizer["App.CsvJobs.DirectionImport"],
            PseudonymizationJobDirection.Export => localizer["App.CsvJobs.DirectionExport"],
            _ => localizer["App.CsvJobs.DirectionPseudonymize"],
        };
}
