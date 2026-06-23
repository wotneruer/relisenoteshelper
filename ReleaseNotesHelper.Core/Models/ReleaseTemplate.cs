namespace ReleaseNotesHelper.Core.Models;

public class ReleaseTemplate
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string ProjectName { get; set; } = "";

    public string Description { get; set; } = "";

    public string DefaultReleaseName { get; set; } = "";

    public string DefaultTargetBranch { get; set; } = "origin/dev";

    // RNH_TEMPLATE_WORKFLOW_POLISH_2026_06_19: remember the last draft/run settings per template.
    public string LastReleaseVersion { get; set; } = "";

    public string LastReleaseName { get; set; } = "";

    public string LastComparisonBaseMode { get; set; } = "Останній installer baseline";

    public string LastDivergedHistoryDiffMode { get; set; } = "Auto: якщо diverged — merge-base..target";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<ReleaseTemplateService> Services { get; set; } = [];
}
