namespace ReleaseNotesHelper.Core.Models;

public class ReleaseService
{
    public string ServiceName { get; set; } = "";

    public string GitUrl { get; set; } = "";

    public bool Included { get; set; } = true;

    public string BaseTag { get; set; } = "";

    public string TargetBranch { get; set; } = "";

    public string PreviousSha { get; set; } = "";

    public string TargetRef { get; set; } = "";

    public string TargetSha { get; set; } = "";

    public string ImageName { get; set; } = "";

    public string SourceVersion { get; set; } = "";

    public string SourceImage { get; set; } = "";

    public string SourceFile { get; set; } = "";

    public int CommitCount { get; set; }

    public int JiraCount { get; set; }

    public int ChangedFilesCount { get; set; }

    // RNH_GIT_DIFF_QUALITY_2026_06_18: git history diagnostics for release diff quality.
    public string MergeBaseSha { get; set; } = "";

    public bool IsPreviousAncestorOfTarget { get; set; } = true;

    public bool IsTargetAncestorOfPrevious { get; set; }

    public string GitHistoryWarning { get; set; } = "";

    // RNH_GIT_DIFF_MODE_2026_06_19: actual diff/log base used after diverged-history handling.
    public string EffectiveDiffBaseSha { get; set; } = "";

    public string DiffBaseMode { get; set; } = "Strict previous..target";

    public bool UsedMergeBaseForDiff { get; set; }

    public string DiffRange { get; set; } = "";

    public List<string> JiraKeys { get; set; } = [];

    public List<string> Commits { get; set; } = [];

    public List<string> ChangedFiles { get; set; } = [];

    public string Status { get; set; } = "NotConfigured";

    public string ErrorMessage { get; set; } = "";
}
