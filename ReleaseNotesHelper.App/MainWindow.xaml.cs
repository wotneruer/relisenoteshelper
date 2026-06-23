using System.IO;
using System.Windows;
using ReleaseNotesHelper.App.Services;
using ReleaseNotesHelper.Core.Models;
using ReleaseNotesHelper.Core.Storage;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using WpfWindow = System.Windows.Window;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

namespace ReleaseNotesHelper.App;

public partial class MainWindow : WpfWindow
{
    private readonly AppDataStore _store = new();
    private List<Release> _releases = [];
    private Release? _currentRelease;

    // RNH_FLOW_OUTPUT_LOGS_2026_06_18: lightweight UI flow state for Output and Logs tabs.
    private readonly StringBuilder _logBuilder = new();
    // RNH_AUTO_SESSION_LOG_2026_06_18: full session log is written to disk; UI shows only the latest lines.
    private readonly Queue<string> _visibleLogLines = new();
    private string? _sessionLogFilePath;
    private const int VisibleLogLineLimit = 500;
    private string? _currentOutputFilePath;
    private string? _currentOutputFolderPath;

    private static readonly string[] KnownOutputFiles =
    [
        "client-release-notes.md",
        "scope-summary.md",
        "scope-commits.md",
        "scope-changed-files.md",
        "service-artifacts-index.md",
        "combined-ai-prompt.md",
        "service-release-notes-index.md",
        "ai-errors.txt"
    ];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;

        // RNH_LIVE_FLOW_LOGGING_2026_06_18: AppLog subscription for messages emitted by nested views.
        AppLog.MessageLogged += AppLog_MessageLogged;
        Closed += MainWindow_Closed;
    }

    private void AppLog_MessageLogged(object? sender, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppLog_MessageLogged(sender, message));
            return;
        }

        AppendLog(message);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        AppLog.MessageLogged -= AppLog_MessageLogged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeSessionLogAsync();
        AppendLog(_sessionLogFilePath == null
            ? "Application loaded. Full session log file was not initialized."
            : "Application loaded. Session log: " + _sessionLogFilePath);
        await LoadReleasesAsync();
        await RefreshOutputTreeAsync();
    }

    private async Task LoadReleasesAsync()
    {
        _releases = await _store.LoadAsync<List<Release>>("releases.json") ?? [];

        var currentRelease = _releases.LastOrDefault();

        if (currentRelease == null)
        {
            UpdateReleaseUi(null);
            return;
        }

        _currentRelease = currentRelease;

        ReleaseProjectTextBox.Text = string.IsNullOrWhiteSpace(currentRelease.ProjectName)
            ? InferProjectName(currentRelease.TemplateName, currentRelease.Name)
            : currentRelease.ProjectName;

        ReleaseNameTextBox.Text = currentRelease.Name;
        ReleaseTemplateTextBox.Text = string.IsNullOrWhiteSpace(currentRelease.TemplateName)
            ? "RS.Core"
            : currentRelease.TemplateName;

        if (!string.IsNullOrWhiteSpace(currentRelease.InstallerUrl))
            InstallerUrlTextBox.Text = currentRelease.InstallerUrl;

        UpdateReleaseUi(currentRelease);
    }

    private async void ImportInstallerBaseline_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppendLog($"Installer baseline import started. Release='{ReleaseNameTextBox.Text.Trim()}', Installer='{InstallerUrlTextBox.Text.Trim()}'.");
            SetReleaseBusy(true, "Імпортую installer baseline...");

            var config = await _store.LoadAsync<AppConfiguration>("config.json");
            var services = await _store.LoadAsync<List<ServiceDefinition>>("services.json") ?? [];

            var repositoriesRoot = !string.IsNullOrWhiteSpace(config?.RepositoriesPath)
                ? config.RepositoriesPath
                : AppPaths.ReposPath;

            var importer = new InstallerBaselineImporter(repositoriesRoot);

            var release = await importer.ImportAsync(
                InstallerUrlTextBox.Text.Trim(),
                ReleaseNameTextBox.Text.Trim(),
                ReleaseTemplateTextBox.Text.Trim(),
                services);

            release.ProjectName = GetReleaseProjectName();

            _currentRelease = release;
            UpdateReleaseUi(release);
            AppendLog($"Installer baseline import completed. Services={release.Services.Count}, resolved={release.Services.Count(x => !string.IsNullOrWhiteSpace(x.TargetSha))}.");

            SetReleaseBusy(false, "Baseline імпортовано. Перевір таблицю і натисни «Зберегти реліз».");

            WpfMessageBox.Show(
                "Baseline імпортовано з installer yaml.\n\nЧервоні рядки потрібно додати або дозаповнити в довіднику сервісів.",
                "Release Notes Helper",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog("Installer baseline import failed: " + ex.Message);
            SetReleaseBusy(false, $"Помилка імпорту baseline — {ex.Message}");

            WpfMessageBox.Show(
                ex.Message,
                "Installer baseline import error",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Error);
        }
    }

    private async void SaveRelease_Click(object sender, RoutedEventArgs e)
    {
        var release = _currentRelease ?? new Release
        {
            Id = BuildReleaseId(ReleaseTemplateTextBox.Text, ReleaseNameTextBox.Text),
            Name = ReleaseNameTextBox.Text.Trim(),
            TemplateName = ReleaseTemplateTextBox.Text.Trim(),
            ProjectName = GetReleaseProjectName(),
            Type = "Draft",
            CreatedAt = DateTime.Now,
            Services = []
        };

        release.Name = ReleaseNameTextBox.Text.Trim();
        release.TemplateName = string.IsNullOrWhiteSpace(ReleaseTemplateTextBox.Text)
            ? "RS.Core"
            : ReleaseTemplateTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(release.Id))
            release.Id = BuildReleaseId(release.TemplateName, release.Name);

        var existingIndex = _releases.FindIndex(x =>
            string.Equals(x.Id, release.Id, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(x.Name, release.Name, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(x.TemplateName, release.TemplateName, StringComparison.OrdinalIgnoreCase)));

        if (existingIndex >= 0)
            _releases[existingIndex] = release;
        else
            _releases.Add(release);

        await _store.SaveAsync("releases.json", _releases);
        AppendLog($"Release saved: {release.Name}. Project={release.ProjectName}. Services={release.Services.Count}.");

        _currentRelease = release;
        UpdateReleaseUi(release);

        WpfMessageBox.Show(
            $"Реліз збережено:\n{Path.Combine(_store.GetBasePath(), "releases.json")}",
            "Release Notes Helper",
            WpfMessageBoxButton.OK,
            WpfMessageBoxImage.Information);
    }

    private async void CreateMissingServiceStubs_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRelease == null)
        {
            WpfMessageBox.Show(
                "Спочатку імпортуй baseline з інсталятора.",
                "Release Notes Helper",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Warning);
            return;
        }

        var missing = _currentRelease.Services
            .Where(x =>
                string.Equals(x.Status, "Missing in services.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Status, "Missing Git URL", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count == 0)
        {
            WpfMessageBox.Show(
                "Відсутніх сервісів немає.",
                "Release Notes Helper",
                WpfMessageBoxButton.OK,
                WpfMessageBoxImage.Information);
            return;
        }

        var services = await _store.LoadAsync<List<ServiceDefinition>>("services.json") ?? [];
        var added = 0;

        foreach (var item in missing)
        {
            var exists = services.Any(x =>
                string.Equals(NormalizeKey(x.Name), NormalizeKey(item.ServiceName), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeKey(x.Name), NormalizeKey(item.ImageName), StringComparison.OrdinalIgnoreCase));

            if (exists)
                continue;

            services.Add(new ServiceDefinition
            {
                Name = item.ServiceName,
                GitUrl = "",
                BaseTag = item.TargetRef,
                SelectedBranch = "origin/dev",
                CreatedFromInstaller = true,
                NeedsGitUrl = true,
                InstallerImageName = item.ImageName,
                InstallerVersion = item.SourceVersion,
                CreatedAt = DateTime.Now
            });

            added++;
        }

        await _store.SaveAsync("services.json", services);
        AppendLog($"Created missing service stubs: {added}.");

        WpfMessageBox.Show(
            $"Створено заготовок сервісів: {added}\n\nВідкрий «Довідник сервісів» і заповни Git URL для червоних позицій.",
            "Release Notes Helper",
            WpfMessageBoxButton.OK,
            WpfMessageBoxImage.Information);
    }

    private void UpdateReleaseUi(Release? release)
    {
        ReleasesDataGrid.ItemsSource = null;
        ReleasesDataGrid.ItemsSource = release?.Services;

        var servicesCount = release?.Services.Count ?? 0;
        var resolvedCount = release?.Services.Count(x => !string.IsNullOrWhiteSpace(x.TargetSha)) ?? 0;
        var missingCount = release?.Services.Count(x =>
            string.Equals(x.Status, "Missing in services.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Status, "Missing Git URL", StringComparison.OrdinalIgnoreCase)) ?? 0;
        var errorCount = release?.Services.Count(x =>
            string.Equals(x.Status, "No Git tag", StringComparison.OrdinalIgnoreCase) ||
            x.Status.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? 0;

        ReleaseServicesCountTextBlock.Text = servicesCount.ToString();
        ReleaseResolvedCountTextBlock.Text = resolvedCount.ToString();
        ReleaseMissingCountTextBlock.Text = missingCount.ToString();
        ReleaseErrorCountTextBlock.Text = errorCount.ToString();

        if (release == null)
        {
            ReleaseStatusTextBlock.Text = "Очікування дії";
            return;
        }

        ReleaseStatusTextBlock.Text =
            $"Project: {release.ProjectName} | Release: {release.Name} | Type: {release.Type} | Source: {release.Source} | Ref: {release.InstallerRef}";
    }

    private void SetReleaseBusy(bool isBusy, string status)
    {
        ImportInstallerBaselineButton.IsEnabled = !isBusy;
        ReleaseStatusTextBlock.Text = status;
        ReleaseStatusTextBlock.Foreground = isBusy
            ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.Green;
    }


    private string GetReleaseProjectName()
    {
        var value = ReleaseProjectTextBox?.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        return InferProjectName(ReleaseTemplateTextBox?.Text, ReleaseNameTextBox?.Text);
    }

    private static string InferProjectName(string? templateName, string? releaseName)
    {
        var source = string.Join(" ", new[] { templateName, releaseName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

        if (source.Contains("Poruch", StringComparison.OrdinalIgnoreCase) || source.Contains("Поруч", StringComparison.OrdinalIgnoreCase))
            return "RS.Core Poruch";

        if (source.StartsWith("RS.Core", StringComparison.OrdinalIgnoreCase))
            return "RS.Core";

        return string.IsNullOrWhiteSpace(templateName) ? "Default" : templateName.Trim();
    }

    private static string BuildReleaseId(string templateName, string releaseName)
    {
        var source = $"{templateName}_{releaseName}";
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            source = source.Replace(invalidChar, '_');
        }

        return source.Replace(" ", "_");
    }

    private static string NormalizeKey(string value)
    {
        return value.Trim().ToLowerInvariant().Replace("_", "-");
    }

    // RNH_OUTPUT_NAV_TREE_2026_06_18: tree based navigation for release outputs, service stacks and repository logs.
    private sealed class OutputNodeTag
    {
        public string Kind { get; init; } = "";
        public string Path { get; init; } = "";
        public string? ServiceName { get; init; }
        public string? ReleaseId { get; init; }
    }

    private bool _isBuildingOutputTree;
    private string? _selectedOutputPath;

    private async void RefreshOutputTree_Click(object sender, RoutedEventArgs e)
    {
        await RefreshOutputTreeAsync();
    }

    private async void OutputTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_isBuildingOutputTree)
            return;

        if (e.NewValue is not System.Windows.Controls.TreeViewItem item || item.Tag is not OutputNodeTag tag)
            return;

        await PreviewOutputNodeAsync(tag);
    }

    private async Task RefreshOutputTreeAsync()
    {
        try
        {
            _isBuildingOutputTree = true;
            OutputTreeView.Items.Clear();

            var releasesRoot = await ResolveReleasesRootAsync();
            Directory.CreateDirectory(releasesRoot);
            _currentOutputFolderPath = releasesRoot;

            var root = CreateOutputTreeItem("Output", new OutputNodeTag { Kind = "folder", Path = releasesRoot }, true);
            OutputTreeView.Items.Add(root);

            var releasesNode = CreateOutputTreeItem("Релізи / розгортайки", new OutputNodeTag { Kind = "folder", Path = releasesRoot }, true);
            root.Items.Add(releasesNode);
            AddReleaseFoldersToTree(releasesNode, releasesRoot);

            var reposRoot = await ResolveRepositoriesRootAsync();
            var reposNode = CreateOutputTreeItem("Git repositories / logs", new OutputNodeTag { Kind = "folder", Path = reposRoot }, false);
            root.Items.Add(reposNode);
            AddRepositoryLogsToTree(reposNode, reposRoot);

            var logsRoot = await ResolveLogsRootAsync();
            var logsNode = CreateOutputTreeItem("Application logs", new OutputNodeTag { Kind = "folder", Path = logsRoot }, false);
            root.Items.Add(logsNode);
            AddTextFilesToTree(logsNode, logsRoot, recursive: true, maxFiles: 120);

            root.IsExpanded = true;
            releasesNode.IsExpanded = true;

            OutputCurrentFileTextBlock.Text = releasesRoot;
            OutputPreviewTextBox.Text = BuildOutputTreeOverview(releasesRoot, reposRoot, logsRoot);
            AppendLog("Output navigation tree refreshed.");
        }
        catch (Exception ex)
        {
            OutputPreviewTextBox.Text = "Не вдалося оновити output tree:" + Environment.NewLine + ex.Message;
            AppendLog("Output tree refresh failed: " + ex.Message);
        }
        finally
        {
            _isBuildingOutputTree = false;
        }
    }

    private void AddReleaseFoldersToTree(System.Windows.Controls.TreeViewItem releasesNode, string releasesRoot)
    {
        if (!Directory.Exists(releasesRoot))
        {
            releasesNode.Items.Add(CreateInfoTreeItem("Папку ще не створено."));
            return;
        }

        var releaseDirs = Directory.EnumerateDirectories(releasesRoot)
            .OrderByDescending(Directory.GetLastWriteTime)
            .Take(80)
            .ToList();

        if (releaseDirs.Count == 0)
        {
            releasesNode.Items.Add(CreateInfoTreeItem("Релізних папок поки немає."));
            return;
        }

        foreach (var releaseDir in releaseDirs)
            releasesNode.Items.Add(BuildReleaseFolderNode(releaseDir));
    }

    private System.Windows.Controls.TreeViewItem BuildReleaseFolderNode(string releaseDir)
    {
        var release = FindReleaseByOutputFolder(releaseDir);
        var releaseName = release?.Name ?? Path.GetFileName(releaseDir);
        var releaseNode = CreateOutputTreeItem(
            releaseName,
            new OutputNodeTag { Kind = "folder", Path = releaseDir, ReleaseId = release?.Id ?? releaseName },
            string.Equals(_currentOutputFolderPath, releaseDir, StringComparison.OrdinalIgnoreCase));

        var topFilesNode = CreateOutputTreeItem("Файли релізу", new OutputNodeTag { Kind = "folder", Path = releaseDir, ReleaseId = release?.Id }, true);
        var addedTopFile = false;

        foreach (var fileName in KnownOutputFiles)
        {
            var path = Path.Combine(releaseDir, fileName);
            if (File.Exists(path))
            {
                topFilesNode.Items.Add(CreateFileTreeItem(fileName, path, release?.Id));
                addedTopFile = true;
            }
        }

        var extraTopFiles = Directory.EnumerateFiles(releaseDir, "*", SearchOption.TopDirectoryOnly)
            .Where(IsPreviewableFile)
            .Where(path => !KnownOutputFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path))
            .Take(80)
            .ToList();

        foreach (var path in extraTopFiles)
        {
            topFilesNode.Items.Add(CreateFileTreeItem(Path.GetFileName(path), path, release?.Id));
            addedTopFile = true;
        }

        if (!addedTopFile)
            topFilesNode.Items.Add(CreateInfoTreeItem("Файлів верхнього рівня не знайдено."));

        releaseNode.Items.Add(topFilesNode);

        var servicesNode = CreateOutputTreeItem("Сервіси / окремі стеки змін", new OutputNodeTag { Kind = "folder", Path = releaseDir, ReleaseId = release?.Id }, true);
        AddServicesToReleaseNode(servicesNode, releaseDir, release);
        releaseNode.Items.Add(servicesNode);

        var promptsDir = Path.Combine(releaseDir, "prompts");
        var promptsNode = CreateOutputTreeItem("Prompts", new OutputNodeTag { Kind = "folder", Path = promptsDir, ReleaseId = release?.Id }, false);
        AddTextFilesToTree(promptsNode, promptsDir, recursive: false, maxFiles: 200, releaseId: release?.Id);
        releaseNode.Items.Add(promptsNode);

        var servicesDir = Path.Combine(releaseDir, "services");
        var serviceNotesNode = CreateOutputTreeItem("Service notes files", new OutputNodeTag { Kind = "folder", Path = servicesDir, ReleaseId = release?.Id }, false);
        AddTextFilesToTree(serviceNotesNode, servicesDir, recursive: false, maxFiles: 200, releaseId: release?.Id);
        releaseNode.Items.Add(serviceNotesNode);

        return releaseNode;
    }

    private void AddServicesToReleaseNode(System.Windows.Controls.TreeViewItem servicesNode, string releaseDir, Release? release)
    {
        var serviceNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (release != null)
        {
            foreach (var service in release.Services)
            {
                if (!string.IsNullOrWhiteSpace(service.ServiceName))
                    serviceNames.Add(service.ServiceName);
            }
        }

        var servicesDir = Path.Combine(releaseDir, "services");
        if (Directory.Exists(servicesDir))
        {
            foreach (var file in Directory.EnumerateFiles(servicesDir, "*.md", SearchOption.TopDirectoryOnly))
                serviceNames.Add(Path.GetFileNameWithoutExtension(file));
        }

        var promptsDir = Path.Combine(releaseDir, "prompts");
        if (Directory.Exists(promptsDir))
        {
            foreach (var file in Directory.EnumerateFiles(promptsDir, "*.prompt.md", SearchOption.TopDirectoryOnly))
                serviceNames.Add(Path.GetFileName(file).Replace(".prompt.md", "", StringComparison.OrdinalIgnoreCase));
        }

        // RNH_STABILIZE_ARTIFACTS_TREE_DISCOVER: include physical service artifact folders in the service tree.
        var artifactsRoot = Path.Combine(releaseDir, "service-artifacts");
        if (Directory.Exists(artifactsRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(artifactsRoot))
                serviceNames.Add(Path.GetFileName(dir));
        }

        if (serviceNames.Count == 0)
        {
            servicesNode.Items.Add(CreateInfoTreeItem("Сервісних output-файлів поки немає."));
            return;
        }

        foreach (var serviceName in serviceNames)
        {
            var serviceNode = CreateOutputTreeItem(
                serviceName,
                new OutputNodeTag { Kind = "service", Path = releaseDir, ServiceName = serviceName, ReleaseId = release?.Id },
                false);

            serviceNode.Items.Add(CreateOutputTreeItem(
                "Стек змін сервісу",
                new OutputNodeTag { Kind = "service-stack", Path = releaseDir, ServiceName = serviceName, ReleaseId = release?.Id },
                false));

            var serviceNotesPath = Path.Combine(servicesDir, SanitizeOutputFileName(serviceName) + ".md");
            var promptPath = Path.Combine(promptsDir, SanitizeOutputFileName(serviceName) + ".prompt.md");

            var actualServiceNotes = FindFileByServiceName(servicesDir, serviceName, ".md");
            var actualPrompt = FindFileByServiceName(promptsDir, serviceName, ".prompt.md");

            if (actualServiceNotes != null || File.Exists(serviceNotesPath))
                serviceNode.Items.Add(CreateFileTreeItem("service notes", actualServiceNotes ?? serviceNotesPath, release?.Id));

            if (actualPrompt != null || File.Exists(promptPath))
                serviceNode.Items.Add(CreateFileTreeItem("AI prompt", actualPrompt ?? promptPath, release?.Id));

            // RNH_STABILIZE_ARTIFACTS_TREE_NODE: show per-service physical artifacts produced during scan/generation.
            var artifactsDir = FindServiceArtifactFolder(artifactsRoot, serviceName);
            if (artifactsDir != null)
            {
                var artifactsNode = CreateOutputTreeItem(
                    "Artifacts",
                    new OutputNodeTag { Kind = "folder", Path = artifactsDir, ServiceName = serviceName, ReleaseId = release?.Id },
                    false);
                AddTextFilesToTree(artifactsNode, artifactsDir, recursive: false, maxFiles: 40, releaseId: release?.Id);
                serviceNode.Items.Add(artifactsNode);
            }

            servicesNode.Items.Add(serviceNode);
        }
    }

    private void AddRepositoryLogsToTree(System.Windows.Controls.TreeViewItem reposNode, string reposRoot)
    {
        if (!Directory.Exists(reposRoot))
        {
            reposNode.Items.Add(CreateInfoTreeItem("Папку репозиторіїв не знайдено: " + reposRoot));
            return;
        }

        var repoDirs = Directory.EnumerateDirectories(reposRoot)
            .OrderBy(path => Path.GetFileName(path))
            .Take(150)
            .ToList();

        if (repoDirs.Count == 0)
        {
            reposNode.Items.Add(CreateInfoTreeItem("Репозиторіїв поки немає."));
            return;
        }

        foreach (var repoDir in repoDirs)
        {
            var gitDir = Path.Combine(repoDir, ".git");
            if (!Directory.Exists(gitDir))
                continue;

            var repoNode = CreateOutputTreeItem(Path.GetFileName(repoDir), new OutputNodeTag { Kind = "folder", Path = repoDir }, false);

            AddExistingFile(repoNode, Path.Combine(gitDir, "FETCH_HEAD"), "FETCH_HEAD");
            AddExistingFile(repoNode, Path.Combine(gitDir, "ORIG_HEAD"), "ORIG_HEAD");
            AddExistingFile(repoNode, Path.Combine(gitDir, "logs", "HEAD"), "logs/HEAD");

            var refLogsDir = Path.Combine(gitDir, "logs", "refs");
            if (Directory.Exists(refLogsDir))
            {
                var refsNode = CreateOutputTreeItem("logs/refs", new OutputNodeTag { Kind = "folder", Path = refLogsDir }, false);
                AddTextFilesToTree(refsNode, refLogsDir, recursive: true, maxFiles: 100);
                repoNode.Items.Add(refsNode);
            }

            if (repoNode.Items.Count == 0)
                repoNode.Items.Add(CreateInfoTreeItem("Git logs не знайдено."));

            reposNode.Items.Add(repoNode);
        }

        if (reposNode.Items.Count == 0)
            reposNode.Items.Add(CreateInfoTreeItem("Git-репозиторіїв з .git не знайдено."));
    }

    private void AddTextFilesToTree(System.Windows.Controls.TreeViewItem parent, string folder, bool recursive, int maxFiles, string? releaseId = null)
    {
        if (!Directory.Exists(folder))
        {
            parent.Items.Add(CreateInfoTreeItem("Папку не знайдено: " + folder));
            return;
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(folder, "*", option)
            .Where(IsPreviewableFile)
            .OrderBy(path => path)
            .Take(maxFiles)
            .ToList();

        if (files.Count == 0)
        {
            parent.Items.Add(CreateInfoTreeItem("Previewable файлів не знайдено."));
            return;
        }

        foreach (var file in files)
        {
            var name = recursive
                ? Path.GetRelativePath(folder, file)
                : Path.GetFileName(file);
            parent.Items.Add(CreateFileTreeItem(name, file, releaseId));
        }
    }

    private static void AddExistingFile(System.Windows.Controls.TreeViewItem parent, string path, string header)
    {
        if (File.Exists(path))
            parent.Items.Add(CreateFileTreeItem(header, path));
    }

    private async Task PreviewOutputNodeAsync(OutputNodeTag tag)
    {
        try
        {
            switch (tag.Kind)
            {
                case "file":
                    await LoadOutputFilePathAsync(tag.Path);
                    break;
                case "folder":
                    await PreviewFolderAsync(tag.Path);
                    break;
                case "service":
                    await PreviewServiceOverviewAsync(tag);
                    break;
                case "service-stack":
                    await PreviewServiceStackAsync(tag);
                    break;
            }
        }
        catch (Exception ex)
        {
            OutputPreviewTextBox.Text = "Не вдалося відкрити вузол:" + Environment.NewLine + ex.Message;
            AppendLog("Output node preview failed: " + ex.Message);
        }
    }

    private async Task LoadOutputFilePathAsync(string path)
    {
        _selectedOutputPath = path;
        _currentOutputFilePath = path;
        _currentOutputFolderPath = Path.GetDirectoryName(path);
        OutputCurrentFileTextBlock.Text = path;

        if (!File.Exists(path))
        {
            OutputPreviewTextBox.Text = "Файл не знайдено:" + Environment.NewLine + path;
            AppendLog("Output file not found: " + path);
            return;
        }

        if (!IsPreviewableFile(path))
        {
            var info = new FileInfo(path);
            OutputPreviewTextBox.Text = $"Файл існує, але не схожий на текстовий preview.{Environment.NewLine}{Environment.NewLine}{path}{Environment.NewLine}Size: {info.Length:n0} bytes";
            AppendLog("Output non-text file selected: " + path);
            return;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > 2 * 1024 * 1024)
        {
            OutputPreviewTextBox.Text = $"Файл завеликий для preview: {fileInfo.Length:n0} bytes{Environment.NewLine}{Environment.NewLine}{path}";
            AppendLog("Output file is too large for preview: " + path);
            return;
        }

        OutputPreviewTextBox.Text = (await File.ReadAllTextAsync(path, Encoding.UTF8)).TrimStart('\ufeff', '\r', '\n');
        AppendLog("Output file opened: " + path);
    }

    private Task PreviewFolderAsync(string folder)
    {
        _selectedOutputPath = folder;
        _currentOutputFolderPath = folder;
        _currentOutputFilePath = null;
        OutputCurrentFileTextBlock.Text = folder;

        var builder = new StringBuilder();
        builder.AppendLine("# Folder");
        builder.AppendLine();
        builder.AppendLine(folder);
        builder.AppendLine();

        if (!Directory.Exists(folder))
        {
            builder.AppendLine("Папку не знайдено.");
            OutputPreviewTextBox.Text = builder.ToString();
            return Task.CompletedTask;
        }

        var directories = Directory.EnumerateDirectories(folder)
            .OrderBy(Path.GetFileName)
            .Take(80)
            .ToList();
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName)
            .Take(120)
            .ToList();

        builder.AppendLine($"Directories: {directories.Count}");
        builder.AppendLine($"Files: {files.Count}");
        builder.AppendLine($"Modified: {Directory.GetLastWriteTime(folder):yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();

        if (directories.Count > 0)
        {
            builder.AppendLine("## Directories");
            foreach (var dir in directories)
                builder.AppendLine("- " + Path.GetFileName(dir));
            builder.AppendLine();
        }

        if (files.Count > 0)
        {
            builder.AppendLine("## Files");
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                builder.AppendLine($"- {Path.GetFileName(file)} — {info.Length:n0} bytes");
            }
        }

        OutputPreviewTextBox.Text = builder.ToString();
        AppendLog("Output folder previewed: " + folder);
        return Task.CompletedTask;
    }

    private Task PreviewServiceOverviewAsync(OutputNodeTag tag)
    {
        _selectedOutputPath = tag.Path;
        _currentOutputFolderPath = tag.Path;
        _currentOutputFilePath = null;
        OutputCurrentFileTextBlock.Text = tag.ServiceName ?? tag.Path;

        var release = FindReleaseForTag(tag);
        var service = FindServiceForTag(release, tag.ServiceName);
        var serviceName = tag.ServiceName ?? service?.ServiceName ?? "service";
        var servicesDir = Path.Combine(tag.Path, "services");
        var promptsDir = Path.Combine(tag.Path, "prompts");
        var notes = FindFileByServiceName(servicesDir, serviceName, ".md");
        var prompt = FindFileByServiceName(promptsDir, serviceName, ".prompt.md");

        var builder = new StringBuilder();
        builder.AppendLine($"# {serviceName}");
        builder.AppendLine();
        builder.AppendLine($"Release: {release?.Name ?? Path.GetFileName(tag.Path)}");
        builder.AppendLine($"Status: {service?.Status ?? "unknown"}");
        builder.AppendLine($"Included: {(service?.Included == true ? "yes" : "no")}");
        builder.AppendLine($"Commits: {service?.CommitCount ?? 0}");
        builder.AppendLine($"Previous: {service?.PreviousSha ?? ""}");
        builder.AppendLine($"Target: {service?.TargetRef ?? service?.TargetSha ?? ""}");
        builder.AppendLine();
        builder.AppendLine("## Files");
        builder.AppendLine("- Stack: вибери дочірній вузол `Стек змін сервісу`");
        builder.AppendLine("- Notes: " + (notes ?? "not found"));
        builder.AppendLine("- Prompt: " + (prompt ?? "not found"));

        OutputPreviewTextBox.Text = builder.ToString();
        AppendLog("Service output overview opened: " + serviceName);
        return Task.CompletedTask;
    }

    private Task PreviewServiceStackAsync(OutputNodeTag tag)
    {
        _selectedOutputPath = tag.Path;
        _currentOutputFolderPath = tag.Path;
        _currentOutputFilePath = null;
        OutputCurrentFileTextBlock.Text = (tag.ServiceName ?? "service") + " / stack";

        var release = FindReleaseForTag(tag);
        var service = FindServiceForTag(release, tag.ServiceName);
        var builder = new StringBuilder();

        if (service == null)
        {
            builder.AppendLine("Стек змін сервісу не знайдено в releases.json.");
            builder.AppendLine();
            builder.AppendLine("Можна відкрити service notes або prompt у дочірніх вузлах, якщо вони є на диску.");
            OutputPreviewTextBox.Text = builder.ToString();
            return Task.CompletedTask;
        }

        builder.AppendLine($"# Stack змін: {service.ServiceName}");
        builder.AppendLine();
        builder.AppendLine($"Release: {release?.Name ?? ""}");
        builder.AppendLine($"Included: {(service.Included ? "yes" : "no")}");
        builder.AppendLine($"Status: {service.Status}");
        builder.AppendLine($"Commits: {service.CommitCount}");
        builder.AppendLine($"Previous: {service.PreviousSha}");
        builder.AppendLine($"Target: {service.TargetRef} / {service.TargetSha}");
        builder.AppendLine();
        builder.AppendLine("## Commits");
        builder.AppendLine();

        if (service.Commits.Count == 0)
        {
            builder.AppendLine("Коміти не знайдені або сервіс не мав змін у цьому scope.");
        }
        else
        {
            foreach (var commit in service.Commits)
                builder.AppendLine("- " + commit);
        }

        OutputPreviewTextBox.Text = builder.ToString();
        AppendLog("Service stack opened: " + service.ServiceName);
        return Task.CompletedTask;
    }

    private async void OpenSelectedOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _selectedOutputPath;

            if (string.IsNullOrWhiteSpace(path))
                path = _currentOutputFilePath ?? _currentOutputFolderPath ?? await ResolveCurrentOutputFolderAsync();

            if (string.IsNullOrWhiteSpace(path))
                return;

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select," + QuoteExplorerPath(path),
                    UseShellExecute = true
                });
            }
            else
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = QuoteExplorerPath(path),
                    UseShellExecute = true
                });
            }

            AppendLog("Output path opened in Explorer: " + path);
        }
        catch (Exception ex)
        {
            AppendLog("Output selected path open failed: " + ex.Message);
            WpfMessageBox.Show(ex.Message, "Open output path error", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
    }

    private async Task<string> ResolveCurrentOutputFolderAsync()
    {
        var releasesRoot = await ResolveReleasesRootAsync();
        Directory.CreateDirectory(releasesRoot);

        var candidates = new List<string>();

        if (_currentRelease != null && !string.IsNullOrWhiteSpace(_currentRelease.Name))
            candidates.AddRange(BuildReleaseFolderNameCandidates(_currentRelease.Name).Select(name => Path.Combine(releasesRoot, name)));

        if (!string.IsNullOrWhiteSpace(ReleaseNameTextBox.Text))
            candidates.AddRange(BuildReleaseFolderNameCandidates(ReleaseNameTextBox.Text.Trim()).Select(name => Path.Combine(releasesRoot, name)));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        var latestFolder = Directory.EnumerateDirectories(releasesRoot)
            .OrderByDescending(Directory.GetLastWriteTime)
            .FirstOrDefault();

        return latestFolder ?? releasesRoot;
    }

    private async Task<string> ResolveReleasesRootAsync()
    {
        var config = await _store.LoadAsync<AppConfiguration>("config.json");

        if (!string.IsNullOrWhiteSpace(config?.OutputPath))
            return ToReleasesRoot(config.OutputPath.Trim());

        if (Directory.Exists(AppPaths.ReleasesPath))
            return AppPaths.ReleasesPath;

        return ToReleasesRoot(AppPaths.OutputPath);
    }

    private async Task<string> ResolveRepositoriesRootAsync()
    {
        var config = await _store.LoadAsync<AppConfiguration>("config.json");

        if (!string.IsNullOrWhiteSpace(config?.RepositoriesPath))
            return Environment.ExpandEnvironmentVariables(config.RepositoriesPath.Trim());

        return AppPaths.ReposPath;
    }

    private async Task<string> ResolveLogsRootAsync()
    {
        var config = await _store.LoadAsync<AppConfiguration>("config.json");

        if (!string.IsNullOrWhiteSpace(config?.LogsPath))
            return Environment.ExpandEnvironmentVariables(config.LogsPath.Trim());

        return AppPaths.LogsPath;
    }

    private static string ToReleasesRoot(string outputPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(outputPath);
        var trimmed = expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var lastPart = Path.GetFileName(trimmed);

        return string.Equals(lastPart, "releases", StringComparison.OrdinalIgnoreCase)
            ? expanded
            : Path.Combine(expanded, "releases");
    }

    private Release? FindReleaseByOutputFolder(string releaseDir)
    {
        var folderName = Path.GetFileName(releaseDir);

        return _releases.FirstOrDefault(release =>
            BuildReleaseFolderNameCandidates(release.Name).Any(name => string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(release.Id, folderName, StringComparison.OrdinalIgnoreCase));
    }

    private Release? FindReleaseForTag(OutputNodeTag tag)
    {
        if (!string.IsNullOrWhiteSpace(tag.ReleaseId))
        {
            var byId = _releases.FirstOrDefault(release => string.Equals(release.Id, tag.ReleaseId, StringComparison.OrdinalIgnoreCase));
            if (byId != null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(tag.Path))
            return FindReleaseByOutputFolder(tag.Path);

        return _currentRelease;
    }

    private static ReleaseService? FindServiceForTag(Release? release, string? serviceName)
    {
        if (release == null || string.IsNullOrWhiteSpace(serviceName))
            return null;

        return release.Services.FirstOrDefault(service =>
            string.Equals(service.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(SanitizeOutputFileName(service.ServiceName), serviceName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.ImageName, serviceName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindServiceArtifactFolder(string artifactsRoot, string serviceName)
    {
        if (!Directory.Exists(artifactsRoot) || string.IsNullOrWhiteSpace(serviceName))
            return null;

        foreach (var candidate in BuildReleaseFolderNameCandidates(serviceName))
        {
            var path = Path.Combine(artifactsRoot, candidate);
            if (Directory.Exists(path))
                return path;
        }

        var sanitized = SanitizeOutputFileName(serviceName);

        return Directory.EnumerateDirectories(artifactsRoot)
            .FirstOrDefault(path =>
            {
                var folderName = Path.GetFileName(path);
                return string.Equals(folderName, serviceName, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(folderName, sanitized, StringComparison.OrdinalIgnoreCase) ||
                       folderName.StartsWith(serviceName, StringComparison.OrdinalIgnoreCase) ||
                       folderName.StartsWith(sanitized, StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string? FindFileByServiceName(string folder, string serviceName, string suffix)
    {
        if (!Directory.Exists(folder))
            return null;

        var expected = SanitizeOutputFileName(serviceName) + suffix;
        var exact = Path.Combine(folder, expected);
        if (File.Exists(exact))
            return exact;

        return Directory.EnumerateFiles(folder, "*" + suffix, SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), expected, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(path), serviceName, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).StartsWith(serviceName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> BuildReleaseFolderNameCandidates(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        yield return SanitizeOutputFileName(value);
        yield return SanitizePathPart(value);
        yield return value.Trim();
    }

    private static string SanitizeOutputFileName(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "item" : value.Trim();

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            result = result.Replace(invalidChar, '_');

        return result;
    }

    private static bool IsPreviewableFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".md" or ".txt" or ".log" or ".json" or ".yaml" or ".yml" or ".csv" or ".xml" or "";
    }

    private static string QuoteExplorerPath(string path)
    {
        var quote = ((char)34).ToString();
        return quote + path.Replace(quote, "") + quote;
    }

    private static System.Windows.Controls.TreeViewItem CreateOutputTreeItem(string header, OutputNodeTag tag, bool expanded = false)
    {
        return new System.Windows.Controls.TreeViewItem
        {
            Header = header,
            Tag = tag,
            IsExpanded = expanded
        };
    }

    private static System.Windows.Controls.TreeViewItem CreateFileTreeItem(string header, string path, string? releaseId = null)
    {
        return CreateOutputTreeItem(header, new OutputNodeTag { Kind = "file", Path = path, ReleaseId = releaseId });
    }

    private static System.Windows.Controls.TreeViewItem CreateInfoTreeItem(string text)
    {
        return new System.Windows.Controls.TreeViewItem { Header = text, IsEnabled = false };
    }

    private static string BuildOutputTreeOverview(string releasesRoot, string reposRoot, string logsRoot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Output navigation");
        builder.AppendLine();
        builder.AppendLine("Ліворуч тепер дерево, а не набір кнопок по одному релізу.");
        builder.AppendLine();
        builder.AppendLine("## Roots");
        builder.AppendLine("- Releases: " + releasesRoot);
        builder.AppendLine("- Repositories: " + reposRoot);
        builder.AppendLine("- Logs: " + logsRoot);
        builder.AppendLine();
        builder.AppendLine("## Як користуватися");
        builder.AppendLine("- Релізи / розгортайки -> вибери конкретний реліз.");
        builder.AppendLine("- Сервіси / окремі стеки змін -> вибери сервіс -> Стек змін сервісу.");
        builder.AppendLine("- Service notes files -> готові notes по конкретному сервісу.");
        builder.AppendLine("- Prompts -> AI prompt по конкретному сервісу.");
        builder.AppendLine("- Git repositories / logs -> FETCH_HEAD, ORIG_HEAD та .git/logs по репозиторіях.");
        return builder.ToString();
    }

    private void CopyOutputPreview_Click(object sender, RoutedEventArgs e)
    {
        var text = OutputPreviewTextBox.Text ?? "";

        if (string.IsNullOrWhiteSpace(text))
        {
            WpfMessageBox.Show("Немає тексту для копіювання.", "Release Notes Helper", WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            return;
        }

        System.Windows.Clipboard.SetText(text);
        AppendLog("Output preview copied to clipboard.");
    }

    private void ExportOutputPreview_Click(object sender, RoutedEventArgs e)
    {
        var text = OutputPreviewTextBox.Text ?? "";

        if (string.IsNullOrWhiteSpace(text))
        {
            WpfMessageBox.Show("Немає тексту для експорту.", "Release Notes Helper", WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            return;
        }

        var defaultName = !string.IsNullOrWhiteSpace(_currentOutputFilePath)
            ? Path.GetFileName(_currentOutputFilePath)
            : "release-output.md";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Експорт output preview",
            FileName = defaultName,
            Filter = "Markdown/Text (*.md;*.txt)|*.md;*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        File.WriteAllText(dialog.FileName, text, Encoding.UTF8);
        AppendLog("Output preview exported: " + dialog.FileName);
    }

    private static string SanitizePathPart(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "release" : value.Trim();

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            result = result.Replace(invalidChar, '_');

        return result.Replace(" ", "_");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _visibleLogLines.Clear();
        LogTextBox.Text = "";
        if (BottomLogStatusTextBlock != null)
            BottomLogStatusTextBlock.Text = "Журнал preview очищено. Повний файл сесії не видалено.";
        AppendLog("Log preview cleared. Full session log file was not deleted.");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = LogTextBox.Text ?? "";

        if (string.IsNullOrWhiteSpace(text))
        {
            WpfMessageBox.Show("Лог порожній.", "Release Notes Helper", WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
            return;
        }

        System.Windows.Clipboard.SetText(text);
        AppendLog("Log copied to clipboard.");
    }

    private async void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = await _store.LoadAsync<AppConfiguration>("config.json");
            var logsRoot = !string.IsNullOrWhiteSpace(config?.LogsPath)
                ? Environment.ExpandEnvironmentVariables(config.LogsPath.Trim())
                : AppPaths.LogsPath;

            Directory.CreateDirectory(logsRoot);

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Зберегти log.txt",
                InitialDirectory = logsRoot,
                FileName = $"rnh-log-export-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true)
                return;

            var text = !string.IsNullOrWhiteSpace(_sessionLogFilePath) && File.Exists(_sessionLogFilePath)
                ? await File.ReadAllTextAsync(_sessionLogFilePath, Encoding.UTF8)
                : _logBuilder.ToString();

            await File.WriteAllTextAsync(dialog.FileName, text, Encoding.UTF8);
            AppendLog($"Full log exported: {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            AppendLog("Log save failed: " + ex.Message);
            WpfMessageBox.Show(ex.Message, "Save log error", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
    }

    // RNH_AUTO_SESSION_LOG_2026_06_18: opens the automatically written full session log.
    private void OpenSessionLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_sessionLogFilePath) || !File.Exists(_sessionLogFilePath))
            {
                WpfMessageBox.Show("Повний лог сесії ще не створено.", "Release Notes Helper", WpfMessageBoxButton.OK, WpfMessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = _sessionLogFilePath,
                UseShellExecute = true
            });

            AppendLog("Full session log opened: " + _sessionLogFilePath);
        }
        catch (Exception ex)
        {
            AppendLog("Open session log failed: " + ex.Message);
            WpfMessageBox.Show(ex.Message, "Open log error", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
    }

    private async Task InitializeSessionLogAsync()
    {
        try
        {
            var config = await _store.LoadAsync<AppConfiguration>("config.json");
            var logsRoot = !string.IsNullOrWhiteSpace(config?.LogsPath)
                ? Environment.ExpandEnvironmentVariables(config.LogsPath.Trim())
                : AppPaths.LogsPath;

            Directory.CreateDirectory(logsRoot);
            _sessionLogFilePath = Path.Combine(logsRoot, $"rnh-session-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            var header = new StringBuilder();
            header.AppendLine("# ReleaseNotesHelper session log");
            header.AppendLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            header.AppendLine();

            await File.WriteAllTextAsync(_sessionLogFilePath, header.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _sessionLogFilePath = null;
            _logBuilder.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARN: Session log file initialization failed: {ex.Message}");
        }
    }

    // RNH_BOTTOM_LOG_PANEL_2026_06_18_CS: update the docked journal and its collapsed header summary.
    // RNH_AUTO_SESSION_LOG_2026_06_18: write full log to file and keep only latest lines in the UI preview.
    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        _logBuilder.AppendLine(line);
        AppendSessionLogFile(line);

        _visibleLogLines.Enqueue(line);
        while (_visibleLogLines.Count > VisibleLogLineLimit)
            _visibleLogLines.Dequeue();

        if (BottomLogStatusTextBlock != null)
            BottomLogStatusTextBlock.Text = message;

        if (LogTextBox != null)
        {
            LogTextBox.Text = string.Join(Environment.NewLine, _visibleLogLines);
            if (_visibleLogLines.Count > 0)
                LogTextBox.Text += Environment.NewLine;
            LogTextBox.ScrollToEnd();
        }
    }

    private void AppendSessionLogFile(string line)
    {
        if (string.IsNullOrWhiteSpace(_sessionLogFilePath))
            return;

        try
        {
            File.AppendAllText(_sessionLogFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            _sessionLogFilePath = null;
        }
    }


}
