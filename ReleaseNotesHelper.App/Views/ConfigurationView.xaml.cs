using System.Windows;
using ReleaseNotesHelper.App;
using ReleaseNotesHelper.Core.Models;
using ReleaseNotesHelper.Core.Storage;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;

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
        Loaded += ConfigurationPortableSettings_Loaded;
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


    private bool _portableConfigurationSettingsHooked;

    private void ConfigurationPortableSettings_Loaded(object sender, RoutedEventArgs e)
    {
        if (_portableConfigurationSettingsHooked)
            return;

        _portableConfigurationSettingsHooked = true;
        ApplyPortableSettingsToConfigurationUi();
        HookPortableSettingsSaveToConfigurationButton();
    }

    private void ApplyPortableSettingsToConfigurationUi()
    {
        try
        {
            var repoTextBox = FindTextBoxAfterLabel(this, "Папка для репозитор", "repositories root", "repositories folder");
            var outputTextBox = FindTextBoxAfterLabel(this, "Папка для результат", "output folder", "results folder");
            var logsTextBox = FindTextBoxAfterLabel(this, "Папка для лог", "logs folder");
            var hideTechnicalCheckBox = FindCheckBoxByText(this, "Не передавати", "Hide technical");

            var settings = PortableSettings.Current;
            var dataRoot = PortableSettings.DataRoot;

            if (repoTextBox is not null && !string.IsNullOrWhiteSpace(settings.RepositoriesRoot))
                repoTextBox.Text = settings.RepositoriesRoot;

            if (outputTextBox is not null && IsDefaultOrEmptyPath(outputTextBox.Text))
                outputTextBox.Text = Path.Combine(dataRoot, "output");

            if (logsTextBox is not null && IsDefaultOrEmptyPath(logsTextBox.Text))
                logsTextBox.Text = Path.Combine(dataRoot, "logs");

            if (hideTechnicalCheckBox is not null)
                hideTechnicalCheckBox.IsChecked = settings.HideTechnicalDataFromAi;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Failed to apply portable settings to configuration UI: " + ex.Message);
        }
    }

    private void HookPortableSettingsSaveToConfigurationButton()
    {
        var saveButton = FindButtonByText(this, "Зберегти конфігурацію", "Save configuration");
        if (saveButton is null)
            return;

        saveButton.Click -= ConfigurationPortableSettings_SaveButtonClick;
        saveButton.Click += ConfigurationPortableSettings_SaveButtonClick;
    }

    private void ConfigurationPortableSettings_SaveButtonClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var repoTextBox = FindTextBoxAfterLabel(this, "Папка для репозитор", "repositories root", "repositories folder");
            var outputTextBox = FindTextBoxAfterLabel(this, "Папка для результат", "output folder", "results folder");
            var logsTextBox = FindTextBoxAfterLabel(this, "Папка для лог", "logs folder");
            var hideTechnicalCheckBox = FindCheckBoxByText(this, "Не передавати", "Hide technical");

            var repoPath = repoTextBox?.Text?.Trim() ?? "";
            var outputPath = outputTextBox?.Text?.Trim() ?? "";
            var logsPath = logsTextBox?.Text?.Trim() ?? "";

            var dataRoot = InferPortableDataRoot(outputPath, logsPath, PortableSettings.Current.DataRoot);
            var settings = new PortableAppSettings
            {
                DataRoot = dataRoot,
                RepositoriesRoot = ToPortablePath(repoPath),
                AiProvider = PortableSettings.Current.AiProvider,
                HideTechnicalDataFromAi = hideTechnicalCheckBox?.IsChecked ?? PortableSettings.Current.HideTechnicalDataFromAi
            };

            PortableSettings.Save(settings);
            Directory.CreateDirectory(PortableSettings.DataRoot);
            Directory.CreateDirectory(Path.Combine(PortableSettings.DataRoot, "logs"));
            Directory.CreateDirectory(Path.Combine(PortableSettings.DataRoot, "output"));

            System.Diagnostics.Debug.WriteLine("Portable settings saved from Configuration tab. DataRoot='" + settings.DataRoot + "', RepositoriesRoot='" + settings.RepositoriesRoot + "'.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Failed to save portable settings from Configuration tab: " + ex.Message);
        }
    }

    private static bool IsDefaultOrEmptyPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        return value.Contains(@"\data\", StringComparison.OrdinalIgnoreCase) ||
               value.Contains(@"/data/", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferPortableDataRoot(string? outputPath, string? logsPath, string currentDataRoot)
    {
        var candidates = new List<string>();

        AddParentIfEndsWith(candidates, outputPath, "output");
        AddParentIfEndsWith(candidates, logsPath, "logs");

        var appData = PortableSettings.ResolvePath(@".\data", PortableSettings.AppDirectory);

        foreach (var candidate in candidates)
        {
            if (PathsEqual(candidate, appData))
                return @".\data";
        }

        var grouped = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => Path.GetFullPath(x), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .FirstOrDefault();

        if (grouped is not null)
            return ToPortablePath(grouped.Key);

        return string.IsNullOrWhiteSpace(currentDataRoot) ? @".\data" : currentDataRoot;
    }

    private static void AddParentIfEndsWith(List<string> candidates, string? path, string leaf)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var full = PortableSettings.ResolvePath(path, PortableSettings.AppDirectory);
        var normalizedLeaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (!string.Equals(normalizedLeaf, leaf, StringComparison.OrdinalIgnoreCase))
            return;

        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(parent))
            candidates.Add(parent);
    }

    private static string ToPortablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var full = PortableSettings.ResolvePath(path, PortableSettings.AppDirectory);
        var appDir = Path.GetFullPath(PortableSettings.AppDirectory);

        try
        {
            var relative = Path.GetRelativePath(appDir, full);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return @".\" + relative;
        }
        catch
        {
            // Keep absolute path if it cannot be made relative.
        }

        return full;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static System.Windows.Controls.TextBox? FindTextBoxAfterLabel(DependencyObject root, params string[] labelParts)
    {
        var elements = FlattenVisualTree(root).ToList();

        for (var i = 0; i < elements.Count; i++)
        {
            if (elements[i] is not TextBlock label)
                continue;

            var text = label.Text ?? "";
            if (!labelParts.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase)))
                continue;

            for (var j = i + 1; j < Math.Min(elements.Count, i + 18); j++)
            {
                if (elements[j] is System.Windows.Controls.TextBox textBox)
                    return textBox;
            }
        }

        return null;
    }

    private static System.Windows.Controls.CheckBox? FindCheckBoxByText(DependencyObject root, params string[] contentParts)
    {
        return FlattenVisualTree(root)
            .OfType<System.Windows.Controls.CheckBox>()
            .FirstOrDefault(x =>
            {
                var content = x.Content?.ToString() ?? "";
                return contentParts.Any(part => content.Contains(part, StringComparison.OrdinalIgnoreCase));
            });
    }

    private static System.Windows.Controls.Button? FindButtonByText(DependencyObject root, params string[] contentParts)
    {
        return FlattenVisualTree(root)
            .OfType<System.Windows.Controls.Button>()
            .FirstOrDefault(x =>
            {
                var content = x.Content?.ToString() ?? "";
                return contentParts.Any(part => content.Contains(part, StringComparison.OrdinalIgnoreCase));
            });
    }

    private static IEnumerable<DependencyObject> FlattenVisualTree(DependencyObject root)
    {
        if (root is null)
            yield break;

        yield return root;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            foreach (var descendant in FlattenVisualTree(child))
                yield return descendant;
        }
    }

}
