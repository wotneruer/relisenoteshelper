using System.Text.Json.Serialization;

namespace ReleaseNotesHelper.Core.Models;

public class ReleaseTemplateService
{
    public bool Included { get; set; }

    public string ServiceName { get; set; } = "";

    public string GitUrl { get; set; } = "";

    public string ValidationStatus { get; set; } = "";

    public string TargetBranch { get; set; } = "";

    // RNH_SERVICE_TARGET_REFS_REAL_GIT_2026_06_19: UI-only dropdown options; not persisted to templates.json.
    [JsonIgnore]
    public List<string> TargetRefOptions { get; set; } = [];

    public bool AskIfChanged { get; set; } = true;

    public string BaselineVersion { get; set; } = "";

    public string BaselineRef { get; set; } = "";

    public string BaselineSha { get; set; } = "";

    public string Notes { get; set; } = "";
}
