namespace ReleaseNotesHelper.Core.Models;

public class ServiceDefinition
{
    public string Name { get; set; } = "";
    public string GitUrl { get; set; } = "";

    public string ProjectName { get; set; } = "";

    public string BaseTag { get; set; } = "";
    public string SelectedBranch { get; set; } = "";

    public bool CreatedFromInstaller { get; set; }

    public bool NeedsGitUrl { get; set; }

    public string InstallerImageName { get; set; } = "";

    public string InstallerVersion { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public string ValidationStatus { get; set; } = "";

    public string Notes { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
