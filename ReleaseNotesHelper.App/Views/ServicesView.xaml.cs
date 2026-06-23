using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ReleaseNotesHelper.App.Ai;
using ReleaseNotesHelper.Core.Models;
using ReleaseNotesHelper.Core.Storage;

namespace ReleaseNotesHelper.App.Views;

public partial class ServicesView : System.Windows.Controls.UserControl
{
    private readonly AppDataStore _store = new();
    private List<ServiceDefinition> _services = [];
    private const string AiCredentialTarget = "ReleaseNotesHelper-AI";
    private bool _isLoadingSelection;
    private bool _isBusy;
    private bool _isRefreshingServicesProjectFilter;
    private List<string> _lastGitTags = [];
    private List<string> _lastGitBranches = [];

    public ServicesView()
    {
        InitializeComponent();
        Loaded += ServicesView_Loaded;
    }

    private async void ServicesView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await LoadServicesAsync();
    }

    private async Task LoadServicesAsync()
    {
        _services = await _store.LoadAsync<List<ServiceDefinition>>("services.json") ?? [];

        var changed = await SyncServicesFromLatestBaselineAsync();
        changed |= NormalizeServiceMetadata();
        changed |= MergeDuplicateServices();

        if (changed)
            await _store.SaveAsync("services.json", _services);

        RefreshServicesProjectFilter();
        RefreshServicesList();
    }

    private async Task<bool> SyncServicesFromLatestBaselineAsync()
    {
        var releases = await _store.LoadAsync<List<Release>>("releases.json") ?? [];
        var latestBaseline = releases
            .Where(x => x.Services.Count > 0)
            .OrderByDescending(x => x.BuiltAt ?? x.CreatedAt)
            .FirstOrDefault();

        if (latestBaseline == null)
            return false;

        var changed = false;

        foreach (var releaseService in latestBaseline.Services)
        {
            if (string.IsNullOrWhiteSpace(releaseService.ServiceName) &&
                string.IsNullOrWhiteSpace(releaseService.ImageName))
            {
                continue;
            }

            var existing = FindCatalogService(_services, releaseService.ServiceName, releaseService.ImageName);

            if (existing == null)
            {
                existing = new ServiceDefinition
                {
                    Name = !string.IsNullOrWhiteSpace(releaseService.ServiceName)
                        ? releaseService.ServiceName
                        : releaseService.ImageName,
                    GitUrl = releaseService.GitUrl ?? "",
                    BaseTag = releaseService.TargetRef ?? "",
                    SelectedBranch = "origin/dev",
                    CreatedFromInstaller = true,
                    InstallerImageName = releaseService.ImageName ?? "",
                    InstallerVersion = releaseService.SourceVersion ?? "",
                    ProjectName = latestBaseline.ProjectName,
                    CreatedAt = DateTime.Now
                };

                _services.Add(existing);
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(existing.ProjectName) && !string.IsNullOrWhiteSpace(latestBaseline.ProjectName))
            {
                existing.ProjectName = latestBaseline.ProjectName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(existing.InstallerImageName) &&
                !string.IsNullOrWhiteSpace(releaseService.ImageName))
            {
                existing.InstallerImageName = releaseService.ImageName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(existing.InstallerVersion) &&
                !string.IsNullOrWhiteSpace(releaseService.SourceVersion))
            {
                existing.InstallerVersion = releaseService.SourceVersion;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(existing.BaseTag) &&
                !string.IsNullOrWhiteSpace(releaseService.TargetRef))
            {
                existing.BaseTag = releaseService.TargetRef;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(existing.GitUrl) &&
                !string.IsNullOrWhiteSpace(releaseService.GitUrl))
            {
                existing.GitUrl = releaseService.GitUrl;
                changed = true;
            }

            if (string.Equals(releaseService.Status, "External image", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(existing.ValidationStatus, "External image", StringComparison.OrdinalIgnoreCase) ||
                    existing.IsActive)
                {
                    existing.ValidationStatus = "External image";
                    existing.IsActive = false;
                    existing.NeedsGitUrl = false;
                    changed = true;
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(existing.GitUrl))
            {
                if (existing.NeedsGitUrl ||
                    string.Equals(existing.ValidationStatus, "Needs Git URL", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(existing.ValidationStatus) ||
                    string.Equals(existing.ValidationStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    existing.NeedsGitUrl = false;
                    existing.ValidationStatus = "Valid";
                    existing.IsActive = true;
                    changed = true;
                }
            }
            else
            {
                if (!string.Equals(existing.ValidationStatus, "Ignore", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(existing.ValidationStatus, "External image", StringComparison.OrdinalIgnoreCase))
                {
                    existing.NeedsGitUrl = true;
                    existing.ValidationStatus = "Needs Git URL";
                    changed = true;
                }
            }
        }

        return changed;
    }

    private bool NormalizeServiceMetadata()
    {
        var changed = false;

        foreach (var service in _services)
        {
            var oldStatus = service.ValidationStatus;
            var oldNeedsGitUrl = service.NeedsGitUrl;
            var oldActive = service.IsActive;

            if (string.Equals(service.ValidationStatus, "Ignore", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(service.ValidationStatus, "External image", StringComparison.OrdinalIgnoreCase))
            {
                service.IsActive = false;
                service.NeedsGitUrl = false;
            }
            else if (string.IsNullOrWhiteSpace(service.GitUrl))
            {
                service.NeedsGitUrl = true;

                if (string.IsNullOrWhiteSpace(service.ValidationStatus) ||
                    string.Equals(service.ValidationStatus, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(service.ValidationStatus, "Valid", StringComparison.OrdinalIgnoreCase))
                {
                    service.ValidationStatus = "Needs Git URL";
                }
            }
            else
            {
                service.NeedsGitUrl = false;

                if (string.IsNullOrWhiteSpace(service.ValidationStatus) ||
                    string.Equals(service.ValidationStatus, "Needs Git URL", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(service.ValidationStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    service.ValidationStatus = "Valid";
                }

                if (!string.Equals(service.ValidationStatus, "Ignore", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(service.ValidationStatus, "External image", StringComparison.OrdinalIgnoreCase))
                {
                    service.IsActive = true;
                }
            }

            if (service.ValidationStatus != oldStatus ||
                service.NeedsGitUrl != oldNeedsGitUrl ||
                service.IsActive != oldActive)
            {
                changed = true;
            }
        }

        return changed;
    }

    private bool MergeDuplicateServices()
    {
        var changed = false;
        var merged = new List<ServiceDefinition>();

        foreach (var service in _services.OrderBy(GetServiceSortRank).ThenBy(x => x.Name))
        {
            var existing = merged.FirstOrDefault(x => ServicesMatch(x, service));

            if (existing == null)
            {
                merged.Add(service);
                continue;
            }

            changed = true;

            if (string.IsNullOrWhiteSpace(existing.GitUrl) && !string.IsNullOrWhiteSpace(service.GitUrl))
                existing.GitUrl = service.GitUrl;

            if (string.IsNullOrWhiteSpace(existing.BaseTag) && !string.IsNullOrWhiteSpace(service.BaseTag))
                existing.BaseTag = service.BaseTag;

            if (string.IsNullOrWhiteSpace(existing.SelectedBranch) && !string.IsNullOrWhiteSpace(service.SelectedBranch))
                existing.SelectedBranch = service.SelectedBranch;

            if (string.IsNullOrWhiteSpace(existing.InstallerImageName) && !string.IsNullOrWhiteSpace(service.InstallerImageName))
                existing.InstallerImageName = service.InstallerImageName;

            if (string.IsNullOrWhiteSpace(existing.InstallerVersion) && !string.IsNullOrWhiteSpace(service.InstallerVersion))
                existing.InstallerVersion = service.InstallerVersion;

            if (string.IsNullOrWhiteSpace(existing.Notes) && !string.IsNullOrWhiteSpace(service.Notes))
                existing.Notes = service.Notes;

            if (!string.IsNullOrWhiteSpace(existing.GitUrl) &&
                !string.Equals(existing.ValidationStatus, "Ignore", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(existing.ValidationStatus, "External image", StringComparison.OrdinalIgnoreCase))
            {
                existing.ValidationStatus = "Valid";
                existing.NeedsGitUrl = false;
                existing.IsActive = true;
            }
        }

        _services = merged;
        return changed;
    }


    private void RefreshServicesProjectFilter()
    {
        if (ServicesProjectFilterComboBox == null)
            return;

        var current = GetServicesProjectFilter();
        var projects = _services
            .Select(x => x.ProjectName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        _isRefreshingServicesProjectFilter = true;
        try
        {
            ServicesProjectFilterComboBox.Items.Clear();
            ServicesProjectFilterComboBox.Items.Add("Усі проекти");

            foreach (var project in projects)
                ServicesProjectFilterComboBox.Items.Add(project);

            var selected = !string.IsNullOrWhiteSpace(current) && projects.Any(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase))
                ? current
                : "Усі проекти";

            ServicesProjectFilterComboBox.SelectedItem = selected;
            if (ServicesProjectFilterComboBox.SelectedIndex < 0)
                ServicesProjectFilterComboBox.SelectedIndex = 0;
        }
        finally
        {
            _isRefreshingServicesProjectFilter = false;
        }
    }

    private string GetServicesProjectFilter()
    {
        var value = ServicesProjectFilterComboBox?.SelectedItem?.ToString()?.Trim() ?? "";
        return string.Equals(value, "Усі проекти", StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    private void ServicesProjectFilterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isBusy || _isRefreshingServicesProjectFilter || ServicesListBox == null)
            return;

        RefreshServicesList();
    }

    private string GetDefaultServiceProjectName()
    {
        var filter = GetServicesProjectFilter();
        if (!string.IsNullOrWhiteSpace(filter))
            return filter;

        return "RS.Core Poruch";
    }

    private void RefreshServicesList()
    {
        if (ServicesListBox == null || ServicesSearchTextBox == null || ServicesFilterComboBox == null || ServicesSortComboBox == null || ServicesProjectFilterComboBox == null)
            return;

        var selectedName = (ServicesListBox.SelectedItem as ServiceDefinition)?.Name;
        var query = ServicesSearchTextBox.Text?.Trim() ?? "";
        var projectFilter = GetServicesProjectFilter();
        var filter = GetComboBoxText(ServicesFilterComboBox);
        var sort = GetComboBoxText(ServicesSortComboBox);

        IEnumerable<ServiceDefinition> items = _services;

        if (!string.IsNullOrWhiteSpace(projectFilter))
        {
            items = items.Where(x => string.Equals(x.ProjectName?.Trim(), projectFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(x =>
                ContainsIgnoreCase(x.Name, query) ||
                ContainsIgnoreCase(x.GitUrl, query) ||
                ContainsIgnoreCase(x.InstallerImageName, query) ||
                ContainsIgnoreCase(x.ProjectName, query) ||
                ContainsIgnoreCase(x.Notes, query));
        }

        items = filter switch
        {
            "Valid" => items.Where(x => string.Equals(GetEffectiveStatus(x), "Valid", StringComparison.OrdinalIgnoreCase)),
            "Needs Git URL" => items.Where(x => string.Equals(GetEffectiveStatus(x), "Needs Git URL", StringComparison.OrdinalIgnoreCase)),
            "Unknown" => items.Where(x => string.Equals(GetEffectiveStatus(x), "Unknown", StringComparison.OrdinalIgnoreCase)),
            "Inactive / ignored" => items.Where(x => !x.IsActive ||
                string.Equals(GetEffectiveStatus(x), "Ignore", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetEffectiveStatus(x), "External image", StringComparison.OrdinalIgnoreCase)),
            _ => items
        };

        items = sort switch
        {
            "Name A-Z" => items.OrderBy(x => x.Name),
            "Status" => items.OrderBy(x => GetEffectiveStatus(x)).ThenBy(x => x.Name),
            "Project" => items.OrderBy(x => string.IsNullOrWhiteSpace(x.ProjectName) ? "~" : x.ProjectName).ThenBy(GetServiceSortRank).ThenBy(x => x.Name),
            _ => items.OrderBy(GetServiceSortRank).ThenBy(x => x.Name)
        };

        var sorted = items.ToList();

        ServicesListBox.ItemsSource = null;
        ServicesListBox.ItemsSource = sorted;

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            ServicesListBox.SelectedItem = sorted.FirstOrDefault(x =>
                string.Equals(x.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ServicesSearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isBusy || ServicesListBox == null)
            return;

        RefreshServicesList();
    }

    private void ServicesFilterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isBusy || ServicesListBox == null)
            return;

        RefreshServicesList();
    }

    private void ServicesSortComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isBusy || ServicesListBox == null)
            return;

        RefreshServicesList();
    }

    private void AddService_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        ServicesListBox.SelectedItem = null;

        ServiceNameTextBox.Text = "";
        ServiceProjectTextBox.Text = GetDefaultServiceProjectName();
        ServiceGitUrlTextBox.Text = "";
        ServiceValidationStatusComboBox.Text = "Unknown";
        ServiceIsActiveCheckBox.IsChecked = true;
        ServiceNotesTextBox.Text = "";

        ClearGitLists();

        GitSyncStatusTextBlock.Text = "Git sync: ще не виконано";
        GitSyncStatusTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
    }

    private async void SaveService_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        var selectedService = ServicesListBox.SelectedItem as ServiceDefinition;

        if (selectedService == null)
        {
            selectedService = new ServiceDefinition();
            _services.Add(selectedService);
        }

        selectedService.Name = ServiceNameTextBox.Text.Trim();
        selectedService.ProjectName = string.IsNullOrWhiteSpace(ServiceProjectTextBox.Text)
            ? GetDefaultServiceProjectName()
            : ServiceProjectTextBox.Text.Trim();
        selectedService.GitUrl = ServiceGitUrlTextBox.Text.Trim();
        selectedService.BaseTag = BaseTagsListBox.SelectedItem?.ToString() ?? selectedService.BaseTag;
        selectedService.SelectedBranch = TargetBranchesListBox.SelectedItem?.ToString() ?? selectedService.SelectedBranch;
        selectedService.IsActive = ServiceIsActiveCheckBox.IsChecked ?? true;
        selectedService.ValidationStatus = string.IsNullOrWhiteSpace(ServiceValidationStatusComboBox.Text)
            ? GuessServiceStatus(selectedService)
            : ServiceValidationStatusComboBox.Text.Trim();
        selectedService.Notes = ServiceNotesTextBox.Text.Trim();

        selectedService.NeedsGitUrl = string.IsNullOrWhiteSpace(selectedService.GitUrl) ||
            string.Equals(selectedService.ValidationStatus, "Needs Git URL", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(selectedService.GitUrl) &&
            string.Equals(selectedService.ValidationStatus, "Needs Git URL", StringComparison.OrdinalIgnoreCase))
        {
            selectedService.ValidationStatus = "Valid";
            selectedService.NeedsGitUrl = false;
        }

        if (string.Equals(selectedService.ValidationStatus, "Ignore", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selectedService.ValidationStatus, "External image", StringComparison.OrdinalIgnoreCase))
        {
            selectedService.IsActive = false;
            selectedService.NeedsGitUrl = false;
        }

        NormalizeServiceMetadata();
        // MergeDuplicateServices();

        await _store.SaveAsync("services.json", _services);

        RefreshServicesProjectFilter();
        RefreshServicesList();

        ServicesListBox.SelectedItem = _services.FirstOrDefault(x =>
            string.Equals(x.Name, selectedService.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.GitUrl, selectedService.GitUrl, StringComparison.OrdinalIgnoreCase));

        System.Windows.MessageBox.Show(
            "Сервіс збережено.",
            "Release Notes Helper",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private async void DeleteService_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        if (ServicesListBox.SelectedItem is not ServiceDefinition selectedService)
        {
            System.Windows.MessageBox.Show(
                "Оберіть сервіс для видалення.",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"Видалити сервіс з довідника?\n\n{selectedService.Name}\n\nЛокальний git-репозиторій на диску не видаляється.",
            "Release Notes Helper",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes)
            return;

        _services.Remove(selectedService);

        await _store.SaveAsync("services.json", _services);

        ServicesListBox.SelectedItem = null;
        ServiceNameTextBox.Text = "";
        ServiceProjectTextBox.Text = GetDefaultServiceProjectName();
        ServiceGitUrlTextBox.Text = "";
        ServiceValidationStatusComboBox.Text = "Unknown";
        ServiceIsActiveCheckBox.IsChecked = true;
        ServiceNotesTextBox.Text = "";
        ClearGitLists();

        RefreshServicesProjectFilter();
        RefreshServicesList();

        GitSyncStatusTextBlock.Text = "Сервіс видалено з довідника.";
        GitSyncStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
    }

    private async void SyncCatalogFromBaseline_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        var changed = await SyncServicesFromLatestBaselineAsync();
        changed |= NormalizeServiceMetadata();
        changed |= MergeDuplicateServices();

        await _store.SaveAsync("services.json", _services);
        RefreshServicesProjectFilter();
        RefreshServicesList();

        System.Windows.MessageBox.Show(
            changed
                ? "Довідник сервісів синхронізовано з останнім збереженим baseline."
                : "Змін не знайдено. Довідник уже синхронізований.",
            "Release Notes Helper",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private async void ServicesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isBusy || _isLoadingSelection)
            return;

        if (ServicesListBox.SelectedItem is not ServiceDefinition service)
            return;

        ServiceNameTextBox.Text = service.Name;
        ServiceProjectTextBox.Text = string.IsNullOrWhiteSpace(service.ProjectName) ? GetDefaultServiceProjectName() : service.ProjectName;
        ServiceGitUrlTextBox.Text = service.GitUrl;
        ServiceValidationStatusComboBox.Text = GetEffectiveStatus(service);
        ServiceIsActiveCheckBox.IsChecked = service.IsActive;
        ServiceNotesTextBox.Text = service.Notes ?? "";

        ClearGitLists();

        if (string.IsNullOrWhiteSpace(service.GitUrl))
        {
            GitSyncStatusTextBlock.Text = "Git sync: Git URL не заповнений";
            GitSyncStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
            return;
        }

        GitSyncStatusTextBlock.Text = "Git sync: читаю локальний репозиторій...";
        GitSyncStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkOrange;

        await LoadLocalGitInfoForSelectedServiceAsync(service);
    }

    private async void SyncService_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        var serviceName = ServiceNameTextBox.Text.Trim();
        var gitUrl = ServiceGitUrlTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(gitUrl))
        {
            System.Windows.MessageBox.Show(
                "Вкажи назву сервісу та Git URL.",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);

            return;
        }

        try
        {
            SetBusy(true, "Git sync: виконується...");

            var reposRoot = await GetRepositoriesRootAsync();
            Directory.CreateDirectory(reposRoot);

            var localRepoPath = Path.Combine(reposRoot, SanitizeFolderName(serviceName));

            if (!Directory.Exists(localRepoPath) || !Directory.Exists(Path.Combine(localRepoPath, ".git")))
            {
                await RunGitAsync(reposRoot, "clone", "--", gitUrl, localRepoPath);
            }

            await RunGitAsync(localRepoPath, "fetch", "--all", "--tags", "--prune", "--force");

            var selectedService = ServicesListBox.SelectedItem as ServiceDefinition;
            var (tags, branches) = await ReadGitTagsAndBranchesAsync(localRepoPath);
            ApplyGitInfoToUi(tags, branches, selectedService);

            if (selectedService != null)
            {
                selectedService.GitUrl = gitUrl;
                selectedService.NeedsGitUrl = false;
                selectedService.ValidationStatus = "Valid";
                selectedService.IsActive = true;

                await _store.SaveAsync("services.json", _services);
                RefreshServicesList();
            }

            SetBusy(false, $"Git sync: OK — знайдено тегів: {tags.Count}, гілок: {branches.Count}");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Git sync: помилка — {ex.Message}");

            System.Windows.MessageBox.Show(
                ex.Message,
                "Git sync error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async void CollectServiceChanges_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        var serviceName = ServiceNameTextBox.Text.Trim();
        var baseRef = BaseTagsListBox.SelectedItem?.ToString();
        var targetRef = TargetBranchesListBox.SelectedItem?.ToString();
        var baseRefType = GetRefTypeText(BaseRefTypeComboBox, "Tag");
        var targetRefType = GetRefTypeText(TargetRefTypeComboBox, "Branch");

        if (string.IsNullOrWhiteSpace(serviceName) ||
            string.IsNullOrWhiteSpace(baseRef) ||
            string.IsNullOrWhiteSpace(targetRef))
        {
            System.Windows.MessageBox.Show(
                "Оберіть сервіс, base ref та target ref.",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);

            return;
        }

        try
        {
            SetBusy(true, "Порівняння refs виконується...");

            var reposRoot = await GetRepositoriesRootAsync();
            var localRepoPath = Path.Combine(reposRoot, SanitizeFolderName(serviceName));

            if (!Directory.Exists(Path.Combine(localRepoPath, ".git")))
            {
                throw new InvalidOperationException("Локальний Git-репозиторій не знайдено. Спочатку натисни 'Завантажити / оновити'.");
            }

            var baseCommit = await ResolveGitCommitAsync(localRepoPath, baseRef);
            var targetCommit = await ResolveGitCommitAsync(localRepoPath, targetRef);
            var baseShort = await ShortGitShaAsync(localRepoPath, baseCommit);
            var targetShort = await ShortGitShaAsync(localRepoPath, targetCommit);

            var mergeBase = await TryGetMergeBaseAsync(localRepoPath, baseCommit, targetCommit);
            var baseIsAncestor = await IsAncestorAsync(localRepoPath, baseCommit, targetCommit);
            var targetIsAncestor = await IsAncestorAsync(localRepoPath, targetCommit, baseCommit);
            var effectiveBase = baseIsAncestor || string.IsNullOrWhiteSpace(mergeBase) ? baseCommit : mergeBase;
            var effectiveBaseShort = await ShortGitShaAsync(localRepoPath, effectiveBase);

            var historyWarning = BuildHistoryWarning(baseIsAncestor, targetIsAncestor, mergeBase, effectiveBaseShort);

            var commits = await RunGitAsync(
                localRepoPath,
                "log",
                $"{effectiveBase}..{targetCommit}",
                "--pretty=format:%h | %ad | %an | %s",
                "--date=short");

            var changedFiles = await RunGitAsync(
                localRepoPath,
                "diff",
                "--name-status",
                $"{effectiveBase}..{targetCommit}");

            var commitsCount = SplitGitLines(commits).Count();
            var filesCount = SplitGitLines(changedFiles).Count();

            var baseTagInfo = IsTagRefType(BaseRefTypeComboBox, true)
                ? await ReadGitTagInfoAsync(localRepoPath, baseRef)
                : null;
            var targetTagInfo = IsTagRefType(TargetRefTypeComboBox, false)
                ? await ReadGitTagInfoAsync(localRepoPath, targetRef)
                : null;

            var outputRoot = await GetOutputRootAsync();
            var folderName = $"{DateTime.Now:yyyy-MM-dd_HHmm}__{SanitizeFolderName(baseRef)}_to_{SanitizeFolderName(targetRef)}";
            var compareOutputPath = Path.Combine(outputRoot, "service-compare", SanitizeFolderName(serviceName), folderName);
            Directory.CreateDirectory(compareOutputPath);

            await File.WriteAllTextAsync(Path.Combine(compareOutputPath, "summary.md"), BuildServiceCompareSummary(
                serviceName,
                baseRefType,
                baseRef,
                targetRefType,
                targetRef,
                baseShort,
                targetShort,
                effectiveBaseShort,
                commitsCount,
                filesCount,
                targetTagInfo,
                historyWarning));

            await File.WriteAllTextAsync(Path.Combine(compareOutputPath, "tag-info.md"), BuildTagInfoMarkdown(baseTagInfo, targetTagInfo));
            await File.WriteAllTextAsync(Path.Combine(compareOutputPath, "tag-changelog.md"), BuildTagChangelogMarkdown(targetTagInfo));
            await File.WriteAllTextAsync(Path.Combine(compareOutputPath, "commits.md"), BuildCodeFile("Commits", commits));
            await File.WriteAllTextAsync(Path.Combine(compareOutputPath, "changed-files.md"), BuildCodeFile("Changed files", changedFiles));
            await File.WriteAllTextAsync(Path.Combine(compareOutputPath, "git-info.md"), BuildServiceCompareGitInfo(
                serviceName,
                baseRefType,
                baseRef,
                targetRefType,
                targetRef,
                baseCommit,
                targetCommit,
                mergeBase,
                effectiveBase,
                baseIsAncestor,
                targetIsAncestor,
                historyWarning));
            await File.WriteAllTextAsync(Path.Combine(compareOutputPath, "ai-prompt.md"), BuildServiceCompareAiPrompt(
                serviceName,
                baseRef,
                targetRef,
                targetTagInfo,
                commits,
                changedFiles));

            SetBusy(false, $"Compare refs: OK — commits: {commitsCount}, files: {filesCount}");

            System.Windows.MessageBox.Show(
                $"Порівняння refs сформовано:{Environment.NewLine}{compareOutputPath}",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Compare refs: помилка — {ex.Message}");

            System.Windows.MessageBox.Show(
                ex.Message,
                "Compare refs error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task LoadLocalGitInfoForSelectedServiceAsync(ServiceDefinition service)
    {
        try
        {
            var reposRoot = await GetRepositoriesRootAsync();
            var localRepoPath = Path.Combine(reposRoot, SanitizeFolderName(service.Name));

            if (!Directory.Exists(Path.Combine(localRepoPath, ".git")))
            {
                GitSyncStatusTextBlock.Text = "Git sync: локальний репозиторій ще не завантажено";
                GitSyncStatusTextBlock.Foreground = System.Windows.Media.Brushes.Gray;
                return;
            }

            var (tags, branches) = await ReadGitTagsAndBranchesAsync(localRepoPath);
            ApplyGitInfoToUi(tags, branches, service);

            GitSyncStatusTextBlock.Text = $"Git sync: локально знайдено тегів: {tags.Count}, гілок: {branches.Count}";
            GitSyncStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        }
        catch (Exception ex)
        {
            GitSyncStatusTextBlock.Text = $"Git sync: не вдалося прочитати локально — {ex.Message}";
            GitSyncStatusTextBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
        }
    }


    private void BaseRefTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSelection || BaseTagsListBox == null)
            return;

        RefreshRefListSelections(ServicesListBox?.SelectedItem as ServiceDefinition);
    }

    private void TargetRefTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSelection || TargetBranchesListBox == null)
            return;

        RefreshRefListSelections(ServicesListBox?.SelectedItem as ServiceDefinition);
    }

    private void RefreshRefListSelections(ServiceDefinition? service)
    {
        _isLoadingSelection = true;

        try
        {
            ApplyBaseRefList(service);
            ApplyTargetRefList(service);
        }
        finally
        {
            _isLoadingSelection = false;
        }
    }

    private void ApplyBaseRefList(ServiceDefinition? service)
    {
        var useTags = IsTagRefType(BaseRefTypeComboBox, defaultValue: true);
        var refs = useTags ? _lastGitTags : _lastGitBranches;

        BaseTagsListBox.ItemsSource = refs;
        BaseTagsListBox.IsEnabled = !_isBusy && refs.Count > 0;

        string? preferred = null;

        if (useTags)
        {
            preferred = !string.IsNullOrWhiteSpace(service?.BaseTag)
                ? refs.FirstOrDefault(x => x == service.BaseTag)
                : null;

            preferred ??= refs.FirstOrDefault(x => x == service?.InstallerVersion)
                         ?? refs.FirstOrDefault(x => x == "1.5.0")
                         ?? refs.FirstOrDefault();
        }
        else
        {
            preferred = !string.IsNullOrWhiteSpace(service?.SelectedBranch)
                ? refs.FirstOrDefault(x => x == service.SelectedBranch)
                : null;

            preferred ??= refs.FirstOrDefault(x => x == "origin/dev")
                         ?? refs.FirstOrDefault(x => x == "origin/master")
                         ?? refs.FirstOrDefault();
        }

        BaseTagsListBox.SelectedItem = preferred;
    }

    private void ApplyTargetRefList(ServiceDefinition? service)
    {
        var useTags = IsTagRefType(TargetRefTypeComboBox, defaultValue: false);
        var refs = useTags ? _lastGitTags : _lastGitBranches;

        TargetBranchesListBox.ItemsSource = refs;
        TargetBranchesListBox.IsEnabled = !_isBusy && refs.Count > 0;

        string? preferred = null;

        if (useTags)
        {
            preferred = !string.IsNullOrWhiteSpace(service?.BaseTag)
                ? refs.FirstOrDefault(x => x == service.BaseTag)
                : null;

            preferred ??= refs.FirstOrDefault();
        }
        else
        {
            preferred = !string.IsNullOrWhiteSpace(service?.SelectedBranch)
                ? refs.FirstOrDefault(x => x == service.SelectedBranch)
                : null;

            preferred ??= refs.FirstOrDefault(x => x == "origin/dev")
                         ?? refs.FirstOrDefault(x => x == "origin/master")
                         ?? refs.FirstOrDefault();
        }

        TargetBranchesListBox.SelectedItem = preferred;
    }

    private static bool IsTagRefType(System.Windows.Controls.ComboBox comboBox, bool defaultValue)
    {
        var value = GetRefTypeText(comboBox, defaultValue ? "Tag" : "Branch");
        return value.Contains("Tag", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Тег", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRefTypeText(System.Windows.Controls.ComboBox comboBox, string fallback)
    {
        if (comboBox?.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            return item.Content?.ToString() ?? fallback;

        return string.IsNullOrWhiteSpace(comboBox?.Text) ? fallback : comboBox.Text;
    }

    private async Task<string> ResolveGitCommitAsync(string localRepoPath, string refName)
    {
        var output = await RunGitAsync(
            localRepoPath,
            "rev-parse",
            "--verify",
            $"{refName}^{{commit}}");

        return output.Trim();
    }

    private async Task<string> ShortGitShaAsync(string localRepoPath, string sha)
    {
        var output = await RunGitAsync(localRepoPath, "rev-parse", "--short", sha);
        return output.Trim();
    }

    private async Task<string> TryGetMergeBaseAsync(string localRepoPath, string leftCommit, string rightCommit)
    {
        var result = await RunGitExitCodeAsync(localRepoPath, "merge-base", leftCommit, rightCommit);
        return result.ExitCode == 0 ? result.Output.Trim() : "";
    }

    private async Task<bool> IsAncestorAsync(string localRepoPath, string possibleAncestor, string possibleDescendant)
    {
        var result = await RunGitExitCodeAsync(localRepoPath, "merge-base", "--is-ancestor", possibleAncestor, possibleDescendant);
        return result.ExitCode == 0;
    }

    private async Task<GitTagInfo> ReadGitTagInfoAsync(string localRepoPath, string tagName)
    {
        var objectType = (await RunGitAsync(localRepoPath, "cat-file", "-t", tagName)).Trim();
        var commitSha = await ResolveGitCommitAsync(localRepoPath, tagName);

        var dateResult = await RunGitExitCodeAsync(
            localRepoPath,
            "for-each-ref",
            $"refs/tags/{tagName}",
            "--format=%(creatordate:iso8601)");

        var message = "";
        if (string.Equals(objectType, "tag", StringComparison.OrdinalIgnoreCase))
        {
            var messageResult = await RunGitExitCodeAsync(
                localRepoPath,
                "for-each-ref",
                $"refs/tags/{tagName}",
                "--format=%(contents)");

            if (messageResult.ExitCode == 0)
                message = messageResult.Output.Trim();
        }

        return new GitTagInfo
        {
            Name = tagName,
            ObjectType = objectType,
            CommitSha = commitSha,
            CreatedAt = dateResult.ExitCode == 0 ? dateResult.Output.Trim() : "",
            Message = message
        };
    }

    private static string BuildHistoryWarning(bool baseIsAncestor, bool targetIsAncestor, string mergeBase, string effectiveBaseShort)
    {
        if (baseIsAncestor)
            return "";

        if (targetIsAncestor)
            return "Target ref is an ancestor of base ref. The selected range may be reversed or contain no forward changes.";

        if (!string.IsNullOrWhiteSpace(mergeBase))
            return $"Base ref is not an ancestor of target ref. History appears diverged. Merge-base is {effectiveBaseShort}; diff/log may include unexpected changes.";

        return "No merge-base was found between selected refs. Diff/log may be incomplete or unavailable.";
    }

    private static string BuildServiceCompareSummary(
        string serviceName,
        string baseRefType,
        string baseRef,
        string targetRefType,
        string targetRef,
        string baseShort,
        string targetShort,
        string effectiveBaseShort,
        int commitsCount,
        int filesCount,
        GitTagInfo? targetTagInfo,
        string historyWarning)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Service compare — {serviceName}");
        builder.AppendLine();
        builder.AppendLine($"Service: {serviceName}");
        builder.AppendLine($"Base ref: {NormalizeRefKind(baseRefType)} {baseRef}");
        builder.AppendLine($"Target ref: {NormalizeRefKind(targetRefType)} {targetRef}");
        builder.AppendLine($"Compare mode: {NormalizeRefKind(baseRefType)} -> {NormalizeRefKind(targetRefType)}");
        builder.AppendLine($"Base commit: {baseShort}");
        builder.AppendLine($"Target commit: {targetShort}");
        builder.AppendLine($"Effective diff base: {effectiveBaseShort}");
        builder.AppendLine($"Commits: {commitsCount}");
        builder.AppendLine($"Files: {filesCount}");
        builder.AppendLine($"Target tag changelog: {DescribeTagChangelog(targetTagInfo)}");

        if (!string.IsNullOrWhiteSpace(historyWarning))
        {
            builder.AppendLine();
            builder.AppendLine("## Warning");
            builder.AppendLine(historyWarning);
        }

        return builder.ToString();
    }

    private static string BuildTagInfoMarkdown(GitTagInfo? baseTagInfo, GitTagInfo? targetTagInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Tag info");
        builder.AppendLine();
        AppendTagInfo(builder, "Base tag", baseTagInfo);
        builder.AppendLine();
        AppendTagInfo(builder, "Target tag", targetTagInfo);
        return builder.ToString();
    }

    private static void AppendTagInfo(StringBuilder builder, string title, GitTagInfo? tagInfo)
    {
        builder.AppendLine($"## {title}");

        if (tagInfo == null)
        {
            builder.AppendLine("Not a tag ref.");
            return;
        }

        builder.AppendLine($"Name: {tagInfo.Name}");
        builder.AppendLine($"Type: {(tagInfo.IsAnnotated ? "annotated tag" : "lightweight tag / commit pointer")}");
        builder.AppendLine($"Commit: {tagInfo.CommitSha}");

        if (!string.IsNullOrWhiteSpace(tagInfo.CreatedAt))
            builder.AppendLine($"Date: {tagInfo.CreatedAt}");

        builder.AppendLine($"Has changelog: {(tagInfo.HasChangelog ? "yes" : "no")}");
    }

    private static string BuildTagChangelogMarkdown(GitTagInfo? targetTagInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Target tag changelog");
        builder.AppendLine();

        if (targetTagInfo == null)
        {
            builder.AppendLine("Target ref is not a tag. Tag changelog is not applicable.");
            return builder.ToString();
        }

        if (!targetTagInfo.IsAnnotated)
        {
            builder.AppendLine("Target tag is lightweight. Changelog message is unavailable.");
            return builder.ToString();
        }

        if (!targetTagInfo.HasChangelog)
        {
            builder.AppendLine("Target annotated tag does not contain a changelog message.");
            return builder.ToString();
        }

        builder.AppendLine(targetTagInfo.Message);
        return builder.ToString();
    }

    private static string BuildCodeFile(string title, string content)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(content) ? "No entries found." : content.Trim());
        return builder.ToString();
    }

    private static string BuildServiceCompareGitInfo(
        string serviceName,
        string baseRefType,
        string baseRef,
        string targetRefType,
        string targetRef,
        string baseCommit,
        string targetCommit,
        string mergeBase,
        string effectiveBase,
        bool baseIsAncestor,
        bool targetIsAncestor,
        string historyWarning)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Git info — {serviceName}");
        builder.AppendLine();
        builder.AppendLine($"Service: {serviceName}");
        builder.AppendLine($"Base ref type: {NormalizeRefKind(baseRefType)}");
        builder.AppendLine($"Base ref: {baseRef}");
        builder.AppendLine($"Base commit: {baseCommit}");
        builder.AppendLine($"Target ref type: {NormalizeRefKind(targetRefType)}");
        builder.AppendLine($"Target ref: {targetRef}");
        builder.AppendLine($"Target commit: {targetCommit}");
        builder.AppendLine($"Merge-base: {mergeBase}");
        builder.AppendLine($"Effective diff base: {effectiveBase}");
        builder.AppendLine($"History base ancestor of target: {baseIsAncestor}");
        builder.AppendLine($"History target ancestor of base: {targetIsAncestor}");

        if (!string.IsNullOrWhiteSpace(historyWarning))
        {
            builder.AppendLine();
            builder.AppendLine("Git history warning:");
            builder.AppendLine(historyWarning);
        }

        return builder.ToString();
    }

    private static string BuildServiceCompareAiPrompt(
        string serviceName,
        string baseRef,
        string targetRef,
        GitTagInfo? targetTagInfo,
        string commits,
        string changedFiles)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Ти готуєш короткий клієнтський changelog українською мовою для одного сервісу.");
        builder.AppendLine();
        builder.AppendLine($"Сервіс: {serviceName}");
        builder.AppendLine($"Порівняння: {baseRef} -> {targetRef}");
        builder.AppendLine();
        builder.AppendLine("Правила:");
        builder.AppendLine("- Якщо є changelog у target tag, використовуй його як основне джерело.");
        builder.AppendLine("- Коміти використовуй як допоміжну перевірку, щоб не пропустити важливі зміни.");
        builder.AppendLine("- Не згадуй Git, hash, гілки, авторів або внутрішні технічні деталі.");
        builder.AppendLine("- Пиши коротко, максимум 8 пунктів.");
        builder.AppendLine("- Починай одразу зі списку змін.");
        builder.AppendLine();

        if (targetTagInfo?.HasChangelog == true)
        {
            builder.AppendLine("Changelog from target tag:");
            builder.AppendLine(targetTagInfo.Message);
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("Changelog from target tag: unavailable.");
            builder.AppendLine();
        }

        builder.AppendLine("Commits:");
        builder.AppendLine(string.IsNullOrWhiteSpace(commits) ? "No commits found." : SanitizeCommitsForAi(commits));
        builder.AppendLine();
        builder.AppendLine("Changed files are not included in this AI prompt by default. Use changed-files.md only for local technical verification.");

        return builder.ToString();
    }

    private static string DescribeTagChangelog(GitTagInfo? tagInfo)
    {
        if (tagInfo == null)
            return "not applicable";

        if (!tagInfo.IsAnnotated)
            return "unavailable — lightweight tag";

        return tagInfo.HasChangelog ? "found" : "empty";
    }

    private static string NormalizeRefKind(string value)
    {
        return value.Contains("Tag", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Тег", StringComparison.OrdinalIgnoreCase)
            ? "tag"
            : "branch";
    }

    private async Task<(List<string> Tags, List<string> Branches)> ReadGitTagsAndBranchesAsync(string localRepoPath)
    {
        var branchesOutput = await RunGitAsync(
            localRepoPath,
            "for-each-ref",
            "--format=%(refname:short)",
            "refs/remotes/origin");

        var branches = SplitGitLines(branchesOutput)
            .Where(x => x != "origin")
            .Where(x => !x.EndsWith("/HEAD"))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var tagsOutput = await RunGitAsync(
            localRepoPath,
            "tag",
            "--sort=-creatordate");

        var tags = SplitGitLines(tagsOutput).ToList();

        return (tags, branches);
    }

    private void ApplyGitInfoToUi(List<string> tags, List<string> branches, ServiceDefinition? service)
    {
        _lastGitTags = tags;
        _lastGitBranches = branches;
        _isLoadingSelection = true;

        try
        {
            ApplyBaseRefList(service);
            ApplyTargetRefList(service);
        }
        finally
        {
            _isLoadingSelection = false;
        }
    }

    private void ClearGitLists()
    {
        _lastGitTags = [];
        _lastGitBranches = [];

        BaseTagsListBox.ItemsSource = null;
        BaseTagsListBox.SelectedItem = null;
        BaseTagsListBox.IsEnabled = false;

        TargetBranchesListBox.ItemsSource = null;
        TargetBranchesListBox.SelectedItem = null;
        TargetBranchesListBox.IsEnabled = false;
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;

        if (SaveServiceButton == null)
            return;

        SaveServiceButton.IsEnabled = !isBusy;
        DeleteServiceButton.IsEnabled = !isBusy;
        SyncServiceButton.IsEnabled = !isBusy;
        CollectChangesButton.IsEnabled = !isBusy;
        ServicesListBox.IsEnabled = !isBusy;
        ServicesSearchTextBox.IsEnabled = !isBusy;
        ServicesFilterComboBox.IsEnabled = !isBusy;
        ServicesSortComboBox.IsEnabled = !isBusy;
        ServicesProjectFilterComboBox.IsEnabled = !isBusy;
        BaseRefTypeComboBox.IsEnabled = !isBusy;
        TargetRefTypeComboBox.IsEnabled = !isBusy;

        BaseTagsListBox.IsEnabled = !isBusy && BaseTagsListBox.Items.Count > 0;
        TargetBranchesListBox.IsEnabled = !isBusy && TargetBranchesListBox.Items.Count > 0;

        ServiceProgressBar.Visibility = isBusy
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        GitSyncStatusTextBlock.Text = status;
        GitSyncStatusTextBlock.Foreground = isBusy
            ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.Green;
    }

    private static int GetServiceSortRank(ServiceDefinition service)
    {
        var status = GetEffectiveStatus(service);

        if (string.Equals(status, "Needs Git URL", StringComparison.OrdinalIgnoreCase))
            return 0;

        if (string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (string.Equals(status, "Valid", StringComparison.OrdinalIgnoreCase))
            return 2;

        if (!service.IsActive ||
            string.Equals(status, "Ignore", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "External image", StringComparison.OrdinalIgnoreCase))
            return 3;

        return 9;
    }

    private static string GetEffectiveStatus(ServiceDefinition service)
    {
        if (!string.IsNullOrWhiteSpace(service.ValidationStatus))
            return service.ValidationStatus;

        return GuessServiceStatus(service);
    }

    private static string GuessServiceStatus(ServiceDefinition service)
    {
        if (string.Equals(service.ValidationStatus, "Ignore", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.ValidationStatus, "External image", StringComparison.OrdinalIgnoreCase))
        {
            return service.ValidationStatus;
        }

        if (service.NeedsGitUrl || string.IsNullOrWhiteSpace(service.GitUrl))
            return "Needs Git URL";

        if (!service.IsActive)
            return "Ignore";

        return "Valid";
    }

    private static bool ContainsIgnoreCase(string? value, string query)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetComboBoxText(System.Windows.Controls.ComboBox comboBox)
    {
        if (comboBox?.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            return item.Content?.ToString() ?? "";

        return comboBox?.Text ?? "";
    }

    private static ServiceDefinition? FindCatalogService(IEnumerable<ServiceDefinition> catalog, string serviceName, string imageName)
    {
        var itemKeys = BuildServiceKeys(serviceName)
            .Concat(BuildServiceKeys(imageName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return catalog.FirstOrDefault(service =>
        {
            var serviceKeys = BuildServiceKeys(service.Name)
                .Concat(BuildServiceKeys(service.InstallerImageName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return serviceKeys.Any(itemKeys.Contains);
        });
    }

    private static bool ServicesMatch(ServiceDefinition left, ServiceDefinition right)
    {
        if (ReferenceEquals(left, right))
            return true;

        var leftKeys = BuildServiceKeys(left.Name)
            .Concat(BuildServiceKeys(left.InstallerImageName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rightKeys = BuildServiceKeys(right.Name)
            .Concat(BuildServiceKeys(right.InstallerImageName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return leftKeys.Any(rightKeys.Contains);
    }

    private static HashSet<string> BuildServiceKeys(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = NormalizeKey(value);

        if (string.IsNullOrWhiteSpace(normalized))
            return result;

        result.Add(normalized);

        foreach (var prefix in new[] { "vpo-", "rs-", "rscore-" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                normalized.Length > prefix.Length)
            {
                result.Add(normalized[prefix.Length..]);
            }
        }

        return result;
    }

    private static string NormalizeKey(string value)
    {
        return value.Trim().ToLowerInvariant().Replace("_", "-");
    }

    private static IEnumerable<string> SplitGitLines(string value)
    {
        return value
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private async Task<string> GetRepositoriesRootAsync()
    {
        var config = await _store.LoadAsync<AppConfiguration>("config.json");

        if (!string.IsNullOrWhiteSpace(config?.RepositoriesPath))
            return config.RepositoriesPath;

        return AppPaths.ReposPath;
    }

    private async Task<string> GetOutputRootAsync()
    {
        var config = await _store.LoadAsync<AppConfiguration>("config.json");

        if (!string.IsNullOrWhiteSpace(config?.OutputPath))
            return config.OutputPath;

        return AppPaths.OutputPath;
    }

    private static string SanitizeFolderName(string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }

        return value;
    }

    private static string BuildReleaseNotesPrompt(string commits, bool hideTechnicalDataFromAi)
    {
        // RNH_P0_2026_06_18: do not pass hashes/authors/dates to AI when privacy flag is enabled.
        var commitsForAi = hideTechnicalDataFromAi ? SanitizeCommitsForAi(commits) : commits;
        var builder = new StringBuilder();
        builder.AppendLine("Ти готуєш release notes для клієнта українською мовою.");
        builder.AppendLine();
        builder.AppendLine("Формат відповіді:");
        builder.AppendLine("RS.Core 1.5.0:");
        builder.AppendLine("* Додано ...");
        builder.AppendLine("* Покращено ...");
        builder.AppendLine("* Реалізовано ...");
        builder.AppendLine("* Усунуто ...");
        builder.AppendLine();
        builder.AppendLine("Правила:");
        builder.AppendLine("* Не використовуй commit hash.");
        builder.AppendLine("* Не згадуй авторів.");
        builder.AppendLine("* Не згадуй назви гілок.");
        builder.AppendLine("* Не згадуй технічні деталі реалізації.");
        builder.AppendLine("* Кожен пункт повинен бути одним реченням.");
        builder.AppendLine("* Не пиши більше 8 пунктів.");
        builder.AppendLine();
        builder.AppendLine("Коміти:");
        builder.AppendLine(commitsForAi);
        return builder.ToString();
    }

    private static string SanitizeCommitsForAi(string commits)
    {
        return string.Join(
            Environment.NewLine,
            SplitGitLines(commits)
                .Select(SanitizeCommitForAi)
                .Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string SanitizeCommitForAi(string commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
            return string.Empty;

        // Очікуваний формат: %h | %ad | %an | %s
        var parts = commit.Split('|');

        if (parts.Length >= 4)
            return parts[^1].Trim();

        return commit.Trim();
    }

    private async Task<string> GenerateWithRetryAsync(GeminiClient gemini, string apiKey, string prompt)
    {
        Exception? lastException = null;
        var delaysSeconds = new[] { 4, 10, 20, 40 };

        for (var attempt = 1; attempt <= delaysSeconds.Length + 1; attempt++)
        {
            try
            {
                return await gemini.GenerateAsync(apiKey, prompt);
            }
            catch (Exception ex) when (IsTransientAiError(ex) && attempt <= delaysSeconds.Length)
            {
                lastException = ex;
                GitSyncStatusTextBlock.Text = $"AI тимчасово недоступний. Спроба {attempt + 1}/{delaysSeconds.Length + 1} через {delaysSeconds[attempt - 1]} сек...";
                await Task.Delay(TimeSpan.FromSeconds(delaysSeconds[attempt - 1]));
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw lastException ?? new InvalidOperationException("AI request failed.");
    }

    private static bool IsTransientAiError(Exception ex)
    {
        var message = ex.ToString();

        return message.Contains("\"code\": 503", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporarily", StringComparison.OrdinalIgnoreCase);
    }


    private sealed class GitTagInfo
    {
        public string Name { get; init; } = "";
        public string ObjectType { get; init; } = "";
        public string CommitSha { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public string Message { get; init; } = "";
        public bool IsAnnotated => string.Equals(ObjectType, "tag", StringComparison.OrdinalIgnoreCase);
        public bool HasChangelog => IsAnnotated && !string.IsNullOrWhiteSpace(Message);
    }

    private sealed class GitCommandResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
        public string Error { get; init; } = "";
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitExitCodeAsync(workingDirectory, arguments);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());

        return result.Output;
    }

    private static async Task<GitCommandResult> RunGitExitCodeAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(ValidateGitArgument(argument));
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new GitCommandResult
        {
            ExitCode = process.ExitCode,
            Output = output,
            Error = error
        };
    }

    private static string ValidateGitArgument(string? argument)
    {
        if (argument == null)
            throw new ArgumentNullException(nameof(argument));

        if (argument.IndexOf('\0') >= 0 || argument.Contains('\r') || argument.Contains('\n'))
            throw new InvalidOperationException("Git argument contains an unsupported control character.");

        return argument;
    }
}
