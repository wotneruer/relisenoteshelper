namespace ReleaseNotesHelper.Core.Models;

public class AppConfiguration
{
    public string? GitAuthType { get; set; }
    public string? GitUsername { get; set; }
    public string? TestRepositoryUrl { get; set; }
    public bool StoreCredentials { get; set; } = true;

    public string? RepositoriesPath { get; set; }
    public string? OutputPath { get; set; }
    public string? LogsPath { get; set; }

    public string? JiraBaseUrl { get; set; }
    public bool LoadJiraTitles { get; set; }

    public string? AiMode { get; set; }
    public bool HideTechnicalDataFromAi { get; set; } = true;
}