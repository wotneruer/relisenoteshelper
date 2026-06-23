using System.Windows;
using ReleaseNotesHelper.App;
using ReleaseNotesHelper.Core.Models;
using ReleaseNotesHelper.Core.Storage;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace ReleaseNotesHelper.App.Views;

public partial class ConfigurationView : System.Windows.Controls.UserControl
{
    private readonly AppDataStore _store = new();

    private const string GitCredentialTarget = "ReleaseNotesHelper-Git";
    private const string JiraCredentialTarget = "ReleaseNotesHelper-Jira";
    private const string AiCredentialTarget = "ReleaseNotesHelper-AI";

    public ConfigurationView()
    {
        InitializeComponent();
        Loaded += ConfigurationView_Loaded;
    }

    private async void ConfigurationView_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadConfigurationAsync();
    }


    private async Task LoadConfigurationAsync()
    {
        var config = await _store.LoadAsync<AppConfiguration>("config.json") ?? new AppConfiguration
        {
            GitAuthType = "Personal Access Token / HTTPS",
            StoreCredentials = true,
            RepositoriesPath = AppPaths.ReposPath,
            OutputPath = AppPaths.OutputPath,
            LogsPath = AppPaths.LogsPath,
            AiMode = "Тільки сформувати prompt для копіювання",
            HideTechnicalDataFromAi = true
        };

        GitUsernameTextBox.Text = config.GitUsername ?? "";
        TestRepositoryUrlTextBox.Text = config.TestRepositoryUrl ?? "";

        RepositoriesPathTextBox.Text = string.IsNullOrWhiteSpace(config.RepositoriesPath) ? AppPaths.ReposPath : config.RepositoriesPath;
        OutputPathTextBox.Text = string.IsNullOrWhiteSpace(config.OutputPath) ? AppPaths.OutputPath : config.OutputPath;
        LogsPathTextBox.Text = string.IsNullOrWhiteSpace(config.LogsPath) ? AppPaths.LogsPath : config.LogsPath;

        JiraBaseUrlTextBox.Text = config.JiraBaseUrl ?? "";

        StoreCredentialsCheckBox.IsChecked = config.StoreCredentials;
        LoadJiraTitlesCheckBox.IsChecked = config.LoadJiraTitles;
        HideTechnicalDataFromAiCheckBox.IsChecked = config.HideTechnicalDataFromAi;

        SelectComboBoxItemByContent(GitAuthTypeComboBox, config.GitAuthType);
        SelectComboBoxItemByContent(AiModeComboBox, config.AiMode);

        GitTokenPasswordBox.Password = CredentialStore.Exists(GitCredentialTarget) ? "********" : "";
        JiraTokenPasswordBox.Password = CredentialStore.Exists(JiraCredentialTarget) ? "********" : "";
        AiTokenPasswordBox.Password = CredentialStore.Exists(AiCredentialTarget) ? "********" : "";
    }
    private async void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        var config = new AppConfiguration
        {
            GitAuthType = GetSelectedComboBoxText(GitAuthTypeComboBox),
            GitUsername = GitUsernameTextBox.Text,
            TestRepositoryUrl = TestRepositoryUrlTextBox.Text,
            StoreCredentials = StoreCredentialsCheckBox.IsChecked == true,

            RepositoriesPath = RepositoriesPathTextBox.Text,
            OutputPath = OutputPathTextBox.Text,
            LogsPath = LogsPathTextBox.Text,

            JiraBaseUrl = JiraBaseUrlTextBox.Text,
            LoadJiraTitles = LoadJiraTitlesCheckBox.IsChecked == true,

            AiMode = GetSelectedComboBoxText(AiModeComboBox),
            HideTechnicalDataFromAi = HideTechnicalDataFromAiCheckBox.IsChecked == true
        };

        await _store.SaveAsync("config.json", config);

        if (StoreCredentialsCheckBox.IsChecked == true)
        {
            if (!string.IsNullOrWhiteSpace(GitTokenPasswordBox.Password) &&
                GitTokenPasswordBox.Password != "********")
            {
                CredentialStore.Save(
                    GitCredentialTarget,
                    GitUsernameTextBox.Text,
                    GitTokenPasswordBox.Password);
            }

            if (!string.IsNullOrWhiteSpace(JiraTokenPasswordBox.Password) &&
                JiraTokenPasswordBox.Password != "********")
            {
                CredentialStore.Save(
                    JiraCredentialTarget,
                    "jira",
                    JiraTokenPasswordBox.Password);
            }

            if (!string.IsNullOrWhiteSpace(AiTokenPasswordBox.Password) &&
                AiTokenPasswordBox.Password != "********")
            {
                CredentialStore.Save(
                    AiCredentialTarget,
                    "ai",
                    AiTokenPasswordBox.Password);
            }
        }
        else
        {
            CredentialStore.Delete(GitCredentialTarget);
            CredentialStore.Delete(JiraCredentialTarget);
            CredentialStore.Delete(AiCredentialTarget);

            GitTokenPasswordBox.Password = "";
            JiraTokenPasswordBox.Password = "";
            AiTokenPasswordBox.Password = "";
        }

        WpfMessageBox.Show(
            $"Конфігурацію збережено:\n{_store.GetBasePath()}",
            "Release Notes Helper",
            WpfMessageBoxButton.OK,
            WpfMessageBoxImage.Information);
    }
    private static string GetSelectedComboBoxText(WpfComboBox comboBox)
    {
        return (comboBox.SelectedItem as WpfComboBoxItem)?.Content?.ToString() ?? "";
    }
    private static void SelectComboBoxItemByContent(WpfComboBox comboBox, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        foreach (var item in comboBox.Items)
        {
            if (item is WpfComboBoxItem comboBoxItem &&
                comboBoxItem.Content?.ToString() == value)
            {
                comboBox.SelectedItem = comboBoxItem;
                return;
            }
        }
    }
    private void CheckGitAccess_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            LastGitCheckTextBlock.Text = $"Остання перевірка: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";

            if (process.ExitCode == 0)
            {
                GitStatusTextBlock.Text = $"Git: OK — {output.Trim()}";
                GitStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                GitStatusTextBlock.Text = $"Git: помилка — {error.Trim()}";
                GitStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
            }
        }
        catch (Exception ex)
        {
            GitStatusTextBlock.Text = $"Git: не знайдено — {ex.Message}";
            GitStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
            LastGitCheckTextBlock.Text = $"Остання перевірка: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }
    }
    private void CheckRepositoryAccess_Click(object sender, RoutedEventArgs e)
    {
        var repoUrl = TestRepositoryUrlTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            RepositoryStatusTextBlock.Text = "Repository: не вказано URL";
            RepositoryStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkOrange;
            LastGitCheckTextBlock.Text = $"Остання перевірка: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
            return;
        }

        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"ls-remote \"{repoUrl}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            LastGitCheckTextBlock.Text = $"Остання перевірка: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";

            if (process.ExitCode == 0)
            {
                RepositoryStatusTextBlock.Text = "Repository: OK — доступ є";
                RepositoryStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                RepositoryStatusTextBlock.Text = $"Repository: помилка — {error.Trim()}";
                RepositoryStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
            }
        }
        catch (Exception ex)
        {
            RepositoryStatusTextBlock.Text = $"Repository: помилка — {ex.Message}";
            RepositoryStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
            LastGitCheckTextBlock.Text = $"Остання перевірка: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }
    }
    private static void SelectFolderForTextBox(System.Windows.Controls.TextBox textBox)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = textBox.Text,
            Description = "Оберіть папку"
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            textBox.Text = dialog.SelectedPath;
        }
    }
    private void BrowseRepositoriesPath_Click(object sender, RoutedEventArgs e)
    {
        SelectFolderForTextBox(RepositoriesPathTextBox);
    }
    private void BrowseOutputPath_Click(object sender, RoutedEventArgs e)
    {
        SelectFolderForTextBox(OutputPathTextBox);
    }
    private void BrowseLogsPath_Click(object sender, RoutedEventArgs e)
    {
        SelectFolderForTextBox(LogsPathTextBox);
    }
}
