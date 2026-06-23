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
using System;

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
    private System.Windows.Controls.TextBox? _portableDataRootTextBox;
    private System.Windows.Controls.TextBox? _portableRepositoriesRootTextBox;
    private System.Windows.Controls.TextBlock? _portableSettingsFileTextBlock;
    private System.Windows.Controls.TextBlock? _portableSettingsStatusTextBlock;

    private void ConfigurationPortableSettings_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_portableConfigurationSettingsHooked)
            return;

        _portableConfigurationSettingsHooked = true;

        EnsurePortableSettingsSectionInConfigurationUi();
        LoadPortableSettingsSection();
        HookExistingConfigurationSaveButton();
    }

    private void EnsurePortableSettingsSectionInConfigurationUi()
    {
        if (FindElementByTag(this, "PortableSettingsSection") is not null)
            return;

        var section = BuildPortableSettingsSection();
        InsertPortableSettingsSection(section);
    }

    private System.Windows.FrameworkElement BuildPortableSettingsSection()
    {
        var border = new System.Windows.Controls.Border
        {
            Tag = "PortableSettingsSection",
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
            Padding = new System.Windows.Thickness(14),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(8),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(72, 86, 105)),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(49, 60, 73))
        };

        var panel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical
        };

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Portable settings",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 244, 250)),
            FontWeight = System.Windows.FontWeights.SemiBold,
            FontSize = 16,
            Margin = new System.Windows.Thickness(0, 0, 0, 6)
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Основні налаштування застосунку. Вони зберігаються у settings.json біля exe, але редагуються тут, без ручного відкриття JSON. AI privacy option використовується з існуючого чекбокса в секції AI генерації.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 220, 232)),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Opacity = 0.95,
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        });

        panel.Children.Add(CreatePathRow(
            "Data root",
            "Папка для logs, results, templates, releases, prompts та ai-rules. Для portable mode рекомендовано .\\data.",
            out _portableDataRootTextBox,
            BrowsePortableDataRootButton_Click));

        panel.Children.Add(CreatePathRow(
            "Repositories root",
            "Обовʼязкова папка з локальними Git repositories сервісів. Scan використовує саме цей шлях.",
            out _portableRepositoriesRootTextBox,
            BrowsePortableRepositoriesRootButton_Click));
        _portableSettingsFileTextBlock = new System.Windows.Controls.TextBlock
        {
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 220, 232)),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Opacity = 0.92,
            Margin = new System.Windows.Thickness(0, 0, 0, 10)
        };
        panel.Children.Add(_portableSettingsFileTextBlock);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new System.Windows.Thickness(0, 2, 0, 0)
        };

        var saveButton = new System.Windows.Controls.Button
        {
            Content = "Save portable settings",
            Width = 160,
            Margin = new System.Windows.Thickness(0, 0, 8, 0)
        };
        saveButton.Click += PortableSettingsSaveButton_Click;
        buttons.Children.Add(saveButton);

        var reloadButton = new System.Windows.Controls.Button
        {
            Content = "Reload",
            Width = 90,
            Margin = new System.Windows.Thickness(0, 0, 8, 0)
        };
        reloadButton.Click += PortableSettingsReloadButton_Click;
        buttons.Children.Add(reloadButton);

        var openDataButton = new System.Windows.Controls.Button
        {
            Content = "Open data folder",
            Width = 130,
            Margin = new System.Windows.Thickness(0, 0, 8, 0)
        };
        openDataButton.Click += PortableSettingsOpenDataRootButton_Click;
        buttons.Children.Add(openDataButton);

        var openFileButton = new System.Windows.Controls.Button
        {
            Content = "Open settings file",
            Width = 140
        };
        openFileButton.Click += PortableSettingsOpenFileButton_Click;
        buttons.Children.Add(openFileButton);

        panel.Children.Add(buttons);

        _portableSettingsStatusTextBlock = new System.Windows.Controls.TextBlock
        {
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 220, 232)),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Opacity = 0.95,
            Margin = new System.Windows.Thickness(0, 10, 0, 0)
        };
        panel.Children.Add(_portableSettingsStatusTextBlock);

        border.Child = panel;
        return border;
    }

    private static System.Windows.FrameworkElement CreatePathRow(
        string title,
        string description,
        out System.Windows.Controls.TextBox textBox,
        System.Windows.RoutedEventHandler browseHandler)
    {
        var wrapper = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Margin = new System.Windows.Thickness(0, 0, 0, 10)
        };

        wrapper.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = title,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(238, 244, 250)),
            FontWeight = System.Windows.FontWeights.SemiBold,
            Margin = new System.Windows.Thickness(0, 0, 0, 2)
        });

        wrapper.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = description,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 220, 232)),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Opacity = 0.92,
            Margin = new System.Windows.Thickness(0, 0, 0, 5)
        });

        var dock = new System.Windows.Controls.DockPanel();

        var browseButton = new System.Windows.Controls.Button
        {
            Content = "Browse",
            Width = 85,
            Margin = new System.Windows.Thickness(8, 0, 0, 0)
        };
        browseButton.Click += browseHandler;
        System.Windows.Controls.DockPanel.SetDock(browseButton, System.Windows.Controls.Dock.Right);
        dock.Children.Add(browseButton);

        textBox = new System.Windows.Controls.TextBox
        {
            MinWidth = 420,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        dock.Children.Add(textBox);

        wrapper.Children.Add(dock);
        return wrapper;
    }

    private void InsertPortableSettingsSection(System.Windows.FrameworkElement section)
    {
        if (Content is System.Windows.Controls.ScrollViewer scrollViewer &&
            scrollViewer.Content is System.Windows.Controls.Panel scrollPanel)
        {
            scrollPanel.Children.Insert(0, section);
            return;
        }

        if (Content is System.Windows.Controls.Panel panel)
        {
            panel.Children.Insert(0, section);
            return;
        }

        if (Content is System.Windows.UIElement oldContent)
        {
            Content = null;

            var wrapper = new System.Windows.Controls.StackPanel();
            wrapper.Children.Add(section);
            wrapper.Children.Add(oldContent);

            Content = wrapper;
            return;
        }

        Content = section;
    }

    private void LoadPortableSettingsSection()
    {
        var settings = PortableSettings.Current;

        if (_portableDataRootTextBox is not null)
            _portableDataRootTextBox.Text = string.IsNullOrWhiteSpace(settings.DataRoot) ? @".\data" : settings.DataRoot;

        if (_portableRepositoriesRootTextBox is not null)
            _portableRepositoriesRootTextBox.Text = settings.RepositoriesRoot ?? "";
        var existingHideTechnicalDataCheckBox = FindExistingHideTechnicalDataCheckBox();
        if (existingHideTechnicalDataCheckBox is not null)
            existingHideTechnicalDataCheckBox.IsChecked = settings.HideTechnicalDataFromAi;
if (_portableSettingsFileTextBlock is not null)
            _portableSettingsFileTextBlock.Text = "Settings file: " + PortableSettings.SettingsFilePath;

        SetPortableSettingsStatus("Loaded portable settings.");
    }

    private void HookExistingConfigurationSaveButton()
    {
        var saveButton = FindButtonByText(this, "Зберегти конфігурацію", "Save configuration");
        if (saveButton is null)
            return;

        saveButton.Click -= ExistingConfigurationSaveButton_PortableSettingsClick;
        saveButton.Click += ExistingConfigurationSaveButton_PortableSettingsClick;
    }

    private void ExistingConfigurationSaveButton_PortableSettingsClick(object sender, System.Windows.RoutedEventArgs e)
    {
        SavePortableSettingsFromSection(showSuccessMessage: false);
    }

    private void PortableSettingsSaveButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SavePortableSettingsFromSection(showSuccessMessage: true);
    }

    private void PortableSettingsReloadButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        PortableSettings.Reload();
        LoadPortableSettingsSection();
    }

    private void BrowsePortableDataRootButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        BrowseFolderIntoTextBox(_portableDataRootTextBox);
    }

    private void BrowsePortableRepositoriesRootButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        BrowseFolderIntoTextBox(_portableRepositoriesRootTextBox);
    }

    private static void BrowseFolderIntoTextBox(System.Windows.Controls.TextBox? textBox)
    {
        if (textBox is null)
            return;

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder",
            UseDescriptionForTitle = true
        };

        if (!string.IsNullOrWhiteSpace(textBox.Text))
        {
            var current = PortableSettings.ResolvePath(textBox.Text, PortableSettings.AppDirectory);
            if (Directory.Exists(current))
                dialog.SelectedPath = current;
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            textBox.Text = dialog.SelectedPath;
    }

    private void PortableSettingsOpenDataRootButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PortableSettings.DataRoot);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = PortableSettings.DataRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetPortableSettingsStatus("Failed to open data folder: " + ex.Message);
        }
    }

    private void PortableSettingsOpenFileButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(PortableSettings.SettingsFilePath))
                PortableSettings.Save();

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = PortableSettings.SettingsFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetPortableSettingsStatus("Failed to open settings file: " + ex.Message);
        }
    }

    private bool SavePortableSettingsFromSection(bool showSuccessMessage)
    {
        try
        {
            var dataRoot = _portableDataRootTextBox?.Text?.Trim() ?? "";
            var repositoriesRoot = _portableRepositoriesRootTextBox?.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(dataRoot))
            {
                SetPortableSettingsStatus("Data root is required. Recommended value: .\\data");
                return false;
            }

            if (string.IsNullOrWhiteSpace(repositoriesRoot))
            {
                SetPortableSettingsStatus("Repositories root is required for scan.");
                return false;
            }

            var resolvedRepositoriesRoot = PortableSettings.ResolvePath(repositoriesRoot, PortableSettings.AppDirectory);
            if (!Directory.Exists(resolvedRepositoriesRoot))
            {
                SetPortableSettingsStatus("Repositories root does not exist: " + resolvedRepositoriesRoot);
                return false;
            }

            var settings = new PortableAppSettings
            {
                DataRoot = ToPortablePath(dataRoot),
                RepositoriesRoot = ToPortablePath(repositoriesRoot),
                AiProvider = PortableSettings.Current.AiProvider,
                HideTechnicalDataFromAi = FindExistingHideTechnicalDataCheckBox()?.IsChecked ?? PortableSettings.Current.HideTechnicalDataFromAi
            };

            PortableSettings.Save(settings);
            Directory.CreateDirectory(PortableSettings.DataRoot);

            if (_portableDataRootTextBox is not null)
                _portableDataRootTextBox.Text = settings.DataRoot;

            if (_portableRepositoriesRootTextBox is not null)
                _portableRepositoriesRootTextBox.Text = settings.RepositoriesRoot;

            if (_portableSettingsFileTextBlock is not null)
                _portableSettingsFileTextBlock.Text = "Settings file: " + PortableSettings.SettingsFilePath;

            SetPortableSettingsStatus(showSuccessMessage ? "Portable settings saved." : "Portable settings saved with configuration.");
            return true;
        }
        catch (Exception ex)
        {
            SetPortableSettingsStatus("Failed to save portable settings: " + ex.Message);
            return false;
        }
    }

    private void SetPortableSettingsStatus(string message)
    {
        if (_portableSettingsStatusTextBlock is not null)
            _portableSettingsStatusTextBlock.Text = message;

        System.Diagnostics.Debug.WriteLine(message);
    }

    private static string ToPortablePath(string path)
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

    private static System.Windows.FrameworkElement? FindElementByTag(System.Windows.DependencyObject root, string tag)
    {
        return FlattenVisualTree(root)
            .OfType<System.Windows.FrameworkElement>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), tag, StringComparison.Ordinal));
    }


    private System.Windows.Controls.CheckBox? FindExistingHideTechnicalDataCheckBox()
    {
        return FlattenVisualTree(this)
            .OfType<System.Windows.Controls.CheckBox>()
            .FirstOrDefault(x =>
            {
                var content = x.Content?.ToString() ?? "";
                return content.Contains("Не передавати", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("technical", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("hash", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static System.Windows.Controls.Button? FindButtonByText(System.Windows.DependencyObject root, params string[] contentParts)
    {
        return FlattenVisualTree(root)
            .OfType<System.Windows.Controls.Button>()
            .FirstOrDefault(x =>
            {
                var content = x.Content?.ToString() ?? "";
                return contentParts.Any(part => content.Contains(part, StringComparison.OrdinalIgnoreCase));
            });
    }

    private static IEnumerable<System.Windows.DependencyObject> FlattenVisualTree(System.Windows.DependencyObject root)
    {
        if (root is null)
            yield break;

        yield return root;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            foreach (var descendant in FlattenVisualTree(child))
                yield return descendant;
        }
    }

}
