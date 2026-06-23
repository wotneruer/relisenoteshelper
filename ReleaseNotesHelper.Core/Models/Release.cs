namespace ReleaseNotesHelper.Core.Models;

public class Release
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Version { get; set; } = "";

    public string TemplateName { get; set; } = "";

    public string ProjectName { get; set; } = "";

    public string Type { get; set; } = "Regular";

    public string PreviousReleaseId { get; set; } = "";

    public string Source { get; set; } = "";

    public string InstallerUrl { get; set; } = "";

    public string InstallerRef { get; set; } = "";

    public string InstallerPath { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? BuiltAt { get; set; }

    public DateTime? ScopeGeneratedAt { get; set; }

    public string OutputFolderName { get; set; } = "";

    public bool HasChanges { get; set; } = true;

    public int ChangedServicesCount { get; set; }

    public int IncludedServicesCount { get; set; }

    public List<ReleaseService> Services { get; set; } = [];
}
