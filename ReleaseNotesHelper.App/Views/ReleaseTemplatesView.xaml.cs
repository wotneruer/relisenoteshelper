using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ReleaseNotesHelper.App.Ai;
using ReleaseNotesHelper.App.Services;
using ReleaseNotesHelper.Core.Models;
using ReleaseNotesHelper.Core.Storage;

namespace ReleaseNotesHelper.App.Views;

public partial class ReleaseTemplatesView : System.Windows.Controls.UserControl
{
    // RNH_LIVE_FLOW_LOGGING_2026_06_18: flow logging enabled.
    private readonly AppDataStore _store = new();
    private const string AiCredentialTarget = "ReleaseNotesHelper-AI";

    private List<ReleaseTemplate> _templates = [];
    private List<ServiceDefinition> _services = [];
    private List<Release> _releases = [];

    private readonly ObservableCollection<ReleaseTemplateService> _rows = [];
    private readonly ObservableCollection<ReleaseService> _scopeResults = [];

    private string? _lastTemplateScanReleaseId;

    // RNH_SERVICE_TARGET_REFS_V2_2026_06_19: common target refs shown in the per-service template dropdown.
    public ObservableCollection<string> TargetRefOptions { get; } = [];

    private bool _isLoading;
    private bool _isBusy;
    // RNH_SERVICE_TARGET_REFS_REAL_GIT_2026_06_19: prevents overlapping Git branch-list refreshes.
    private bool _isRefreshingTargetRefsFromGit;
    private bool _isRefreshingTemplateProjectFilter;
    private string _lastTemplateProjectFilter = "";


    public ReleaseTemplatesView()
    {
        InitializeComponent();
        Loaded += ReleaseTemplatesView_Loaded;
        TemplatesListBox.MouseDoubleClick += TemplatesListBox_MouseDoubleClick;
    }

    private async void ReleaseTemplatesView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _isLoading = true;

        try
        {
            _templates = await SafeLoadAsync<List<ReleaseTemplate>>("templates.json") ?? [];
            _services = await SafeLoadAsync<List<ServiceDefinition>>("services.json") ?? [];
            _releases = await SafeLoadAsync<List<Release>>("releases.json") ?? [];

            AppLog.Info($"Release templates loaded. Templates={_templates.Count}, services={_services.Count}, releases={_releases.Count}.");

            TemplateServicesDataGrid.ItemsSource = _rows;
            ScopeResultsDataGrid.ItemsSource = _scopeResults;

            // RNH_SERVICE_TARGET_REFS_V2_LOAD_REFRESH
            RefreshTargetRefOptions();
            RefreshTemplateProjectFilter();

            RefreshTemplatesList();

            if (_templates.Count > 0)
            {
                var firstTemplate = _templates.OrderBy(x => string.IsNullOrWhiteSpace(x.ProjectName) ? "~" : x.ProjectName).ThenBy(x => x.Name).First();
                TemplatesListBox.SelectedItem = firstTemplate;
                LoadTemplateToUi(firstTemplate);
            }
            else
            {
                NewTemplateFromCatalog("RS.Core Poruch", includeActive: false);
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task<T?> SafeLoadAsync<T>(string fileName)
    {
        try
        {
            return await _store.LoadAsync<T>(fileName);
        }
        catch
        {
            return default;
        }
    }


    private void RefreshTemplateProjectFilter()
    {
        if (TemplateProjectFilterComboBox == null)
            return;

        var current = GetTemplateProjectFilter();
        var projects = _templates
            .Select(x => GetEffectiveProjectName(x.ProjectName, x.Name, x.LastReleaseName))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        _isRefreshingTemplateProjectFilter = true;
        try
        {
            TemplateProjectFilterComboBox.Items.Clear();
            TemplateProjectFilterComboBox.Items.Add("Усі проекти");

            foreach (var project in projects)
                TemplateProjectFilterComboBox.Items.Add(project);

            var selected = !string.IsNullOrWhiteSpace(current) && projects.Any(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase))
                ? current
                : "Усі проекти";

            TemplateProjectFilterComboBox.SelectedItem = selected;
            if (TemplateProjectFilterComboBox.SelectedIndex < 0)
                TemplateProjectFilterComboBox.SelectedIndex = 0;

            _lastTemplateProjectFilter = GetTemplateProjectFilter();
        }
        finally
        {
            _isRefreshingTemplateProjectFilter = false;
        }
    }

    private string GetTemplateProjectFilter()
    {
        var value = TemplateProjectFilterComboBox?.SelectedItem?.ToString()?.Trim() ?? "";
        return string.Equals(value, "Усі проекти", StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    private void TemplateProjectFilterComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoading || _isRefreshingTemplateProjectFilter || TemplatesListBox == null)
            return;

        var selected = GetTemplateProjectFilter();
        if (string.Equals(selected, _lastTemplateProjectFilter, StringComparison.OrdinalIgnoreCase))
            return;

        _lastTemplateProjectFilter = selected;
        RefreshTemplatesList();
    }

    private void RefreshTemplatesList()
    {
        if (TemplatesListBox == null)
            return;

        var query = TemplateSearchTextBox?.Text?.Trim() ?? "";
        var projectFilter = GetTemplateProjectFilter();

        IEnumerable<ReleaseTemplate> items = _templates;

        if (!string.IsNullOrWhiteSpace(projectFilter))
        {
            items = items.Where(x => string.Equals(GetEffectiveProjectName(x.ProjectName, x.Name, x.LastReleaseName), projectFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(x =>
                ContainsIgnoreCase(x.Name, query) ||
                ContainsIgnoreCase(x.ProjectName, query) ||
                ContainsIgnoreCase(x.Description, query));
        }

        TemplatesListBox.ItemsSource = null;
        TemplatesListBox.ItemsSource = items
            .OrderBy(x => string.IsNullOrWhiteSpace(x.ProjectName) ? "~" : x.ProjectName)
            .ThenBy(x => x.Name)
            .ToList();
    }

    private void TemplatesListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TemplatesListBox.SelectedItem is ReleaseTemplate template)
            LoadTemplateToUi(template);
    }

    private void TemplatesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoading)
            return;

        if (TemplatesListBox.SelectedItem is not ReleaseTemplate template)
            return;

        LoadTemplateToUi(template);
    }
    // RNH_SERVICE_TARGET_REFS_V2_2026_06_19: target ref dropdown and multi-target parsing helpers.
    // RNH_TARGET_REFS_CHECKBOX_PICKER_2026_06_19: multi-select branch/ref picker for one template service row.

    private void PickBaseRef_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is not ReleaseTemplateService row)
            return;

        var options = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in row.TargetRefOptions ?? [])
            AddTargetRefPickerValues(options, value);

        AddTargetRefPickerValues(options, row.BaselineRef);
        AddTargetRefPickerValues(options, row.TargetBranch);
        AddTargetRefPickerValues(options, TemplateDefaultBranchTextBox?.Text);

        if (options.Count == 0)
            options.Add("origin/dev");

        var picked = ShowBaseRefPickerDialog(row.ServiceName, options, row.BaselineRef);

        if (picked is null)
            return;

        row.BaselineRef = picked;

        // RNH_TEMPLATE_AUTO_INCLUDE_REF_ROWS_028: editing refs means the service is intended for scan.
        row.Included = true;

        if (!options.Contains(picked))
            options.Add(picked);

        row.TargetRefOptions = options.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        TemplateServicesDataGrid?.Items.Refresh();

        TemplateStatusTextBlock.Text = $"Base ref оновлено для {row.ServiceName}: {row.BaselineRef}. Перед AI notes натисни Save → Scan.";
    }

    
private void PickTargetRefs_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is not ReleaseTemplateService row)
            return;

        var options = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in row.TargetRefOptions ?? [])
            AddTargetRefPickerValues(options, value);

        AddTargetRefPickerValues(options, row.BaselineRef);
        AddTargetRefPickerValues(options, row.TargetBranch);
        AddTargetRefPickerValues(options, TemplateDefaultBranchTextBox?.Text);

        if (options.Count == 0)
            options.Add("origin/dev");

        var selectedRefs = SplitTargetRefsForPicker(row.TargetBranch).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var picked = ShowTargetRefsPickerDialog(row.ServiceName, options, selectedRefs);

        if (picked is null)
            return;

        row.TargetBranch = string.Join("; ", picked);

        // RNH_TEMPLATE_AUTO_INCLUDE_REF_ROWS_028: editing refs means the service is intended for scan.
        row.Included = true;

        foreach (var targetRef in picked)
            options.Add(targetRef);

        row.TargetRefOptions = options.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        TemplateServicesDataGrid?.Items.Refresh();

        TemplateStatusTextBlock.Text = $"Target refs оновлено для {row.ServiceName}: {row.TargetBranch}. Сервіс автоматично включено в scan. Перед AI notes натисни Save → Scan.";
    }



    
    private string? ShowBaseRefPickerDialog(string serviceName, IEnumerable<string> options, string? selectedRef)
    {
        var window = new System.Windows.Window
        {
            Title = $"Base ref: {serviceName}",
            Width = 640,
            Height = 640,
            MinWidth = 520,
            MinHeight = 420,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ResizeMode = System.Windows.ResizeMode.CanResize,
            Owner = System.Windows.Window.GetWindow(this)
        };

        var root = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(12)
        };

        var header = new System.Windows.Controls.TextBlock
        {
            Text = "Вибери початковий base ref. Теги та гілки згруповані окремо. Base ref може бути tag, branch або SHA.",
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10)
        };
        System.Windows.Controls.DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        root.Children.Add(header);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(0, 10, 0, 0)
        };

        var okButton = new System.Windows.Controls.Button { Content = "OK", MinWidth = 90, Margin = new System.Windows.Thickness(0, 0, 8, 0) };
        var cancelButton = new System.Windows.Controls.Button { Content = "Скасувати", MinWidth = 90 };

        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(buttons);

        var manualPanel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 8, 0, 0)
        };

        manualPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Або введи вручну:",
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        });

        var manualTextBox = new System.Windows.Controls.TextBox
        {
            MinHeight = 26,
            Text = selectedRef?.Trim() ?? "",
            ToolTip = "Можна вказати tag, branch або SHA"
        };
        manualPanel.Children.Add(manualTextBox);
        System.Windows.Controls.DockPanel.SetDock(manualPanel, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(manualPanel);

        var stack = new System.Windows.Controls.StackPanel();
        var radioButtons = new List<System.Windows.Controls.RadioButton>();

        foreach (var group in BuildGroupedRefOptions(options))
        {
            var groupStack = new System.Windows.Controls.StackPanel();

            foreach (var value in group.Items)
            {
                var radioButton = new System.Windows.Controls.RadioButton
                {
                    Content = value,
                    IsChecked = string.Equals(value, selectedRef?.Trim(), StringComparison.OrdinalIgnoreCase),
                    GroupName = "BaseRefPicker",
                    Margin = new System.Windows.Thickness(0, 2, 0, 2)
                };

                radioButton.Checked += (_, _) =>
                {
                    manualTextBox.Text = radioButton.Content?.ToString() ?? "";
                };

                radioButton.MouseDoubleClick += (_, _) =>
                {
                    manualTextBox.Text = radioButton.Content?.ToString() ?? "";
                    window.DialogResult = true;
                    window.Close();
                };

                radioButtons.Add(radioButton);
                groupStack.Children.Add(radioButton);
            }

            var expander = new System.Windows.Controls.Expander
            {
                Header = $"{group.Name} ({group.Items.Count})",
                IsExpanded = true,
                Margin = new System.Windows.Thickness(0, 0, 0, 8),
                Content = groupStack
            };

            stack.Children.Add(expander);
        }

        var scrollViewer = new System.Windows.Controls.ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        root.Children.Add(scrollViewer);

        string? result = null;

        okButton.Click += (_, _) =>
        {
            var picked = manualTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(picked))
            {
                System.Windows.MessageBox.Show(
                    window,
                    "Вибери base ref зі списку або введи вручну.",
                    "Base ref",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            result = picked;
            window.DialogResult = true;
            window.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            window.DialogResult = false;
            window.Close();
        };

        window.Content = root;
        window.ShowDialog();

        if (window.DialogResult == true && result is null)
        {
            result = radioButtons
                .Where(x => x.IsChecked == true)
                .Select(x => x.Content?.ToString())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        }

        return result;
    }


    
    private List<string>? ShowTargetRefsPickerDialog(string serviceName, IEnumerable<string> options, ISet<string> selectedRefs)
    {
        var window = new System.Windows.Window
        {
            Title = $"Target refs: {serviceName}",
            Width = 640,
            Height = 640,
            MinWidth = 520,
            MinHeight = 420,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            ResizeMode = System.Windows.ResizeMode.CanResize,
            Owner = System.Windows.Window.GetWindow(this)
        };

        var root = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(12)
        };

        var header = new System.Windows.Controls.TextBlock
        {
            Text = "Познач target refs. Теги та гілки згруповані окремо. Для незмерджених гілок можна вибрати кілька refs.",
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10)
        };
        System.Windows.Controls.DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        root.Children.Add(header);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(0, 10, 0, 0)
        };

        var selectAllButton = new System.Windows.Controls.Button { Content = "Всі", MinWidth = 80, Margin = new System.Windows.Thickness(0, 0, 8, 0) };
        var clearButton = new System.Windows.Controls.Button { Content = "Жодна", MinWidth = 80, Margin = new System.Windows.Thickness(0, 0, 16, 0) };
        var okButton = new System.Windows.Controls.Button { Content = "OK", MinWidth = 90, Margin = new System.Windows.Thickness(0, 0, 8, 0) };
        var cancelButton = new System.Windows.Controls.Button { Content = "Скасувати", MinWidth = 90 };

        buttons.Children.Add(selectAllButton);
        buttons.Children.Add(clearButton);
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(buttons);

        var manualPanel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 8, 0, 0)
        };

        manualPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Додатково вручну, якщо ref/SHA ще немає у списку:",
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        });

        var manualTextBox = new System.Windows.Controls.TextBox
        {
            MinHeight = 26,
            ToolTip = "Можна вказати кілька значень через ; або ,"
        };
        manualPanel.Children.Add(manualTextBox);
        System.Windows.Controls.DockPanel.SetDock(manualPanel, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(manualPanel);

        var stack = new System.Windows.Controls.StackPanel();
        var checkBoxes = new List<System.Windows.Controls.CheckBox>();

        foreach (var group in BuildGroupedRefOptions(options))
        {
            var groupStack = new System.Windows.Controls.StackPanel();

            foreach (var targetRef in group.Items)
            {
                var checkBox = new System.Windows.Controls.CheckBox
                {
                    Content = targetRef,
                    IsChecked = selectedRefs.Contains(targetRef),
                    Margin = new System.Windows.Thickness(0, 2, 0, 2)
                };

                checkBoxes.Add(checkBox);
                groupStack.Children.Add(checkBox);
            }

            var expander = new System.Windows.Controls.Expander
            {
                Header = $"{group.Name} ({group.Items.Count})",
                IsExpanded = true,
                Margin = new System.Windows.Thickness(0, 0, 0, 8),
                Content = groupStack
            };

            stack.Children.Add(expander);
        }

        var scrollViewer = new System.Windows.Controls.ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
        };
        root.Children.Add(scrollViewer);

        List<string>? result = null;

        selectAllButton.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
                checkBox.IsChecked = true;
        };

        clearButton.Click += (_, _) =>
        {
            foreach (var checkBox in checkBoxes)
                checkBox.IsChecked = false;
            manualTextBox.Text = "";
        };

        okButton.Click += (_, _) =>
        {
            var picked = new List<string>();

            foreach (var value in checkBoxes
                .Where(x => x.IsChecked == true)
                .Select(x => x.Content?.ToString() ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                if (!picked.Contains(value, StringComparer.OrdinalIgnoreCase))
                    picked.Add(value);
            }

            foreach (var value in SplitTargetRefsForPicker(manualTextBox.Text))
            {
                if (!picked.Contains(value, StringComparer.OrdinalIgnoreCase))
                    picked.Add(value);
            }

            if (picked.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    window,
                    "Вибери хоча б один target ref або натисни «Скасувати».",
                    "Target refs",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            result = picked;
            window.DialogResult = true;
            window.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            window.DialogResult = false;
            window.Close();
        };

        window.Content = root;
        window.ShowDialog();

        return result;
    }



    private static IReadOnlyList<RefOptionGroup> BuildGroupedRefOptions(IEnumerable<string> options)
    {
        var groups = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tags"] = new(StringComparer.OrdinalIgnoreCase),
            ["Branches"] = new(StringComparer.OrdinalIgnoreCase),
            ["Other / SHA"] = new(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var option in options.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var value = option.Trim();
            groups[GetRefGroupName(value)].Add(value);
        }

        return groups
            .Where(x => x.Value.Count > 0)
            .Select(x => new RefOptionGroup
            {
                Name = x.Key,
                Items = x.Value.ToList()
            })
            .ToList();
    }

    private static string GetRefGroupName(string value)
    {
        var normalized = value.Trim();

        if (normalized.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase))
        {
            return "Tags";
        }

        if (normalized.StartsWith("branch:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("origin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("refs/remotes/", StringComparison.OrdinalIgnoreCase))
        {
            return "Branches";
        }

        return "Other / SHA";
    }

    private sealed class RefOptionGroup
    {
        public string Name { get; init; } = "";
        public List<string> Items { get; init; } = [];
    }

    private static void AddTargetRefPickerValues(ISet<string> values, string? text)
    {
        foreach (var targetRef in SplitTargetRefsForPicker(text))
            values.Add(targetRef);
    }

    private static IEnumerable<string> SplitTargetRefsForPicker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var normalized = text
            .Replace("\r", "\n")
            .Replace(";", "\n")
            .Replace(",", "\n");

        foreach (var raw in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = raw.Trim();

            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

        private void RefreshTargetRefOptions()
    {
        var values = BuildKnownTargetRefFallbacks();

        TargetRefOptions.Clear();

        foreach (var value in values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            TargetRefOptions.Add(value);

        foreach (var row in _rows)
            ApplyKnownTargetRefOptions(row, values);

        TemplateServicesDataGrid?.Items.Refresh();

        // Real branch options are per service and come from that service local Git repository.
        _ = RefreshTargetRefOptionsFromLocalGitAsync();
    }
    // RNH_SERVICE_TARGET_REFS_REAL_GIT_2026_06_19: per-service target ref options.
    private SortedSet<string> BuildKnownTargetRefFallbacks()
    {
        // RNH_TEMPLATE_WORKFLOW_POLISH_2026_06_19:
        // Keep fallback options minimal. Real dropdown options are loaded per service from that service local Git repository.
        var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        AddTargetRefsTo(values, TemplateDefaultBranchTextBox?.Text);

        if (values.Count == 0)
            values.Add("origin/dev");

        return values;
    }

    private void ApplyKnownTargetRefOptions(ReleaseTemplateService row, IEnumerable<string> fallbackValues)
    {
        var values = new SortedSet<string>(fallbackValues, StringComparer.OrdinalIgnoreCase);
        var catalogService = FindCatalogService(row.ServiceName, "");

        AddTargetRefsTo(values, row.BaselineRef);
        AddTargetRefsTo(values, row.TargetBranch);
        AddTargetRefsTo(values, catalogService?.SelectedBranch);
        AddTargetRefsTo(values, TemplateDefaultBranchTextBox?.Text);

        if (values.Count == 0)
            values.Add("origin/dev");

        row.TargetRefOptions = values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task RefreshTargetRefOptionsFromLocalGitAsync()
    {
        if (_isRefreshingTargetRefsFromGit || _rows.Count == 0)
            return;

        _isRefreshingTargetRefsFromGit = true;

        try
        {
            var reposRoot = await GetRepositoriesRootAsync();
            var updatedRows = 0;
            var scannedRepos = 0;

            foreach (var row in _rows.ToList())
            {
                var values = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var catalogService = FindCatalogService(row.ServiceName, "");

                AddTargetRefsTo(values, row.BaselineRef);
                AddTargetRefsTo(values, row.TargetBranch);
                AddTargetRefsTo(values, catalogService?.SelectedBranch);
                AddTargetRefsTo(values, TemplateDefaultBranchTextBox?.Text);

                var localRepoPath = Path.Combine(reposRoot, SanitizeFolderName(row.ServiceName));

                if (Directory.Exists(Path.Combine(localRepoPath, ".git")))
                {
                    scannedRepos++;

                    try
                    {
                        var branchesOutput = await RunGitAsync(
                            localRepoPath,
                            "for-each-ref",
                            "--format=%(refname:short)",
                            "refs/remotes/origin");

                        foreach (var branch in SplitTargetRefOptionLines(branchesOutput))
                        {
                            if (string.Equals(branch, "origin", StringComparison.OrdinalIgnoreCase) ||
                                branch.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            values.Add(branch);
                        }

                        var tagsOutput = await RunGitAsync(
                            localRepoPath,
                            "for-each-ref",
                            "--sort=-creatordate",
                            "--format=%(refname:short)",
                            "refs/tags");

                        foreach (var tag in SplitTargetRefOptionLines(tagsOutput))
                        {
                            if (string.IsNullOrWhiteSpace(tag))
                                continue;

                            values.Add(ToTemplateTagDisplayRef(tag));
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning($"Target refs were not read from local Git. Service='{row.ServiceName}', error='{CompactLogMessage(ex.Message)}'.");
                    }
                }

                if (values.Count == 0)
                    values.Add("origin/dev");

                var newOptions = values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

                if (!newOptions.SequenceEqual(row.TargetRefOptions ?? [], StringComparer.OrdinalIgnoreCase))
                {
                    row.TargetRefOptions = newOptions;
                    updatedRows++;
                }
            }

            if (updatedRows > 0)
            {
                TemplateServicesDataGrid?.Items.Refresh();
                AppLog.Info($"Target refs refreshed from local Git repositories. Repositories scanned={scannedRepos}, rows updated={updatedRows}.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Target refs refresh failed: {CompactLogMessage(ex.Message)}");
        }
        finally
        {
            _isRefreshingTargetRefsFromGit = false;
        }
    }

    private static IEnumerable<string> SplitTargetRefOptionLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var raw in text.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = raw.Trim();

            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }



    private static void AddTargetRefsTo(ISet<string> values, string? text)
    {
        foreach (var targetRef in SplitTargetRefs(text))
            values.Add(targetRef);
    }

    private static IEnumerable<string> SplitTargetRefs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var normalized = text
            .Replace("\r", "\n")
            .Replace(";", "\n")
            .Replace(",", "\n");

        foreach (var raw in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = raw.Trim();

            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }




    private static string GetEffectiveProjectName(string? projectName, string? templateName, string? releaseName)
    {
        if (!string.IsNullOrWhiteSpace(projectName))
            return projectName.Trim();

        return InferProjectName(templateName, releaseName);
    }

    private string GetTemplateProjectName()
    {
        var value = TemplateProjectTextBox?.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        return InferProjectName(TemplateNameTextBox?.Text, ReleaseNameTextBox?.Text);
    }

    private static string GetReleaseProjectName(Release release)
    {
        if (!string.IsNullOrWhiteSpace(release.ProjectName))
            return release.ProjectName.Trim();

        return InferProjectName(release.TemplateName, release.Name);
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

    private void LoadTemplateToUi(ReleaseTemplate template)
    {
        TemplateNameTextBox.Text = template.Name;
        TemplateProjectTextBox.Text = string.IsNullOrWhiteSpace(template.ProjectName)
            ? InferProjectName(template.Name, template.LastReleaseName)
            : template.ProjectName;
        TemplateDescriptionTextBox.Text = template.Description;
        TemplateReleaseNameTextBox.Text = string.IsNullOrWhiteSpace(template.DefaultReleaseName)
            ? $"{template.Name} {{version}}"
            : template.DefaultReleaseName;
        TemplateDefaultBranchTextBox.Text = string.IsNullOrWhiteSpace(template.DefaultTargetBranch)
            ? "origin/dev"
            : template.DefaultTargetBranch;
        if (RunTargetRefTextBox != null)
            RunTargetRefTextBox.Text = TemplateDefaultBranchTextBox.Text;

        // RNH_TEMPLATE_WORKFLOW_POLISH_2026_06_19: restore the last draft/run values for this template.
        var storedVersion = template.LastReleaseVersion?.Trim() ?? "";
        var storedReleaseName = template.LastReleaseName?.Trim() ?? "";

        if (ReleaseVersionTextBox != null)
            ReleaseVersionTextBox.Text = storedVersion;

        if (ReleaseNameTextBox != null)
        {
            if (!string.IsNullOrWhiteSpace(storedReleaseName))
                ReleaseNameTextBox.Text = storedReleaseName;
            else if (!string.IsNullOrWhiteSpace(storedVersion))
                ReleaseNameTextBox.Text = BuildReleaseName(TemplateReleaseNameTextBox.Text, template.Name, storedVersion);
        }

        SelectComboBoxItemByContent(ComparisonBaseComboBox, template.LastComparisonBaseMode);
        SelectComboBoxItemByContent(DivergedHistoryDiffModeComboBox, template.LastDivergedHistoryDiffMode);

        _rows.Clear();

        foreach (var row in template.Services.OrderBy(x => x.ServiceName))
        {
            EnsureRowDefaults(row);
            _rows.Add(row);
        }


        // RNH_SERVICE_TARGET_REFS_V2_LOAD_TEMPLATE_REFRESH
        RefreshTargetRefOptions();

        TemplateStatusTextBlock.Text = $"Завантажено шаблон: {template.Name}";
        TemplateStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        LoadLatestTemplateScopeResults(template.Name);
    }

    private void NewTemplate_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        NewTemplateFromCatalog("New template", includeActive: false);
    }

    private void CreateFromActiveServices_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        NewTemplateFromCatalog("RS.Core Poruch", includeActive: true);
    }

    private void CreateFromBaseline_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var baseline = FindLatestBaseline();

        if (baseline == null)
        {
            System.Windows.MessageBox.Show(
                "Не знайдено baseline у releases.json. Спочатку імпортуйте baseline з інсталятора та збережіть реліз.",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        TemplateNameTextBox.Text = string.IsNullOrWhiteSpace(baseline.TemplateName)
            ? baseline.Name
            : baseline.TemplateName;

        TemplateDescriptionTextBox.Text = $"Шаблон створено з baseline: {baseline.Name}";
        TemplateProjectTextBox.Text = GetReleaseProjectName(baseline);
        TemplateReleaseNameTextBox.Text = $"{TemplateNameTextBox.Text} {{version}}";
        TemplateDefaultBranchTextBox.Text = "origin/dev";

        _rows.Clear();

        foreach (var releaseService in baseline.Services.OrderBy(x => x.ServiceName))
        {
            var service = FindCatalogService(releaseService.ServiceName, releaseService.ImageName);
            var status = service?.ValidationStatus ?? releaseService.Status;
            var isExternal = IsIgnoredOrExternal(status);

            var row = new ReleaseTemplateService
            {
                Included = !isExternal && (service?.IsActive ?? true),
                ServiceName = service?.Name ?? releaseService.ServiceName,
                GitUrl = service?.GitUrl ?? releaseService.GitUrl,
                ValidationStatus = string.IsNullOrWhiteSpace(status) ? "Unknown" : status,
                TargetBranch = !string.IsNullOrWhiteSpace(service?.SelectedBranch)
                    ? service.SelectedBranch
                    : "origin/dev",
                AskIfChanged = true,
                BaselineVersion = releaseService.SourceVersion,
                BaselineRef = releaseService.TargetRef,
                BaselineSha = releaseService.TargetSha,
                Notes = ""
            };

            EnsureRowDefaults(row);
            _rows.Add(row);
        }

        // RNH_SERVICE_TARGET_REFS_V2_CREATE_BASELINE_REFRESH
        RefreshTargetRefOptions();

        TemplateStatusTextBlock.Text = $"Створено шаблон з baseline: {baseline.Name}. Перевір галочки та натисни «Зберегти шаблон».";
        TemplateStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
    }

    private void NewTemplateFromCatalog(string name, bool includeActive)
    {
        TemplatesListBox.SelectedItem = null;

        TemplateNameTextBox.Text = name;
        TemplateProjectTextBox.Text = InferProjectName(name, name);
        TemplateDescriptionTextBox.Text = "";
        TemplateReleaseNameTextBox.Text = $"{name} {{version}}";
        TemplateDefaultBranchTextBox.Text = "origin/dev";

        _rows.Clear();

        foreach (var service in _services.OrderBy(x => x.Name))
        {
            var status = GetEffectiveStatus(service);
            var isExternal = IsIgnoredOrExternal(status);

            var row = new ReleaseTemplateService
            {
                Included = includeActive && service.IsActive && !isExternal,
                ServiceName = service.Name,
                GitUrl = service.GitUrl,
                ValidationStatus = status,
                TargetBranch = string.IsNullOrWhiteSpace(service.SelectedBranch)
                    ? "origin/dev"
                    : service.SelectedBranch,
                AskIfChanged = true,
                BaselineVersion = service.InstallerVersion,
                BaselineRef = service.BaseTag,
                Notes = ""
            };

            EnsureRowDefaults(row);
            _rows.Add(row);
        }

        // RNH_SERVICE_TARGET_REFS_V2_NEW_TEMPLATE_REFRESH
        RefreshTargetRefOptions();

        TemplateStatusTextBlock.Text = includeActive
            ? "Створено чернетку з активних сервісів."
            : "Створено порожню чернетку шаблону.";
        TemplateStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
    }

    private void IncludeActive_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.Included = !IsIgnoredOrExternal(row.ValidationStatus) &&
                           !string.Equals(row.ValidationStatus, "Needs Git URL", StringComparison.OrdinalIgnoreCase);
        }

        TemplateServicesDataGrid.Items.Refresh();
        TemplateStatusTextBlock.Text = "Включено валідні активні сервіси. Сервіси без Git URL залишені вимкненими.";
    }

    private void ClearIncluded_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        foreach (var row in _rows)
            row.Included = false;

        TemplateServicesDataGrid.Items.Refresh();
        TemplateStatusTextBlock.Text = "Усі сервіси вимкнено.";
    }

    private void RefreshRowsFromCatalog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var existingByName = _rows
            .GroupBy(x => NormalizeKey(x.ServiceName))
            .ToDictionary(x => x.Key, x => x.First());

        foreach (var service in _services.OrderBy(x => x.Name))
        {
            var key = NormalizeKey(service.Name);
            var status = GetEffectiveStatus(service);

            if (existingByName.TryGetValue(key, out var existing))
            {
                existing.GitUrl = service.GitUrl;
                existing.ValidationStatus = status;
                existing.TargetBranch = string.IsNullOrWhiteSpace(existing.TargetBranch)
                    ? (string.IsNullOrWhiteSpace(service.SelectedBranch) ? "origin/dev" : service.SelectedBranch)
                    : existing.TargetBranch;
                existing.BaselineVersion = string.IsNullOrWhiteSpace(existing.BaselineVersion)
                    ? service.InstallerVersion
                    : existing.BaselineVersion;
                existing.BaselineRef = string.IsNullOrWhiteSpace(existing.BaselineRef)
                    ? service.BaseTag
                    : existing.BaselineRef;
            }
            else
            {
                _rows.Add(new ReleaseTemplateService
                {
                    Included = false,
                    ServiceName = service.Name,
                    GitUrl = service.GitUrl,
                    ValidationStatus = status,
                    TargetBranch = string.IsNullOrWhiteSpace(service.SelectedBranch) ? "origin/dev" : service.SelectedBranch,
                    AskIfChanged = true,
                    BaselineVersion = service.InstallerVersion,
                    BaselineRef = service.BaseTag
                });
            }
        }

        TemplateServicesDataGrid.Items.Refresh();
        // RNH_SERVICE_TARGET_REFS_V2_REFRESH_CATALOG_REFRESH
        RefreshTargetRefOptions();
        TemplateStatusTextBlock.Text = "Список сервісів оновлено з довідника.";
    }

    private async void SaveTemplate_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await SaveCurrentTemplateAsync(showMessage: true);
    }

    private async Task<ReleaseTemplate?> SaveCurrentTemplateAsync(bool showMessage)
    {
        var name = TemplateNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            System.Windows.MessageBox.Show(
                "Вкажіть назву шаблону.",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return null;
        }

        CommitTemplateGridEdits();

        var selectedTemplate = TemplatesListBox?.SelectedItem as ReleaseTemplate;



        // If the user edits the name of the currently selected template, save it as a rename.


        // Do not create a second template just because the name changed.


        var existing = selectedTemplate != null && _templates.Any(x => ReferenceEquals(x, selectedTemplate))


            ? selectedTemplate


            : null;



        if (existing == null && selectedTemplate != null && !string.IsNullOrWhiteSpace(selectedTemplate.Id))


        {


            existing = _templates.FirstOrDefault(x =>


                string.Equals(x.Id, selectedTemplate.Id, StringComparison.OrdinalIgnoreCase));


        }



        if (existing == null)


        {


            existing = _templates.FirstOrDefault(x =>


                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));


        }


        else


        {


            var duplicateName = _templates.FirstOrDefault(x =>


                !ReferenceEquals(x, existing) &&


                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));



            if (duplicateName != null)


            {


                System.Windows.MessageBox.Show(


                    $"Шаблон із назвою '{name}' вже існує. Вкажіть іншу назву або виберіть існуючий шаблон зі списку.",


                    "Release Notes Helper",


                    System.Windows.MessageBoxButton.OK,


                    System.Windows.MessageBoxImage.Warning);


                return null;


            }


        }



        if (existing == null)


        {


            existing = new ReleaseTemplate


            {


                Id = BuildSafeId(name),


                CreatedAt = DateTime.Now


            };



            _templates.Add(existing);


        }

        if (string.IsNullOrWhiteSpace(existing.Id))
            existing.Id = BuildSafeId(name);

        existing.Name = name;
        existing.ProjectName = GetTemplateProjectName();
        existing.Description = TemplateDescriptionTextBox.Text.Trim();
        existing.DefaultReleaseName = TemplateReleaseNameTextBox.Text.Trim();
        existing.DefaultTargetBranch = string.IsNullOrWhiteSpace(TemplateDefaultBranchTextBox.Text)
            ? "origin/dev"
            : TemplateDefaultBranchTextBox.Text.Trim();
        existing.LastReleaseVersion = ReleaseVersionTextBox?.Text?.Trim() ?? "";
        existing.LastReleaseName = ReleaseNameTextBox?.Text?.Trim() ?? "";
        existing.LastComparisonBaseMode = "Auto per service";
        existing.LastDivergedHistoryDiffMode = GetDivergedHistoryDiffMode();
        existing.UpdatedAt = DateTime.Now;

        existing.Services = _rows
            .Select(CloneRow)
            .OrderByDescending(x => x.Included)
            .ThenBy(x => x.ServiceName)
            .ToList();

        var savedTemplateId = existing.Id;

        await _store.SaveAsync("templates.json", _templates);
        RefreshTemplateProjectFilter();
        RefreshTemplatesList();
        TemplatesListBox.SelectedItem = _templates.FirstOrDefault(x =>
            string.Equals(x.Id, savedTemplateId, StringComparison.OrdinalIgnoreCase));

        var included = existing.Services.Count(x => x.Included);

        AppLog.Info($"Template saved. Template='{existing.Name}', included={included}, total={existing.Services.Count}.");

        TemplateStatusTextBlock.Text = $"Шаблон збережено. Включено сервісів: {included}.";
        TemplateStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;

        if (showMessage)
        {
            System.Windows.MessageBox.Show(
                $"Шаблон збережено.\n\nВключено сервісів: {included}",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        return existing;
    }

    private async void DeleteTemplate_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var name = TemplateNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
            return;

        var template = _templates.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        if (template == null)
            return;

        var result = System.Windows.MessageBox.Show(
            $"Видалити шаблон?\n\n{name}",
            "Release Notes Helper",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes)
            return;

        _templates.Remove(template);
        await _store.SaveAsync("templates.json", _templates);

        RefreshTemplatesList();
        NewTemplateFromCatalog("New template", includeActive: false);
    }

    private void ApplyRunTargetToIncluded_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        CommitTemplateGridEdits();

        var targetRef = RunTargetRefTextBox?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(targetRef))
        {
            System.Windows.MessageBox.Show(
                "Вкажи target ref, наприклад origin/dev, origin/release/1.5.1 або конкретний SHA/tag.",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var updated = 0;
        foreach (var row in _rows.Where(x => x.Included))
        {
            row.TargetBranch = targetRef;
            updated++;
        }

        TemplateServicesDataGrid.Items.Refresh();
        // RNH_SERVICE_TARGET_REFS_V2_APPLY_OVERRIDE_REFRESH
        RefreshTargetRefOptions();
        TemplateStatusTextBlock.Text = $"Target ref '{targetRef}' записано у включені сервіси: {updated}. Натисни «Зберегти шаблон», щоб зафіксувати в templates.json.";
        TemplateStatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
        AppLog.Info($"Run target ref applied to included template services. targetRef='{targetRef}', services={updated}.");
    }

    private string GetRunTargetRefMode()
    {
        if (RunTargetRefModeComboBox?.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            return item.Content?.ToString() ?? "З шаблону по сервісах";

        return "З шаблону по сервісах";
    }

    private string GetRunTargetRefOverride(string targetRefMode)
    {
        if (string.IsNullOrWhiteSpace(targetRefMode))
            return "";

        if (!targetRefMode.StartsWith("Один target ref", StringComparison.OrdinalIgnoreCase))
            return "";

        return RunTargetRefTextBox?.Text?.Trim() ?? "";
    }

    private string GetDivergedHistoryDiffMode()
    {
        // Hidden from UI. Auto mode is the safest default:
        // if previous SHA is not an ancestor of target, use merge-base..target.
        return "Auto: якщо diverged — merge-base..target";
    }

    private string ResolveTargetRefForScan(ReleaseTemplateService row, string runTargetRefOverride)
    {
        if (!string.IsNullOrWhiteSpace(runTargetRefOverride))
            return runTargetRefOverride.Trim();

        if (!string.IsNullOrWhiteSpace(row.TargetBranch))
            return row.TargetBranch.Trim();

        return GetDefaultBranch();
    }

    private async void RunTemplateScope_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        CommitTemplateGridEdits();

        var template = await SaveCurrentTemplateAsync(showMessage: false);
        if (template == null)
            return;

        var includedRows = template.Services
            .Where(x => x.Included)
            .Where(x => !IsIgnoredOrExternal(x.ValidationStatus))
            .ToList();

        if (includedRows.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "У шаблоні немає включених сервісів.",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var releaseVersion = ReleaseVersionTextBox.Text.Trim();
        var releaseName = ReleaseNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(releaseName))
            releaseName = BuildReleaseName(template.DefaultReleaseName, template.Name, releaseVersion);

        var comparisonBase = GetComparisonBaseMode();
        var targetRefMode = GetRunTargetRefMode();
        var runTargetRefOverride = GetRunTargetRefOverride(targetRefMode);
        var divergedHistoryDiffMode = GetDivergedHistoryDiffMode();
        const bool usePreviousReleaseServiceSha = false;

        // RNH_AUTO_BASELINE_REMOVE_DIVERGED_UI_030:
        // Baseline is selected per service automatically:
        // Base ref -> installer baseline SHA -> previous release -> baseline version.
        var previousRelease = FindLatestBaseline()
            ?? FindPreviousReleaseForTemplate(template.Name)
            ?? new Release
            {
                Name = "No saved baseline",
                TemplateName = template.Name,
                Type = "Auto"
            };

        // RNH_SCAN_PREVIEW_DEDUP_CONFIRM
        var confirmMessage = BuildTemplateScanPreviewMessage(
            releaseName,
            releaseVersion,
            comparisonBase,
            previousRelease,
            includedRows,
            runTargetRefOverride,
            divergedHistoryDiffMode);

        var confirm = System.Windows.MessageBox.Show(
            confirmMessage,
            "Перевірка scope перед прогоном",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Information);

        if (confirm != System.Windows.MessageBoxResult.OK)
        {
            AppLog.Warning($"Template scan cancelled before start. Template='{template.Name}', release='{releaseName}'.");
            return;
        }

        AppLog.Info($"Template scan queued. Template='{template.Name}', release='{releaseName}', comparison='{comparisonBase}', targetRefMode='{targetRefMode}', targetOverride='{(string.IsNullOrWhiteSpace(runTargetRefOverride) ? "n/a" : runTargetRefOverride)}', services={includedRows.Count}, fallbackBase='{previousRelease.Name}'.");

        try
        {
            SetBusy(true, "Прогін шаблону виконується...");
            _scopeResults.Clear();
            AppLog.Info($"Template scan started. Release='{releaseName}'.");

            var reposRoot = await GetRepositoriesRootAsync();
            Directory.CreateDirectory(reposRoot);
            AppLog.Info("Repositories root: " + reposRoot);

            foreach (var row in includedRows.OrderBy(x => x.ServiceName))
            {
                var resolvedTargetRef = ResolveTargetRefForScan(row, runTargetRefOverride);
                AppLog.Info($"Scanning service '{row.ServiceName}'. Target='{resolvedTargetRef}'.");
                var result = await ScanServiceAsync(row, previousRelease, reposRoot, usePreviousReleaseServiceSha, runTargetRefOverride, divergedHistoryDiffMode);

                if (string.Equals(result.Status, "Error", StringComparison.OrdinalIgnoreCase))
                    AppLog.Error($"Service scan failed. Service='{result.ServiceName}', error='{CompactLogMessage(result.ErrorMessage)}'.");
                else
                    AppLog.Info($"Service scan completed. Service='{result.ServiceName}', status='{result.Status}', commits={result.CommitCount}.");

                if (result.CommitCount > 0 &&
                    string.Equals(result.Status, "Changed", StringComparison.OrdinalIgnoreCase) &&
                    row.AskIfChanged)
                {
                    var answer = AskIncludeChangedService(result);

                    if (answer == System.Windows.MessageBoxResult.Cancel)
                    {
                        result.Included = false;
                        result.Status = "Cancelled";
                        result.ErrorMessage = "Прогін зупинено користувачем.";
                        AppLog.Warning($"Template scan cancelled by user while reviewing service '{result.ServiceName}'.");
                        _scopeResults.Add(result);
                        break;
                    }

                    if (answer == System.Windows.MessageBoxResult.Yes)
                    {
                        result.Included = true;
                        result.Status = "Included";
                        AppLog.Info($"Service included by user. Service='{result.ServiceName}', commits={result.CommitCount}.");
                    }
                    else
                    {
                        result.Included = false;
                        result.Status = "Skipped";
                        AppLog.Info($"Service skipped by user. Service='{result.ServiceName}', commits={result.CommitCount}.");
                    }
                }
                else if (result.CommitCount > 0 && string.Equals(result.Status, "Changed", StringComparison.OrdinalIgnoreCase))
                {
                    result.Included = true;
                    result.Status = "Included";
                    AppLog.Info($"Service included automatically. Service='{result.ServiceName}', commits={result.CommitCount}.");
                }

                _scopeResults.Add(result);
                ScopeResultsDataGrid.Items.Refresh();
            }

            // RNH DATED SNAPSHOT NO CHANGES GUARD
            var scanCompletedAt = DateTime.Now;
            var changedCountBeforeSave = _scopeResults.Count(x => x.CommitCount > 0);
            var includedCountBeforeSave = _scopeResults.Count(x => x.Included && x.CommitCount > 0);
            var errorsCountBeforeSave = _scopeResults.Count(x => string.Equals(x.Status, "Error", StringComparison.OrdinalIgnoreCase));

            if (changedCountBeforeSave == 0)
            {
                var saveEmpty = System.Windows.MessageBox.Show(
                    $"Змін не знайдено станом на {scanCompletedAt:yyyy-MM-dd HH:mm}.\n\nНе створювати новий реліз зазвичай правильніше.\n\nЗберегти порожній snapshot прогону для аудиту?",
                    "Змін не знайдено",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Information);

                if (saveEmpty != System.Windows.MessageBoxResult.Yes)
                {
                    SetBusy(false, $"Змін не знайдено станом на {scanCompletedAt:yyyy-MM-dd HH:mm}. Новий реліз не створено.");
                    System.Windows.MessageBox.Show(
                        "Змін не знайдено. Новий реліз не створено.",
                        "Release Notes Helper",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }
            }
            else if (includedCountBeforeSave == 0)
            {
                var saveWithoutIncluded = System.Windows.MessageBox.Show(
                    $"Зміни знайдено, але жоден сервіс не включено в реліз станом на {scanCompletedAt:yyyy-MM-dd HH:mm}.\n\nЗберегти snapshot без включених змін?",
                    "Немає включених змін",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);

                if (saveWithoutIncluded != System.Windows.MessageBoxResult.Yes)
                {
                    SetBusy(false, "Жоден сервіс не включено. Новий реліз не створено.");
                    return;
                }
            }

            var release = new Release
            {
                Id = $"{BuildSafeId(releaseName)}_{DateTime.Now:yyyyMMdd_HHmmss}",
                Name = releaseName,
                Version = releaseVersion,
                TemplateName = template.Name,
                ProjectName = GetTemplateProjectName(),
                Type = "Regular",
                PreviousReleaseId = previousRelease.Id,
                Source = "TemplateScan",
                CreatedAt = scanCompletedAt,
                BuiltAt = scanCompletedAt,
                ScopeGeneratedAt = scanCompletedAt,
                OutputFolderName = BuildReleaseOutputFolderName(releaseName, scanCompletedAt),
                HasChanges = changedCountBeforeSave > 0,
                ChangedServicesCount = changedCountBeforeSave,
                IncludedServicesCount = includedCountBeforeSave,
                Services = _scopeResults.ToList()
            };

            _releases.Add(release);
            _lastTemplateScanReleaseId = release.Id;
            await _store.SaveAsync("releases.json", _releases);
            await WriteReleaseScopeFilesAsync(release, previousRelease);

            var releaseOutputPath = await GetReleaseOutputPathAsync(release);
            AppLog.Info($"Scope files written. Release='{release.Name}', output='{releaseOutputPath}'.");

            var changedCount = release.Services.Count(x => x.CommitCount > 0);
            var includedCount = release.Services.Count(x => x.Included && x.CommitCount > 0);
            var errorsCount = release.Services.Count(x => string.Equals(x.Status, "Error", StringComparison.OrdinalIgnoreCase));

            AppLog.Info($"Template scan completed. Release='{release.Name}', changed={changedCount}, included={includedCount}, errors={errorsCount}.");

            SetBusy(false, $"Прогін завершено. Baseline: Auto per service. Змінені сервіси: {changedCount}, включено: {includedCount}, помилок: {errorsCount}. Реліз збережено в releases.json.");

            // RNH_SCAN_PREVIEW_DEDUP_COMPLETION
            var completionPreview = BuildScopePreviewMessage(release, comparisonBase, previousRelease.Name, changedCount, includedCount, errorsCount);
            System.Windows.MessageBox.Show(
                completionPreview,
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("Template scan failed: " + CompactLogMessage(ex.Message));
            SetBusy(false, $"Помилка прогону шаблону: {ex.Message}");

            System.Windows.MessageBox.Show(
                ex.Message,
                "Template scope error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    // RNH_TEMPLATE_WORKFLOW_POLISH_FIX_001: select ComboBox item by visible content.
    private static void SelectComboBoxItemByContent(System.Windows.Controls.ComboBox? comboBox, string? content)
    {
        if (comboBox == null || string.IsNullOrWhiteSpace(content))
            return;

        for (var i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is System.Windows.Controls.ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }
    }

    // RNH_TEMPLATE_WORKFLOW_POLISH_FIX_001: restore the latest scan result for the selected template.
    // RNH_PROJECT_AWARE_TEMPLATE_SCAN_018: template scan restore is scoped by project.
    private void LoadLatestTemplateScopeResults(string templateName)
    {
        _scopeResults.Clear();
        var projectName = GetTemplateProjectName();

        var latestRelease = _releases
            .Where(x => string.Equals(x.Source, "TemplateScan", StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(GetReleaseProjectName(x), projectName, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(x.TemplateName, templateName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.BuiltAt ?? x.CreatedAt)
            .FirstOrDefault();

        if (latestRelease != null)
        {
            foreach (var service in latestRelease.Services.OrderBy(x => x.ServiceName))
                _scopeResults.Add(service);

            ScopeResultsDataGrid?.Items.Refresh();
            TemplateStatusTextBlock.Text = $"Завантажено шаблон: {templateName}. Відновлено останній прогін: {latestRelease.Name}.";
            AppLog.Info($"Latest template scan restored. Template='{templateName}', release='{latestRelease.Name}', services={latestRelease.Services.Count}.");
        }
        else
        {
            ScopeResultsDataGrid?.Items.Refresh();
        }
    }


    // RNH_AI_SNAPSHOT_GUARD_FIX_003: always generate/open release notes from the latest current template scan snapshot.
    private Release? FindReleaseForAiGeneration()
    {
        if (!string.IsNullOrWhiteSpace(_lastTemplateScanReleaseId))
        {
            var byId = _releases.FirstOrDefault(x =>
                string.Equals(x.Id, _lastTemplateScanReleaseId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Source, "TemplateScan", StringComparison.OrdinalIgnoreCase));

            if (byId != null)
                return byId;
        }

        var selectedTemplateName = (TemplatesListBox?.SelectedItem as ReleaseTemplate)?.Name
            ?? TemplateNameTextBox?.Text?.Trim()
            ?? "";

        var releaseName = ReleaseNameTextBox?.Text?.Trim() ?? "";
        var releaseVersion = ReleaseVersionTextBox?.Text?.Trim() ?? "";

        var projectName = GetTemplateProjectName();

        var templateCandidates = _releases
            .Where(x => string.Equals(x.Source, "TemplateScan", StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(GetReleaseProjectName(x), projectName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(selectedTemplateName))
        {
            templateCandidates = templateCandidates
                .Where(x => string.Equals(x.TemplateName, selectedTemplateName, StringComparison.OrdinalIgnoreCase));
        }

        var candidates = templateCandidates.ToList();
        var exactCandidates = candidates.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(releaseName))
        {
            exactCandidates = exactCandidates
                .Where(x => string.Equals(x.Name, releaseName, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(releaseVersion))
        {
            exactCandidates = exactCandidates
                .Where(x =>
                    string.Equals(x.Version, releaseVersion, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.Name, releaseVersion, StringComparison.OrdinalIgnoreCase));
        }

        var exact = exactCandidates
            .OrderByDescending(x => x.ScopeGeneratedAt ?? x.BuiltAt ?? x.CreatedAt)
            .FirstOrDefault();

        if (exact != null)
        {
            _lastTemplateScanReleaseId = exact.Id;
            return exact;
        }

        var latest = candidates
            .OrderByDescending(x => x.ScopeGeneratedAt ?? x.BuiltAt ?? x.CreatedAt)
            .FirstOrDefault();

        if (latest != null)
            _lastTemplateScanReleaseId = latest.Id;

        return latest;
    }

    // RNH_AI_SNAPSHOT_GUARD_FIX_003: warn when current UI target refs no longer match the saved scan snapshot.
    
// RNH_AI_NOTES_SAVE_SCAN_WARNING_032: explain that AI notes use the last Scan snapshot, not unsaved/current UI table state.
    private string BuildAiTargetMismatchWarning(Release release)
    {
        try
        {
            var mismatches = new List<string>();
            var targetRefMode = GetRunTargetRefMode();
            var runTargetRefOverride = GetRunTargetRefOverride(targetRefMode);

            var snapshotIncludedServices = release.Services
                .Where(x => x.Included)
                .OrderBy(x => x.ServiceName)
                .ToList();

            var currentIncludedRows = _rows
                .Where(x => x.Included)
                .OrderBy(x => x.ServiceName)
                .ToList();

            foreach (var row in currentIncludedRows)
            {
                var service = snapshotIncludedServices.FirstOrDefault(x =>
                    string.Equals(x.ServiceName, row.ServiceName, StringComparison.OrdinalIgnoreCase));

                if (service == null)
                {
                    mismatches.Add($"- {row.ServiceName}: включено в поточній таблиці, але цього немає в останньому Scan snapshot");
                    continue;
                }

                var currentTargets = NormalizeTargetRefsForCompare(ResolveTargetRefForScan(row, runTargetRefOverride));
                var snapshotTargets = NormalizeTargetRefsForCompare(service.TargetRef);

                if (string.IsNullOrWhiteSpace(snapshotTargets))
                    snapshotTargets = NormalizeTargetRefsForCompare(service.TargetBranch);

                if (!string.Equals(currentTargets, snapshotTargets, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"- {service.ServiceName}: target refs у snapshot='{snapshotTargets}', у поточній таблиці='{currentTargets}'");
                }

                var currentBase = NormalizeRefForCompare(row.BaselineRef);
                var snapshotBase = NormalizeRefForCompare(service.BaseTag);

                if (!string.IsNullOrWhiteSpace(currentBase) &&
                    !string.IsNullOrWhiteSpace(snapshotBase) &&
                    !string.Equals(currentBase, snapshotBase, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"- {service.ServiceName}: base ref у snapshot='{snapshotBase}', у поточній таблиці='{currentBase}'");
                }
            }

            foreach (var service in snapshotIncludedServices)
            {
                var row = currentIncludedRows.FirstOrDefault(x =>
                    string.Equals(x.ServiceName, service.ServiceName, StringComparison.OrdinalIgnoreCase));

                if (row == null)
                    mismatches.Add($"- {service.ServiceName}: є в останньому Scan snapshot, але зараз галочка в таблиці знята");
            }

            if (mismatches.Count == 0)
                return "";

            var builder = new StringBuilder();
            builder.AppendLine("У таблиці шаблону є зміни, які ще не потрапили в останній Scan snapshot.");
            builder.AppendLine();
            builder.AppendLine("AI notes НЕ читає поточну таблицю напряму. Він генерує release notes зі збереженого snapshot останнього Scan.");
            builder.AppendLine();
            builder.AppendLine("Щоб згенерувати AI notes по поточних refs:");
            builder.AppendLine("1. Натисни «Save».");
            builder.AppendLine("2. Натисни «Scan».");
            builder.AppendLine("3. Після завершення знову натисни «AI notes».");
            builder.AppendLine();
            builder.AppendLine("Якщо продовжити зараз, AI використає старий snapshot:");
            builder.AppendLine($"- Реліз: {release.Name}");
            builder.AppendLine($"- Папка snapshot: {GetReleaseOutputFolderName(release)}");
            builder.AppendLine();
            builder.AppendLine("Розбіжності:");
            foreach (var mismatch in mismatches.Take(20))
                builder.AppendLine(mismatch);

            if (mismatches.Count > 20)
                builder.AppendLine($"... ще розбіжностей: {mismatches.Count - 20}");

            return builder.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Warning("AI snapshot target warning failed: " + CompactLogMessage(ex.Message));
            return "";
        }
    }



    private static string NormalizeRefForCompare(string? value)
    {
        return (value ?? "").Trim();
    }

    private static string NormalizeTargetRefsForCompare(string? targetRefs)
    {
        var values = SplitTargetRefs(targetRefs)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return string.Join("; ", values);
    }


    // RNH_SCAN_PREVIEW_DEDUP_2026_06_19: confirmation preview before scan and compact summary after scan.
    private string BuildTemplateScanPreviewMessage(
        string releaseName,
        string releaseVersion,
        string comparisonBase,
        Release previousRelease,
        IReadOnlyList<ReleaseTemplateService> includedRows,
        string runTargetRefOverride,
        string divergedHistoryDiffMode)
    {
        var message = new StringBuilder();
        message.AppendLine("Перевір scope перед прогоном:");
        message.AppendLine();
        message.AppendLine($"Реліз: {releaseName}");

        if (!string.IsNullOrWhiteSpace(releaseVersion))
            message.AppendLine($"Версія: {releaseVersion}");

        message.AppendLine("Baseline: Auto per service");
        message.AppendLine($"Fallback baseline/previous: {previousRelease.Name}");
        message.AppendLine($"Сервісів включено: {includedRows.Count}");
        message.AppendLine();

        foreach (var row in includedRows.OrderBy(x => x.ServiceName).Take(25))
        {
            var targets = SplitTargetRefs(ResolveTargetRefForScan(row, runTargetRefOverride)).ToList();
            if (targets.Count == 0)
                targets.Add(GetDefaultBranch());

            var previousService = FindServiceInRelease(previousRelease, row.ServiceName);
            var previousText = row.BaselineRef;

            if (string.IsNullOrWhiteSpace(previousText))
                previousText = row.BaselineSha;

            if (string.IsNullOrWhiteSpace(previousText))
                previousText = previousService?.TargetSha;

            if (string.IsNullOrWhiteSpace(previousText))
                previousText = row.BaselineVersion;

            message.AppendLine($"• {row.ServiceName}");
            message.AppendLine($"  previous: {ShortSha(previousText)}");
            message.AppendLine($"  targets: {string.Join(", ", targets)}");
        }

        if (includedRows.Count > 25)
            message.AppendLine($"... ще сервісів: {includedRows.Count - 25}");

        message.AppendLine();
        message.AppendLine("Продовжити прогін?");
        return message.ToString();
    }

    private string BuildScopePreviewMessage(Release release, string comparisonBase, string previousReleaseName, int changedCount, int includedCount, int errorsCount)
    {
        var message = new StringBuilder();
        message.AppendLine("Прогін завершено.");
        message.AppendLine();
        message.AppendLine($"Реліз: {release.Name}");
        message.AppendLine($"База: {previousReleaseName}");
        message.AppendLine("Baseline: Auto per service");
        message.AppendLine($"Змінені сервіси: {changedCount}");
        message.AppendLine($"Включено в реліз: {includedCount}");
        message.AppendLine($"Помилок: {errorsCount}");
        message.AppendLine();

        foreach (var service in release.Services
            .Where(x => x.CommitCount > 0 || string.Equals(x.Status, "Error", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ServiceName)
            .Take(20))
        {
            message.AppendLine($"• {service.ServiceName}");
            message.AppendLine($"  status: {service.Status}");

            if (!string.IsNullOrWhiteSpace(service.TargetRef))
                message.AppendLine($"  targets: {service.TargetRef}");

            message.AppendLine($"  commits: {service.CommitCount} unique");
            message.AppendLine($"  files: {service.ChangedFilesCount} unique");

            if (!string.IsNullOrWhiteSpace(service.DiffRange))
                message.AppendLine($"  diff: {service.DiffRange}");

            if (!string.IsNullOrWhiteSpace(service.GitHistoryWarning))
                message.AppendLine("  warnings: yes");
        }

        if (release.Services.Count(x => x.CommitCount > 0 || string.Equals(x.Status, "Error", StringComparison.OrdinalIgnoreCase)) > 20)
            message.AppendLine("... список скорочено, повний scope дивись в Output artifacts.");

        return message.ToString();
    }

    private string GetComparisonBaseMode()
    {
        return "Auto per service";
    }

    private async Task<ReleaseService> ScanServiceAsync(ReleaseTemplateService row, Release previousRelease, string reposRoot, bool usePreviousReleaseServiceSha, string runTargetRefOverride, string divergedHistoryDiffMode)
    {
        var resolvedTargetRef = ResolveTargetRefForScan(row, runTargetRefOverride);

        var releaseService = new ReleaseService
        {
            ServiceName = row.ServiceName,
            GitUrl = row.GitUrl,
            Included = false,
            TargetBranch = resolvedTargetRef,
            TargetRef = resolvedTargetRef,
            SourceVersion = row.BaselineVersion,
            BaseTag = row.BaselineRef,
            DiffBaseMode = divergedHistoryDiffMode,
            Status = "Pending"
        };

        try
        {
            if (string.IsNullOrWhiteSpace(row.GitUrl))
            {
                releaseService.Status = "Error";
                releaseService.ErrorMessage = "Git URL не заповнений.";
                AppLog.Warning($"Service '{row.ServiceName}' skipped because Git URL is empty.");
                return releaseService;
            }

            AppLog.Info($"Service target resolved. Service='{row.ServiceName}', targetRef='{resolvedTargetRef}', source='{(string.IsNullOrWhiteSpace(runTargetRefOverride) ? "service/template" : "run override")}'.");

            var localRepoPath = Path.Combine(reposRoot, SanitizeFolderName(row.ServiceName));

            if (!Directory.Exists(localRepoPath) || !Directory.Exists(Path.Combine(localRepoPath, ".git")))
            {
                AppLog.Info($"Git clone started. Service='{row.ServiceName}', repo='{row.GitUrl}'.");
                await RunGitAsync(reposRoot, "clone", "--", row.GitUrl, localRepoPath);
                AppLog.Info($"Git clone completed. Service='{row.ServiceName}', path='{localRepoPath}'.");
            }

            AppLog.Info($"Git fetch started. Service='{row.ServiceName}'.");
            await RunGitAsync(localRepoPath, "fetch", "--all", "--tags", "--prune", "--force");
            AppLog.Info($"Git fetch completed. Service='{row.ServiceName}'.");

            // RNH_AUTO_BASELINE_REMOVE_DIVERGED_UI_030:
            // Resolve baseline independently for each service.
            string? previousSha = null;
            var baseSource = "";

            if (!string.IsNullOrWhiteSpace(row.BaselineRef))
            {
                previousSha = await TryResolveRefAsync(localRepoPath, row.BaselineRef);
                if (!string.IsNullOrWhiteSpace(previousSha))
                    baseSource = $"Base ref: {row.BaselineRef}";
            }

            if (string.IsNullOrWhiteSpace(previousSha) && !string.IsNullOrWhiteSpace(row.BaselineSha))
            {
                previousSha = row.BaselineSha;
                baseSource = "Installer baseline SHA";
            }

            if (string.IsNullOrWhiteSpace(previousSha))
            {
                var previousService = FindServiceInRelease(previousRelease, row.ServiceName);
                previousSha = previousService?.TargetSha;
                if (!string.IsNullOrWhiteSpace(previousSha))
                    baseSource = $"Saved baseline/previous release: {previousRelease.Name}";
            }

            if (string.IsNullOrWhiteSpace(previousSha))
            {
                previousSha = await TryResolveRefAsync(localRepoPath, row.BaselineVersion);
                if (!string.IsNullOrWhiteSpace(previousSha))
                    baseSource = $"Baseline version: {row.BaselineVersion}";
            }

            if (!string.IsNullOrWhiteSpace(baseSource))
                AppLog.Info($"Base resolved. Service='{row.ServiceName}', source='{baseSource}', previous={ShortSha(previousSha)}.");

            if (string.IsNullOrWhiteSpace(previousSha))
            {
                releaseService.Status = "Error";
                releaseService.ErrorMessage = "Не вдалося визначити previous SHA з baseline/попереднього релізу/Base ref з шаблону.";
                AppLog.Error($"Previous SHA was not resolved. Service='{row.ServiceName}', baselineRef='{row.BaselineRef}', baselineVersion='{row.BaselineVersion}'.");
                return releaseService;
            }

            var targetRefsForScan = SplitTargetRefs(releaseService.TargetRef).ToList();
            if (targetRefsForScan.Count == 0)
                targetRefsForScan.Add(GetDefaultBranch());

            releaseService.TargetRef = string.Join("; ", targetRefsForScan);
            releaseService.TargetBranch = releaseService.TargetRef;

            if (targetRefsForScan.Count > 1)
                return await ScanServiceMultipleTargetsAsync(row, localRepoPath, releaseService, previousSha, targetRefsForScan, divergedHistoryDiffMode);

            releaseService.TargetRef = targetRefsForScan[0];
            releaseService.TargetBranch = releaseService.TargetRef;

            var targetSha = await ResolveRefAsync(localRepoPath, releaseService.TargetRef);

            releaseService.PreviousSha = previousSha;
            releaseService.TargetSha = targetSha;
            releaseService.EffectiveDiffBaseSha = previousSha;
            releaseService.UsedMergeBaseForDiff = false;
            releaseService.DiffRange = $"{ShortSha(previousSha)}..{ShortSha(targetSha)}";
            AppLog.Info($"Refs resolved. Service='{row.ServiceName}', previous={ShortSha(previousSha)}, target={ShortSha(targetSha)}, targetRef='{releaseService.TargetRef}'.");

            if (string.Equals(previousSha, targetSha, StringComparison.OrdinalIgnoreCase))
            {
                releaseService.Status = "No changes";
                releaseService.CommitCount = 0;
                releaseService.ErrorMessage = "SHA однакові.";
                AppLog.Info($"No changes. Service='{row.ServiceName}', sha={ShortSha(targetSha)}.");
                return releaseService;
            }

            // RNH_TARGET_REF_CONTROLS_2026_06_19: validate Git history and select the effective diff/log base.
            var historyCheck = await CheckGitHistoryAsync(localRepoPath, previousSha, targetSha);
            releaseService.MergeBaseSha = historyCheck.MergeBaseSha;
            releaseService.IsPreviousAncestorOfTarget = historyCheck.IsPreviousAncestorOfTarget;
            releaseService.IsTargetAncestorOfPrevious = historyCheck.IsTargetAncestorOfPrevious;
            releaseService.GitHistoryWarning = historyCheck.Warning;

            if (!string.IsNullOrWhiteSpace(historyCheck.Warning))
            {
                AppLog.Warning($"Git history warning. Service='{row.ServiceName}', mergeBase={ShortSha(historyCheck.MergeBaseSha)}, warning='{CompactLogMessage(historyCheck.Warning)}'.");
            }
            else
            {
                AppLog.Info($"Git history OK. Service='{row.ServiceName}', previous is ancestor of target, mergeBase={ShortSha(historyCheck.MergeBaseSha)}.");
            }

            var effectiveDiffBaseSha = previousSha;
            var useMergeBaseForDiff = false;
            var hasMergeBase = !string.IsNullOrWhiteSpace(historyCheck.MergeBaseSha);
            var forceMergeBase = divergedHistoryDiffMode.StartsWith("Завжди", StringComparison.OrdinalIgnoreCase);
            var autoMergeBase = divergedHistoryDiffMode.StartsWith("Auto:", StringComparison.OrdinalIgnoreCase) && !historyCheck.IsPreviousAncestorOfTarget;

            if ((forceMergeBase || autoMergeBase) && hasMergeBase)
            {
                effectiveDiffBaseSha = historyCheck.MergeBaseSha;
                useMergeBaseForDiff = !string.Equals(effectiveDiffBaseSha, previousSha, StringComparison.OrdinalIgnoreCase);
            }

            releaseService.EffectiveDiffBaseSha = effectiveDiffBaseSha;
            releaseService.DiffBaseMode = divergedHistoryDiffMode;
            releaseService.UsedMergeBaseForDiff = useMergeBaseForDiff;
            releaseService.DiffRange = $"{ShortSha(effectiveDiffBaseSha)}..{ShortSha(targetSha)}";

            if (useMergeBaseForDiff)
                AppLog.Warning($"Diff base switched to merge-base. Service='{row.ServiceName}', range={releaseService.DiffRange}, mode='{divergedHistoryDiffMode}'.");
            else
                AppLog.Info($"Diff base selected. Service='{row.ServiceName}', range={releaseService.DiffRange}, mode='{divergedHistoryDiffMode}'.");

            var countText = await RunGitAsync(localRepoPath, "rev-list", "--count", $"{effectiveDiffBaseSha}..{targetSha}");
            var commitCount = int.TryParse(countText.Trim(), out var parsedCount) ? parsedCount : 0;
            releaseService.CommitCount = commitCount;

            if (commitCount <= 0)
            {
                releaseService.Status = "No changes";
                releaseService.ErrorMessage = "Нових комітів не знайдено.";
                AppLog.Info($"No new commits. Service='{row.ServiceName}', range={releaseService.DiffRange}.");
                return releaseService;
            }

            var commitsText = await RunGitAsync(
                localRepoPath,
                "log",
                $"{effectiveDiffBaseSha}..{targetSha}",
                "--pretty=format:%h | %ad | %an | %s",
                "--date=short");

            releaseService.Commits = SplitLines(commitsText).ToList();

            try
            {
                var changedFilesText = await RunGitAsync(localRepoPath, "diff", "--name-status", $"{effectiveDiffBaseSha}..{targetSha}");
                releaseService.ChangedFiles = SplitLines(changedFilesText).ToList();
                releaseService.ChangedFilesCount = releaseService.ChangedFiles.Count;
                AppLog.Info($"Changed files collected. Service='{row.ServiceName}', files={releaseService.ChangedFilesCount}.");
            }
            catch (Exception diffEx)
            {
                releaseService.ChangedFiles = [];
                releaseService.ChangedFilesCount = 0;
                AppLog.Warning($"Changed files were not collected. Service='{row.ServiceName}', error='{CompactLogMessage(diffEx.Message)}'.");
            }

            releaseService.Status = "Changed";
            releaseService.ErrorMessage = $"Знайдено комітів: {commitCount}. Файлів змінено: {releaseService.ChangedFilesCount}. Diff: {releaseService.DiffRange}.";
            AppLog.Info($"Commits collected. Service='{row.ServiceName}', commits={commitCount}, range={releaseService.DiffRange}.");

            return releaseService;
        }
        catch (Exception ex)
        {
            releaseService.Status = "Error";
            releaseService.ErrorMessage = ex.Message;
            AppLog.Error($"Service scan exception. Service='{row.ServiceName}', error='{CompactLogMessage(ex.Message)}'.");
            return releaseService;
        }
    }
    // RNH_SERVICE_TARGET_REFS_V2_2026_06_19: scan several target refs for one service and aggregate commits/files.
    // RNH_SCAN_PREVIEW_DEDUP_2026_06_19: scan several target refs and aggregate duplicate commits/files across branches.
    private async Task<ReleaseService> ScanServiceMultipleTargetsAsync(
        ReleaseTemplateService row,
        string localRepoPath,
        ReleaseService releaseService,
        string previousSha,
        IReadOnlyList<string> targetRefsForScan,
        string divergedHistoryDiffMode)
    {
        var commitOrder = new List<string>();
        var commitTextByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var commitRefsByKey = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        var fileOrder = new List<string>();
        var fileTextByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileRefsByKey = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        var targetShaParts = new List<string>();
        var mergeBaseParts = new List<string>();
        var diffRanges = new List<string>();
        var warnings = new List<string>();
        var errors = new List<string>();
        var usedMergeBase = false;
        var totalRawCommits = 0;
        var totalRawFiles = 0;

        AppLog.Info($"Multi-target scan started. Service='{row.ServiceName}', targets={targetRefsForScan.Count}, refs='{string.Join("; ", targetRefsForScan)}'.");

        foreach (var targetRef in targetRefsForScan)
        {
            try
            {
                var targetSha = await ResolveRefAsync(localRepoPath, targetRef);
                targetShaParts.Add($"{targetRef}={ShortSha(targetSha)}");

                if (string.Equals(previousSha, targetSha, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Info($"No changes for target ref. Service='{row.ServiceName}', targetRef='{targetRef}', sha={ShortSha(targetSha)}.");
                    continue;
                }

                var historyCheck = await CheckGitHistoryAsync(localRepoPath, previousSha, targetSha);
                if (!string.IsNullOrWhiteSpace(historyCheck.MergeBaseSha))
                    mergeBaseParts.Add($"{targetRef}={ShortSha(historyCheck.MergeBaseSha)}");

                if (!string.IsNullOrWhiteSpace(historyCheck.Warning))
                {
                    var warning = $"{targetRef}: {historyCheck.Warning}";
                    warnings.Add(warning);
                    AppLog.Warning($"Git history warning. Service='{row.ServiceName}', targetRef='{targetRef}', mergeBase={ShortSha(historyCheck.MergeBaseSha)}, warning='{CompactLogMessage(historyCheck.Warning)}'.");
                }
                else
                {
                    AppLog.Info($"Git history OK. Service='{row.ServiceName}', targetRef='{targetRef}', previous is ancestor of target, mergeBase={ShortSha(historyCheck.MergeBaseSha)}.");
                }

                var effectiveDiffBaseSha = previousSha;
                var hasMergeBase = !string.IsNullOrWhiteSpace(historyCheck.MergeBaseSha);
                var forceMergeBase = divergedHistoryDiffMode.StartsWith("Завжди", StringComparison.OrdinalIgnoreCase);
                var autoMergeBase = divergedHistoryDiffMode.StartsWith("Auto:", StringComparison.OrdinalIgnoreCase) && !historyCheck.IsPreviousAncestorOfTarget;

                if ((forceMergeBase || autoMergeBase) && hasMergeBase)
                {
                    effectiveDiffBaseSha = historyCheck.MergeBaseSha;
                    var switched = !string.Equals(effectiveDiffBaseSha, previousSha, StringComparison.OrdinalIgnoreCase);
                    usedMergeBase |= switched;

                    if (switched)
                        AppLog.Warning($"Diff base switched to merge-base. Service='{row.ServiceName}', targetRef='{targetRef}', range={ShortSha(effectiveDiffBaseSha)}..{ShortSha(targetSha)}, mode='{divergedHistoryDiffMode}'.");
                }

                var range = $"{ShortSha(effectiveDiffBaseSha)}..{ShortSha(targetSha)}";
                diffRanges.Add($"{targetRef}: {range}");

                var countText = await RunGitAsync(localRepoPath, "rev-list", "--count", $"{effectiveDiffBaseSha}..{targetSha}");
                var commitCount = int.TryParse(countText.Trim(), out var parsedCount) ? parsedCount : 0;
                totalRawCommits += Math.Max(0, commitCount);

                if (commitCount <= 0)
                {
                    AppLog.Info($"No new commits. Service='{row.ServiceName}', targetRef='{targetRef}', range={range}.");
                    continue;
                }

                var commitsText = await RunGitAsync(
                    localRepoPath,
                    "log",
                    $"{effectiveDiffBaseSha}..{targetSha}",
                    "--pretty=format:%h | %ad | %an | %s",
                    "--date=short");

                foreach (var line in SplitLines(commitsText))
                {
                    var key = line.Split('|')[0].Trim();
                    if (string.IsNullOrWhiteSpace(key))
                        key = line.Trim();

                    if (!commitTextByKey.ContainsKey(key))
                    {
                        commitTextByKey[key] = line;
                        commitRefsByKey[key] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        commitOrder.Add(key);
                    }

                    commitRefsByKey[key].Add(targetRef);
                }

                try
                {
                    var changedFilesText = await RunGitAsync(localRepoPath, "diff", "--name-status", $"{effectiveDiffBaseSha}..{targetSha}");
                    foreach (var line in SplitLines(changedFilesText))
                    {
                        var key = line.Trim();
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        totalRawFiles++;

                        if (!fileTextByKey.ContainsKey(key))
                        {
                            fileTextByKey[key] = line;
                            fileRefsByKey[key] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                            fileOrder.Add(key);
                        }

                        fileRefsByKey[key].Add(targetRef);
                    }
                }
                catch (Exception diffEx)
                {
                    var warning = $"{targetRef}: changed files were not collected: {CompactLogMessage(diffEx.Message)}";
                    warnings.Add(warning);
                    AppLog.Warning($"Changed files were not collected. Service='{row.ServiceName}', targetRef='{targetRef}', error='{CompactLogMessage(diffEx.Message)}'.");
                }

                AppLog.Info($"Target ref scan completed. Service='{row.ServiceName}', targetRef='{targetRef}', commits={commitCount}, range={range}.");
            }
            catch (Exception ex)
            {
                var error = $"{targetRef}: {CompactLogMessage(ex.Message)}";
                errors.Add(error);
                AppLog.Error($"Target ref scan failed. Service='{row.ServiceName}', targetRef='{targetRef}', error='{CompactLogMessage(ex.Message)}'.");
            }
        }

        var commitLines = commitOrder
            .Where(commitTextByKey.ContainsKey)
            .Select(key => $"[{string.Join(", ", commitRefsByKey[key])}] {commitTextByKey[key]}")
            .ToList();

        var changedFileLines = fileOrder
            .Where(fileTextByKey.ContainsKey)
            .Select(key => $"[{string.Join(", ", fileRefsByKey[key])}] {fileTextByKey[key]}")
            .ToList();

        releaseService.PreviousSha = previousSha;
        releaseService.TargetRef = string.Join("; ", targetRefsForScan);
        releaseService.TargetBranch = releaseService.TargetRef;
        releaseService.TargetSha = string.Join("; ", targetShaParts);
        releaseService.MergeBaseSha = string.Join("; ", mergeBaseParts);
        releaseService.EffectiveDiffBaseSha = previousSha;
        releaseService.DiffBaseMode = divergedHistoryDiffMode;
        releaseService.UsedMergeBaseForDiff = usedMergeBase;
        releaseService.DiffRange = string.Join("; ", diffRanges);
        releaseService.GitHistoryWarning = string.Join("\n", warnings.Concat(errors));
        releaseService.Commits = commitLines;
        releaseService.CommitCount = commitLines.Count;
        releaseService.ChangedFiles = changedFileLines;
        releaseService.ChangedFilesCount = changedFileLines.Count;

        if (targetShaParts.Count == 0)
        {
            releaseService.Status = "Error";
            releaseService.ErrorMessage = errors.Count == 0
                ? "Не вдалося визначити жоден target ref."
                : string.Join("\n", errors);
            return releaseService;
        }

        if (releaseService.CommitCount <= 0)
        {
            releaseService.Status = "No changes";
            releaseService.ErrorMessage = errors.Count == 0
                ? "Нових комітів не знайдено по жодному target ref."
                : "Нових комітів не знайдено. Помилки target refs:\n" + string.Join("\n", errors);
            AppLog.Info($"No new commits across target refs. Service='{row.ServiceName}', targets={targetRefsForScan.Count}.");
            return releaseService;
        }

        releaseService.Status = "Changed";
        releaseService.ErrorMessage = $"Знайдено унікальних комітів: {releaseService.CommitCount}. Унікальних файлів: {releaseService.ChangedFilesCount}. Raw commits: {totalRawCommits}. Raw files: {totalRawFiles}. Targets: {releaseService.TargetRef}.";
        AppLog.Info($"Multi-target commits deduplicated. Service='{row.ServiceName}', uniqueCommits={releaseService.CommitCount}, rawCommits={totalRawCommits}, uniqueFiles={releaseService.ChangedFilesCount}, rawFiles={totalRawFiles}, targets={targetRefsForScan.Count}.");
        return releaseService;
    }




    private System.Windows.MessageBoxResult AskIncludeChangedService(ReleaseService service)
    {
        var preview = service.Commits
            .Take(8)
            .Select(x => "• " + x)
            .ToList();

        var message = new StringBuilder();
        message.AppendLine($"Сервіс: {service.ServiceName}");
        message.AppendLine($"Комітів: {service.CommitCount}");
        message.AppendLine($"Файлів: {service.ChangedFilesCount}");
        if (!string.IsNullOrWhiteSpace(service.DiffRange))
            message.AppendLine($"Diff range: {service.DiffRange}");
        if (!string.IsNullOrWhiteSpace(service.GitHistoryWarning))
        {
            message.AppendLine();
            message.AppendLine("Увага: Git history diverged.");
            message.AppendLine(service.GitHistoryWarning);
        }
        message.AppendLine();
        message.AppendLine("Включити ці зміни в реліз?");
        message.AppendLine();

        foreach (var line in preview)
            message.AppendLine(line);

        if (service.Commits.Count > preview.Count)
            message.AppendLine($"... ще {service.Commits.Count - preview.Count}");

        return System.Windows.MessageBox.Show(
            message.ToString(),
            "Зміни сервісу",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);
    }



    private void OpenAiRulesFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            var projectName = GetTemplateProjectName();
            EnsureAiRulesScaffold(projectName, _rows.Select(x => x.ServiceName));

            var editor = new AiRulesEditorWindow(
                projectName,
                TemplateNameTextBox?.Text?.Trim() ?? "",
                ReleaseNameTextBox?.Text?.Trim() ?? "",
                _rows.Select(x => x.ServiceName));

            editor.Owner = System.Windows.Window.GetWindow(this);
            editor.ShowDialog();

            TemplateStatusTextBlock.Text = $"AI rules оновлено: {projectName}";
            AppLog.Info($"AI rules editor closed. Project='{projectName}'.");
        }
        catch (Exception ex)
        {
            AppLog.Warning("AI rules editor failed: " + CompactLogMessage(ex.Message));
            System.Windows.MessageBox.Show(
                ex.Message,
                "AI rules",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private async void GenerateReleaseNotes_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isBusy)
            return;

        var release = FindReleaseForAiGeneration();

        if (release == null)
        {
            System.Windows.MessageBox.Show(
                "Не знайдено прогнаний реліз для цього шаблону. Спочатку натисни «Прогнати шаблон».",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var targetMismatchWarning = BuildAiTargetMismatchWarning(release);
        if (!string.IsNullOrWhiteSpace(targetMismatchWarning))
        {
            var answer = System.Windows.MessageBox.Show(
                targetMismatchWarning + "\n\nПродовжити генерацію AI release notes зі старого snapshot?",
                "AI notes: потрібен Save / Scan",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (answer != System.Windows.MessageBoxResult.Yes)
                return;
        }

        var includedChangedServices = release.Services
            .Where(x => x.Included)
            .Where(x => x.CommitCount > 0)
            .OrderBy(x => x.ServiceName)
            .ToList();

        var includedChangedServicesCount = includedChangedServices.Count;

        if (includedChangedServicesCount == 0)
        {
            System.Windows.MessageBox.Show(
                "У цьому прогоні немає включених сервісів зі змінами.",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        AppLog.Info($"Release notes generation queued. Release='{release.Name}', services={includedChangedServicesCount}.");

        try
        {
            SetBusy(true, "Генерація release notes через AI...");
            AppLog.Info($"Release notes generation started. Release='{release.Name}'.");

            var result = await GenerateReleaseNotesForReleaseAsync(release, includedChangedServices);

            AppLog.Info("Release notes generation completed. " + result);
            SetBusy(false, result);

            System.Windows.MessageBox.Show(
                result,
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error("Release notes generation failed: " + CompactLogMessage(ex.Message));
            SetBusy(false, $"Помилка генерації release notes: {ex.Message}");

            System.Windows.MessageBox.Show(
                ex.Message,
                "AI release notes error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async void OpenReleaseFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var release = FindReleaseForAiGeneration();

        if (release == null)
        {
            System.Windows.MessageBox.Show(
                "Не знайдено прогнаний реліз для цього шаблону.",
                "Release Notes Helper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var releasePath = await GetReleaseOutputPathAsync(release);
        Directory.CreateDirectory(releasePath);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{releasePath}\"",
            UseShellExecute = true
        });
    }

    private async Task<string> GenerateReleaseNotesForReleaseAsync(Release release, List<ReleaseService> services)
    {
        var releasePath = await GetReleaseOutputPathAsync(release);
        var serviceNotesPath = Path.Combine(releasePath, "services");
        var promptsPath = Path.Combine(releasePath, "prompts");

        Directory.CreateDirectory(releasePath);
        Directory.CreateDirectory(serviceNotesPath);
        Directory.CreateDirectory(promptsPath);
        AppLog.Info($"Release notes output prepared. Release='{release.Name}', path='{releasePath}', services={services.Count}.");

        var config = await SafeLoadAsync<AppConfiguration>("config.json");
        var apiKey = ReleaseNotesHelper.App.CredentialStore.Load(AiCredentialTarget);
        var hideTechnicalDataFromAi = config?.HideTechnicalDataFromAi ?? true;

        var aiEnabled =
            !string.IsNullOrWhiteSpace(apiKey) &&
            string.Equals(config?.AiMode, "Внутрішній AI endpoint", StringComparison.OrdinalIgnoreCase);

        var gemini = aiEnabled ? new GeminiClient() : null;
        // RNH_AI_QUOTA_FALLBACK_2026_06_18: quota-aware AI flow.
        var serviceOutputs = new List<(string ServiceName, string Notes, string PromptPath, string NotesPath)>();
        var aiErrors = new List<string>();
        var aiStoppedForThisRun = false;
        var aiStopReason = "";

        foreach (var service in services)
        {
            var prompt = BuildServiceReleaseNotesPrompt(release, service, hideTechnicalDataFromAi);
            var safeServiceName = SanitizeFolderName(service.ServiceName);

            var promptPath = Path.Combine(promptsPath, $"{safeServiceName}.prompt.md");
            var notesPath = Path.Combine(serviceNotesPath, $"{safeServiceName}.md");
            // RNH_STABILIZE_ARTIFACTS_AI_MIRROR: keep AI files near other service artifacts too.
            var serviceArtifactsPath = Path.Combine(releasePath, "service-artifacts", safeServiceName);
            Directory.CreateDirectory(serviceArtifactsPath);

            await File.WriteAllTextAsync(promptPath, prompt, Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(serviceArtifactsPath, "ai-prompt.md"), prompt, Encoding.UTF8);
            AppLog.Info($"AI prompt written. Service='{service.ServiceName}', file='{promptPath}'.");

            var notes = "";

            if (gemini != null && !string.IsNullOrWhiteSpace(apiKey) && !aiStoppedForThisRun)
            {
                try
                {
                    AppLog.Info($"AI request started. Service='{service.ServiceName}'.");
                    notes = await GenerateWithRetryAsync(gemini, apiKey, prompt);
                    AppLog.Info($"AI request completed. Service='{service.ServiceName}'.");
                }
                catch (Exception ex)
                {
                    aiErrors.Add($"{service.ServiceName}: {ex.Message}");

                    if (IsAiQuotaExceededError(ex))
                    {
                        aiStoppedForThisRun = true;
                        aiStopReason = BuildAiQuotaStopReason(ex);
                        ScopeStatusTextBlock.Text = "AI quota вичерпана. Prompt-файли збережено, далі формується fallback.";
                        AppLog.Warning($"AI quota exceeded. Prompt-only fallback enabled for the rest of this run. Service='{service.ServiceName}'. {CompactLogMessage(ex.Message)}");
                    }
                    else
                    {
                        AppLog.Warning($"AI fallback for service '{service.ServiceName}': {CompactLogMessage(ex.Message)}");
                    }

                    notes = BuildFallbackServiceNotes(release, service, aiStoppedForThisRun ? aiStopReason : ex.Message);
                }
            }
            else
            {
                var reason = aiStoppedForThisRun
                    ? aiStopReason
                    : "AI не налаштований або не вибрано внутрішній AI endpoint.";

                if (aiStoppedForThisRun)
                {
                    AppLog.Warning($"AI skipped for service '{service.ServiceName}' because quota fallback is active. Prompt file is available: {promptPath}");
                }
                else
                {
                    AppLog.Warning($"AI disabled or not configured. Fallback service notes will be created for '{service.ServiceName}'.");
                }

                notes = BuildFallbackServiceNotes(release, service, reason);
            }

            await File.WriteAllTextAsync(notesPath, notes, Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(serviceArtifactsPath, "ai-notes.md"), notes, Encoding.UTF8);
            AppLog.Info($"Service notes written. Service='{service.ServiceName}', file='{notesPath}'.");
            serviceOutputs.Add((service.ServiceName, notes, promptPath, notesPath));
        }

        var combinedPrompt = BuildCombinedReleaseNotesPrompt(release, serviceOutputs);
        var combinedPromptPath = Path.Combine(releasePath, "combined-ai-prompt.md");
        await File.WriteAllTextAsync(combinedPromptPath, combinedPrompt, Encoding.UTF8);
        AppLog.Info("Combined AI prompt written: " + combinedPromptPath);

        var combinedNotes = "";

        if (gemini != null && !string.IsNullOrWhiteSpace(apiKey) && !aiStoppedForThisRun)
        {
            try
            {
                AppLog.Info("Combined AI request started.");
                combinedNotes = await GenerateWithRetryAsync(gemini, apiKey, combinedPrompt);
                AppLog.Info("Combined AI request completed.");
            }
            catch (Exception ex)
            {
                aiErrors.Add($"combined: {ex.Message}");

                if (IsAiQuotaExceededError(ex))
                {
                    aiStoppedForThisRun = true;
                    aiStopReason = BuildAiQuotaStopReason(ex);
                    ScopeStatusTextBlock.Text = "AI quota вичерпана. Combined prompt збережено, сформовано fallback.";
                    AppLog.Warning("Combined AI quota fallback: " + CompactLogMessage(ex.Message));
                }
                else
                {
                    AppLog.Warning("Combined AI fallback: " + CompactLogMessage(ex.Message));
                }

                combinedNotes = BuildFallbackCombinedNotes(release, serviceOutputs, aiStoppedForThisRun ? aiStopReason : ex.Message);
            }
        }
        else
        {
            var reason = aiStoppedForThisRun
                ? aiStopReason
                : "AI не налаштований або не вибрано внутрішній AI endpoint.";

            if (aiStoppedForThisRun)
            {
                AppLog.Warning("Combined AI request skipped because quota fallback is active. Combined prompt is available: " + combinedPromptPath);
            }
            else
            {
                AppLog.Warning("AI disabled or not configured. Combined fallback release notes will be created.");
            }

            combinedNotes = BuildFallbackCombinedNotes(release, serviceOutputs, reason);
        }

        var clientNotesPath = Path.Combine(releasePath, "client-release-notes.md");
        await File.WriteAllTextAsync(clientNotesPath, combinedNotes, Encoding.UTF8);
        AppLog.Info("Client release notes written: " + clientNotesPath);

        var serviceIndexPath = Path.Combine(releasePath, "service-release-notes-index.md");
        await File.WriteAllTextAsync(serviceIndexPath, BuildServiceNotesIndex(release, serviceOutputs), Encoding.UTF8);
        AppLog.Info("Service notes index written: " + serviceIndexPath);

        if (aiStoppedForThisRun)
        {
            var promptOnlyGuidePath = Path.Combine(releasePath, "prompt-only-fallback.md");
            await File.WriteAllTextAsync(
                promptOnlyGuidePath,
                BuildPromptOnlyFallbackGuide(release, serviceOutputs, combinedPromptPath, promptsPath, aiStopReason),
                Encoding.UTF8);
            AppLog.Warning("Prompt-only fallback guide written: " + promptOnlyGuidePath);
        }

        if (aiErrors.Count > 0)
        {
            var errorsPath = Path.Combine(releasePath, "ai-errors.txt");
            await File.WriteAllLinesAsync(errorsPath, aiErrors, Encoding.UTF8);
            AppLog.Warning($"AI errors written. Count={aiErrors.Count}, file='{errorsPath}'.");
        }

        return aiErrors.Count == 0
            ? $"Release notes згенеровано: {clientNotesPath}"
            : $"Release notes згенеровано з fallback для частини запитів. Файл: {clientNotesPath}. AI помилки: {aiErrors.Count}.";
    }


    private static string BuildServiceReleaseNotesPrompt(Release release, ReleaseService service, bool hideTechnicalDataFromAi)
    {
        var projectName = GetReleaseProjectName(release);
        var commitLines = hideTechnicalDataFromAi
            ? service.Commits.Select(SanitizeCommitForAi)
            : service.Commits.Select(x => x.Trim());

        var cleanedCommitLines = commitLines
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var commits = cleanedCommitLines.Count > 0
            ? string.Join(Environment.NewLine, cleanedCommitLines.Select(x => "- " + x))
            : "- Коміти не знайдені.";

        var builder = new StringBuilder();
        builder.AppendLine("Ти готуєш release notes українською мовою для одного сервісу.");
        builder.AppendLine();
        builder.AppendLine($"Реліз: {release.Name}");
        builder.AppendLine($"Проект: {projectName}");
        builder.AppendLine($"Сервіс: {service.ServiceName}");
        builder.AppendLine($"Кількість релевантних змін: {cleanedCommitLines.Count}");
        builder.AppendLine();
        builder.AppendLine("Завдання:");
        builder.AppendLine("- Сформуй короткий клієнтський changelog саме по цьому сервісу.");
        AppendDefaultDomainRules(builder, projectName);
        builder.AppendLine("- Не згадуй hash комітів.");
        builder.AppendLine("- Не згадуй авторів.");
        builder.AppendLine("- Не згадуй назви гілок.");
        builder.AppendLine("- Не пиши технічні деталі, якщо з них не зрозуміла бізнес-цінність.");
        builder.AppendLine("- Групуй схожі коміти.");
        builder.AppendLine("- Максимум 8 пунктів.");
        builder.AppendLine("- Формат: markdown.");
        builder.AppendLine("- Починай одразу зі списку змін без привітання, вступу або маркетингової фрази.");
        builder.AppendLine("- Не використовуй фрази типу \"Ми раді повідомити\", \"Оновлення включає\" або \"Нижче наведено\".");
        builder.AppendLine("- Не додавай заголовок із назвою сервісу; назва сервісу використовується лише для локального індексу.");
        builder.AppendLine("- Не використовуй вкладені списки.");
        builder.AppendLine("- Кожен пункт має починатися зі слова: Додано, Покращено, Усунено або Реалізовано.");
        builder.AppendLine("- Не використовуй загальні фрази типу \"покращено стабільність\", якщо немає конкретного користувацького сценарію.");
        builder.AppendLine("- Не згадуй \"на основі відгуків клієнтів\".");

        AppendAiRulesBlock(builder, release, "service", service.ServiceName);

        builder.AppendLine();
        builder.AppendLine("Коміти:");
        builder.AppendLine(commits);

        return builder.ToString();
    }

    private static string SanitizeCommitForAi(string commit)
    {
        if (string.IsNullOrWhiteSpace(commit))
            return string.Empty;

        var text = commit.Trim();
        var parts = text.Split('|');

        if (parts.Length >= 4)
            text = parts[^1].Trim();

        if (text.StartsWith("Merge branch ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Merge remote-tracking branch ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Merge pull request ", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (text.Contains("fix build", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("version and changelog", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("changes based on", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("customers request", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("customer request", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        text = RegexReplace(text, @"https?://\S+", "[redacted-url]");
        text = RegexReplace(text, @"git@\S+", "[redacted-repo]");
        text = RegexReplace(text, @"\borigin/[A-Za-z0-9._/\-]+", "[redacted-ref]");
        text = RegexReplace(text, @"\s*[\[(]PTS-\d+[\])]", "");
        text = RegexReplace(text, @"^\s*(feat|fix|chore|docs|refactor|perf|test|build|ci)\s*:\s*", "");
        text = text.Replace("inseide", "inside", StringComparison.OrdinalIgnoreCase);
        text = text.Replace("if logistics if self-resolved", "if logistics is self-resolved", StringComparison.OrdinalIgnoreCase);

        return text.Trim();
    }

    
    private string BuildCombinedReleaseNotesPrompt(Release release, List<(string ServiceName, string Notes, string PromptPath, string NotesPath)> serviceOutputs)
    {
        var projectName = GetReleaseProjectName(release);
        var notes = new StringBuilder();

        foreach (var item in serviceOutputs)
        {
            notes.AppendLine($"### {item.ServiceName}");
            notes.AppendLine(item.Notes);
            notes.AppendLine();
        }

        var builder = new StringBuilder();
        builder.AppendLine("Ти готуєш фінальні release notes українською мовою для клієнта.");
        builder.AppendLine();
        builder.AppendLine($"Реліз: {release.Name}");
        builder.AppendLine($"Проект: {projectName}");
        builder.AppendLine();
        builder.AppendLine("Вхідні дані нижче — це release notes по окремих сервісах. Потрібно об'єднати їх в один чистий клієнтський changelog.");
        builder.AppendLine();
        builder.AppendLine("Правила:");
        builder.AppendLine("- Не згадуй назви сервісів, якщо це не потрібно клієнту.");
        AppendDefaultDomainRules(builder, projectName);
        builder.AppendLine("- Не згадуй hash комітів, Git, гілки, авторів.");
        builder.AppendLine("- Не дублюй однакові зміни.");
        builder.AppendLine("- Об'єднуй схожі зміни в один пункт.");
        builder.AppendLine("- Пиши коротко і зрозуміло.");
        builder.AppendLine("- Максимум 12 пунктів.");
        builder.AppendLine("- Не використовуй вкладені списки.");
        builder.AppendLine("- Не роби окремі секції \"Додано\", \"Покращено\", \"Усунено\".");
        builder.AppendLine("- Кожен пункт має бути одним реченням.");
        builder.AppendLine("- Кожен пункт має починатися зі слова: Додано, Покращено, Усунено або Реалізовано.");
        builder.AppendLine("- Не використовуй загальні фрази типу \"покращено стабільність\", якщо немає конкретного користувацького сценарію.");
        builder.AppendLine("- Не згадуй \"на основі відгуків клієнтів\".");

        AppendAiRulesBlock(builder, release, "final", null);

        builder.AppendLine("- Формат:");
        builder.AppendLine($"  # {release.Name}");
        builder.AppendLine();
        builder.AppendLine("  * Додано ...");
        builder.AppendLine("  * Покращено ...");
        builder.AppendLine("  * Усунено ...");
        builder.AppendLine("  * Реалізовано ...");
        builder.AppendLine();
        builder.AppendLine("Notes по сервісах:");
        builder.AppendLine(notes.ToString());

        return builder.ToString();
    }

    private static void AppendDefaultDomainRules(StringBuilder builder, string projectName)
    {
        if (projectName.Contains("Poruch", StringComparison.OrdinalIgnoreCase) ||
            projectName.Contains("Поруч", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine("- Обов'язково використовуй доменний глосарій RS.Core Poruch.");
            builder.AppendLine("- ticket/request/application = заявка, не квиток.");
            builder.AppendLine("- traveler/passenger/person = особа або ВПО, залежно від контексту.");
            builder.AppendLine("- accommodation/placement = розміщення.");
            builder.AppendLine("- trip/logistics = поїздка або логістика.");
            builder.AppendLine("- transit center = транзитний центр.");
            builder.AppendLine("- self-organized trip/logistics = самостійно організована поїздка.");
            builder.AppendLine("- Не використовуй слово \"квиток\", якщо в комітах/notes немає прямої згадки саме про квиток.");
            return;
        }

        builder.AppendLine("- Використовуй доменний глосарій проекту з додаткових правил, якщо він заданий.");
    }

    private static void AppendAiRulesBlock(StringBuilder builder, Release release, string purpose, string? serviceName)
    {
        var blocks = LoadAiRuleBlocks(release, purpose, serviceName);
        if (blocks.Count == 0)
            return;

        builder.AppendLine();
        builder.AppendLine("Додаткові правила проекту:");

        foreach (var block in blocks)
        {
            builder.AppendLine($"## {block.Label}");
            builder.AppendLine(block.Content.Trim());
            builder.AppendLine();
        }
    }

    private static List<(string Label, string Content)> LoadAiRuleBlocks(Release release, string purpose, string? serviceName)
    {
        var result = new List<(string Label, string Content)>();
        var projectName = GetReleaseProjectName(release);
        var root = AppPaths.AiRulesPath;
        var projectRoot = Path.Combine(root, "projects", SafePathSegment(projectName));
        var safeServiceName = string.IsNullOrWhiteSpace(serviceName) ? "" : SafePathSegment(serviceName);
        var safeReleaseName = SafePathSegment(release.Name);
        var safeTemplateName = SafePathSegment(release.TemplateName);

        AddRuleFile(result, Path.Combine(root, "global.md"), "global.md");
        AddRuleFile(result, Path.Combine(projectRoot, "project-rules.md"), "project-rules.md");
        AddRuleFile(result, Path.Combine(projectRoot, "glossary.md"), "glossary.md");

        if (string.Equals(purpose, "service", StringComparison.OrdinalIgnoreCase))
        {
            AddRuleFile(result, Path.Combine(projectRoot, "service-release-notes.md"), "service-release-notes.md");
            if (!string.IsNullOrWhiteSpace(safeServiceName))
                AddRuleFile(result, Path.Combine(projectRoot, "services", safeServiceName + ".md"), "services/" + safeServiceName + ".md");
        }
        else
        {
            AddRuleFile(result, Path.Combine(projectRoot, "final-release-notes.md"), "final-release-notes.md");
        }

        AddRuleFile(result, Path.Combine(projectRoot, "releases", safeReleaseName + ".md"), "releases/" + safeReleaseName + ".md");
        AddRuleFile(result, Path.Combine(projectRoot, "templates", safeTemplateName + ".md"), "templates/" + safeTemplateName + ".md");

        return result;
    }

    private static void AddRuleFile(List<(string Label, string Content)> result, string path, string label)
    {
        if (!File.Exists(path))
            return;

        var content = File.ReadAllText(path, Encoding.UTF8).Trim();
        if (string.IsNullOrWhiteSpace(content))
            return;

        result.Add((label, content.Length > 12000 ? content[..12000] + Environment.NewLine + "[truncated]" : content));
    }

    private static string EnsureAiRulesScaffold(string projectName, IEnumerable<string> serviceNames)
    {
        projectName = string.IsNullOrWhiteSpace(projectName) ? "Default" : projectName.Trim();
        var root = AppPaths.AiRulesPath;
        var projectRoot = Path.Combine(root, "projects", SafePathSegment(projectName));

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, "services"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "releases"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "templates"));

        WriteFileIfMissing(Path.Combine(root, "global.md"), "# Global AI rules\n\n- Не згадуй Git, hash комітів, гілки, авторів та внутрішні URL у клієнтських release notes.\n- Пиши коротко, предметно і без маркетингового вступу.\n");
        WriteFileIfMissing(Path.Combine(projectRoot, "project-rules.md"), $"# {projectName} — project rules\n\n- Опиши зміни мовою користувацької цінності.\n- Не вигадуй функціональність, якої немає у вхідних змінах.\n");
        WriteFileIfMissing(Path.Combine(projectRoot, "glossary.md"), BuildDefaultGlossary(projectName));
        WriteFileIfMissing(Path.Combine(projectRoot, "service-release-notes.md"), "# Service release notes rules\n\n- Починай одразу зі списку змін.\n- Групуй технічно схожі коміти в один клієнтський пункт.\n");
        WriteFileIfMissing(Path.Combine(projectRoot, "final-release-notes.md"), "# Final release notes rules\n\n- Об'єднуй дублікати між сервісами.\n- Не згадуй назви сервісів, якщо це не потрібно клієнту.\n");

        foreach (var rawServiceName in serviceNames.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var servicePath = Path.Combine(projectRoot, "services", SafePathSegment(rawServiceName) + ".md");
            WriteFileIfMissing(servicePath, $"# {rawServiceName}\n\n- Додай правила формулювання release notes для цього сервісу за потреби.\n");
        }

        return projectRoot;
    }

    private static string BuildDefaultGlossary(string projectName)
    {
        if (projectName.Contains("Poruch", StringComparison.OrdinalIgnoreCase) ||
            projectName.Contains("Поруч", StringComparison.OrdinalIgnoreCase))
        {
            return "# RS.Core Poruch glossary\n\n- ticket/request/application = заявка, не квиток.\n- traveler/passenger/person = особа або ВПО, залежно від контексту.\n- accommodation/placement = розміщення.\n- trip/logistics = поїздка або логістика.\n- transit center = транзитний центр.\n- self-organized trip/logistics = самостійно організована поїздка.\n";
        }

        return "# Project glossary\n\n- Додай доменні терміни проекту у форматі: source term = клієнтський термін.\n";
    }

    private static void WriteFileIfMissing(string path, string content)
    {
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppPaths.AiRulesPath);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static string SafePathSegment(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Default" : value.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');
        return value.Replace('/', '_').Replace('\\', '_');
    }

    private static string RegexReplace(string input, string pattern, string replacement)
    {
        return System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

        private async Task<string> GenerateWithRetryAsync(GeminiClient gemini, string apiKey, string prompt)
    {
        Exception? lastException = null;
        var delaysSeconds = new[] { 4, 10, 20 };

        for (var attempt = 1; attempt <= delaysSeconds.Length + 1; attempt++)
        {
            try
            {
                return await gemini.GenerateAsync(apiKey, prompt);
            }
            catch (Exception ex) when (IsAiQuotaExceededError(ex))
            {
                AppLog.Warning("AI quota error detected. Retry skipped. " + CompactLogMessage(ex.Message));
                throw;
            }
            catch (Exception ex) when (IsTransientAiError(ex) && attempt <= delaysSeconds.Length)
            {
                lastException = ex;
                ScopeStatusTextBlock.Text = $"AI тимчасово недоступний. Спроба {attempt + 1}/{delaysSeconds.Length + 1} через {delaysSeconds[attempt - 1]} сек...";
                AppLog.Warning($"Transient AI error. Retry {attempt + 1}/{delaysSeconds.Length + 1} in {delaysSeconds[attempt - 1]} sec. {CompactLogMessage(ex.Message)}");
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

    private static bool IsAiQuotaExceededError(Exception ex)
    {
        var message = ex.ToString();

        return message.Contains("\"code\": 429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("exceeded your current quota", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("generate_content_free_tier", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("rate-limits", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("rate-limit", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildAiQuotaStopReason(Exception ex)
    {
        return "AI quota exhausted. Prompt files were saved. Retry after quota reset, switch model/API key, or use prompt-only/manual generation. Details: " + CompactLogMessage(ex.Message);
    }

    private static bool IsTransientAiError(Exception ex)
    {
        var message = ex.ToString();

        return message.Contains("\"code\": 503", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("\"code\": 500", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("\"code\": 502", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("\"code\": 504", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("503", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("temporarily", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPromptOnlyFallbackGuide(
        Release release,
        IEnumerable<(string ServiceName, string Notes, string PromptPath, string NotesPath)> serviceOutputs,
        string combinedPromptPath,
        string promptsPath,
        string reason)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"# Prompt-only fallback: {release.Name}");
        builder.AppendLine();
        builder.AppendLine("AI generation was stopped for this run because the provider returned a quota error.");
        builder.AppendLine();
        builder.AppendLine("Reason:");
        builder.AppendLine(reason);
        builder.AppendLine();
        builder.AppendLine("Use these files for manual generation or retry later:");
        builder.AppendLine($"- Combined prompt: {combinedPromptPath}");
        builder.AppendLine($"- Service prompts folder: {promptsPath}");
        builder.AppendLine();
        builder.AppendLine("Service prompts:");

        foreach (var service in serviceOutputs)
        {
            builder.AppendLine($"- {service.ServiceName}: {service.PromptPath}");
        }

        builder.AppendLine();
        builder.AppendLine("Fallback notes were generated locally, so release files are still available even without AI response.");

        return builder.ToString();
    }

    private string BuildFallbackServiceNotes(Release release, ReleaseService service, string reason)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"## {service.ServiceName}");
        builder.AppendLine();
        builder.AppendLine($"AI fallback: {reason}");
        builder.AppendLine();
        builder.AppendLine($"Комітів: {service.CommitCount}");
        builder.AppendLine();

        foreach (var commit in service.Commits.Take(30))
            builder.AppendLine($"- {commit}");

        if (service.Commits.Count > 30)
            builder.AppendLine($"- ... ще {service.Commits.Count - 30}");

        return builder.ToString();
    }

    private string BuildFallbackCombinedNotes(Release release, List<(string ServiceName, string Notes, string PromptPath, string NotesPath)> serviceOutputs, string reason)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"# {release.Name}");
        builder.AppendLine();
        builder.AppendLine($"AI fallback: {reason}");
        builder.AppendLine();

        foreach (var item in serviceOutputs)
        {
            builder.AppendLine($"## {item.ServiceName}");
            builder.AppendLine();
            builder.AppendLine(item.Notes.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private string BuildServiceNotesIndex(Release release, List<(string ServiceName, string Notes, string PromptPath, string NotesPath)> serviceOutputs)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"# Service release notes index — {release.Name}");
        builder.AppendLine();

        foreach (var item in serviceOutputs.OrderBy(x => x.ServiceName))
        {
            builder.AppendLine($"- {item.ServiceName}: `{Path.GetFileName(item.NotesPath)}`");
        }

        return builder.ToString();
    }

    private Release? FindLatestTemplateScanRelease()
    {
        var templateName = TemplateNameTextBox.Text.Trim();

        var projectName = GetTemplateProjectName();

        return _releases
            .Where(x => string.Equals(x.Source, "TemplateScan", StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(GetReleaseProjectName(x), projectName, StringComparison.OrdinalIgnoreCase))
            .Where(x => string.IsNullOrWhiteSpace(templateName) ||
                        string.Equals(x.TemplateName, templateName, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Services.Count > 0)
            .OrderByDescending(x => x.BuiltAt ?? x.CreatedAt)
            .FirstOrDefault();
    }

    private async Task<string> GetReleaseOutputPathAsync(Release release)
    {
        var root = await GetReleasesRootAsync();
        return Path.Combine(root, GetReleaseOutputFolderName(release));
    }

    private async Task<string> GetReleasesRootAsync()
    {
        var config = await _store.LoadAsync<AppConfiguration>("config.json");

        if (!string.IsNullOrWhiteSpace(config?.OutputPath))
            return Path.Combine(config.OutputPath, "releases");

        return AppPaths.ReleasesPath;
    }

    private async Task WriteReleaseScopeFilesAsync(Release release, Release previousRelease)
    {
        var root = await GetReleasesRootAsync();
        var releasePath = Path.Combine(root, GetReleaseOutputFolderName(release));
        Directory.CreateDirectory(releasePath);

        var artifactsRoot = Path.Combine(releasePath, "service-artifacts");
        Directory.CreateDirectory(artifactsRoot);

        var summaryPath = Path.Combine(releasePath, "scope-summary.md");
        var commitsPath = Path.Combine(releasePath, "scope-commits.md");
        var changedFilesPath = Path.Combine(releasePath, "scope-changed-files.md");
        var artifactsIndexPath = Path.Combine(releasePath, "service-artifacts-index.md");

        var summary = new StringBuilder();
        summary.AppendLine($"# {release.Name}");
        summary.AppendLine();
        summary.AppendLine($"Template: {release.TemplateName}");
        summary.AppendLine($"Project: {release.ProjectName}");
        summary.AppendLine($"Previous release: {previousRelease.Name}");
        summary.AppendLine($"Built at: {release.BuiltAt:yyyy-MM-dd HH:mm:ss}");
        summary.AppendLine();

        summary.AppendLine("## Services");
        summary.AppendLine();
        summary.AppendLine("| Included | Service | Status | Commits | Files | History | Diff range | Target | Artifacts |");
        summary.AppendLine("|---|---|---:|---:|---:|---|---|---|---|");

        var commits = new StringBuilder();
        commits.AppendLine($"# Commits for {release.Name}");
        commits.AppendLine();

        var changedFiles = new StringBuilder();
        changedFiles.AppendLine($"# Changed files for {release.Name}");
        changedFiles.AppendLine();

        var artifactsIndex = new StringBuilder();
        artifactsIndex.AppendLine($"# Service artifacts for {release.Name}");
        artifactsIndex.AppendLine();
        artifactsIndex.AppendLine($"Previous release: {previousRelease.Name}");
        artifactsIndex.AppendLine();

        foreach (var service in release.Services.OrderBy(x => x.ServiceName))
        {
            var safeServiceName = SanitizeFolderName(service.ServiceName);
            var serviceDir = Path.Combine(artifactsRoot, safeServiceName);
            Directory.CreateDirectory(serviceDir);

            var historyStatus = string.IsNullOrWhiteSpace(service.GitHistoryWarning) ? "OK" : "WARN";
            summary.AppendLine($"| {(service.Included ? "yes" : "no")} | {service.ServiceName} | {service.Status} | {service.CommitCount} | {service.ChangedFilesCount} | {historyStatus} | {service.DiffRange} | {service.TargetRef} | service-artifacts/{safeServiceName}/ |");

            artifactsIndex.AppendLine($"## {service.ServiceName}");
            artifactsIndex.AppendLine();
            artifactsIndex.AppendLine($"- Status: {service.Status}");
            artifactsIndex.AppendLine($"- Included: {(service.Included ? "yes" : "no")}");
            artifactsIndex.AppendLine($"- Commits: {service.CommitCount}");
            artifactsIndex.AppendLine($"- Changed files: {service.ChangedFilesCount}");
            artifactsIndex.AppendLine($"- Target refs: {service.TargetRef}");
            artifactsIndex.AppendLine($"- Folder: service-artifacts/{safeServiceName}/");
            artifactsIndex.AppendLine();

            commits.AppendLine($"## {service.ServiceName}");
            commits.AppendLine();
            AppendGitDiagnostics(commits, service);

            if (service.Commits.Count == 0)
            {
                commits.AppendLine("_No commits._");
            }
            else
            {
                foreach (var commit in service.Commits)
                    commits.AppendLine($"- {commit}");
            }
            commits.AppendLine();

            changedFiles.AppendLine($"## {service.ServiceName}");
            changedFiles.AppendLine();
            AppendGitDiagnostics(changedFiles, service);

            if (service.ChangedFiles.Count == 0)
            {
                changedFiles.AppendLine("_No changed files._");
            }
            else
            {
                foreach (var changedFile in service.ChangedFiles)
                    changedFiles.AppendLine($"- {changedFile}");
            }
            changedFiles.AppendLine();

            await WriteServiceArtifactFilesAsync(serviceDir, release, previousRelease, service);
        }

        await File.WriteAllTextAsync(summaryPath, summary.ToString(), Encoding.UTF8);
        await File.WriteAllTextAsync(commitsPath, commits.ToString(), Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(releasePath, "release-metadata.md"),
            BuildReleaseMetadataMarkdown(release, previousRelease),
            Encoding.UTF8);
        await File.WriteAllTextAsync(changedFilesPath, changedFiles.ToString(), Encoding.UTF8);
        await File.WriteAllTextAsync(artifactsIndexPath, artifactsIndex.ToString(), Encoding.UTF8);

        // RNH_TARGET_LEVEL_ARTIFACTS: create per-target-ref artifacts under service-artifacts/<service>/targets/.
        WriteTargetLevelServiceArtifacts(releasePath, release);

        AppLog.Info($"Service-level artifacts written. Release='{release.Name}', services={release.Services.Count}, folder='{artifactsRoot}'.");
    }

    private static async Task WriteServiceArtifactFilesAsync(string serviceDir, Release release, Release previousRelease, ReleaseService service)
    {
        await File.WriteAllTextAsync(Path.Combine(serviceDir, "summary.md"), BuildServiceArtifactSummary(release, previousRelease, service), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(serviceDir, "commits.md"), BuildServiceArtifactCommits(service), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(serviceDir, "changed-files.md"), BuildServiceArtifactChangedFiles(service), Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(serviceDir, "git-info.md"), BuildServiceArtifactGitInfo(service), Encoding.UTF8);
    }

    private static string BuildServiceArtifactSummary(Release release, Release previousRelease, ReleaseService service)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {service.ServiceName}");
        builder.AppendLine();
        builder.AppendLine($"Release: {release.Name}");
        builder.AppendLine($"Template: {release.TemplateName}");
        builder.AppendLine($"Previous release: {previousRelease.Name}");
        builder.AppendLine($"Status: {service.Status}");
        builder.AppendLine($"Included: {(service.Included ? "yes" : "no")}");
        builder.AppendLine($"Commits: {service.CommitCount}");
        builder.AppendLine($"Changed files: {service.ChangedFilesCount}");
        builder.AppendLine($"Target refs: {service.TargetRef}");
        builder.AppendLine($"Diff range: {service.DiffRange}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(service.ErrorMessage))
        {
            builder.AppendLine("## Message");
            builder.AppendLine();
            builder.AppendLine(service.ErrorMessage);
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(service.GitHistoryWarning))
        {
            builder.AppendLine("## Warnings");
            builder.AppendLine();
            builder.AppendLine(service.GitHistoryWarning);
            builder.AppendLine();
        }

        builder.AppendLine("## Files");
        builder.AppendLine();
        builder.AppendLine("- summary.md");
        builder.AppendLine("- commits.md");
        builder.AppendLine("- changed-files.md");
        builder.AppendLine("- git-info.md");
        builder.AppendLine("- ai-prompt.md / ai-notes.md після генерації release notes");
        return builder.ToString();
    }

    private static string BuildServiceArtifactCommits(ReleaseService service)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Commits — {service.ServiceName}");
        builder.AppendLine();
        AppendGitDiagnostics(builder, service);

        if (service.Commits.Count == 0)
        {
            builder.AppendLine("_No commits._");
            return builder.ToString();
        }

        foreach (var commit in service.Commits)
            builder.AppendLine($"- {commit}");

        return builder.ToString();
    }

    private static string BuildServiceArtifactChangedFiles(ReleaseService service)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Changed files — {service.ServiceName}");
        builder.AppendLine();
        AppendGitDiagnostics(builder, service);

        if (service.ChangedFiles.Count == 0)
        {
            builder.AppendLine("_No changed files._");
            return builder.ToString();
        }

        foreach (var changedFile in service.ChangedFiles)
            builder.AppendLine($"- {changedFile}");

        return builder.ToString();
    }

    private static string BuildServiceArtifactGitInfo(ReleaseService service)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Git info — {service.ServiceName}");
        builder.AppendLine();
        AppendGitDiagnostics(builder, service);
        return builder.ToString();
    }

    private static void AppendGitDiagnostics(StringBuilder builder, ReleaseService service)
    {
        builder.AppendLine($"Service: {service.ServiceName}");
        builder.AppendLine($"Git URL: {service.GitUrl}");
        builder.AppendLine($"Base tag/ref: {service.BaseTag}");
        builder.AppendLine($"Previous SHA: {service.PreviousSha}");
        builder.AppendLine($"Target refs: {service.TargetRef}");
        builder.AppendLine($"Target SHA: {service.TargetSha}");
        builder.AppendLine($"Merge-base: {service.MergeBaseSha}");
        builder.AppendLine($"Effective diff base: {service.EffectiveDiffBaseSha}");
        builder.AppendLine($"Diff mode: {service.DiffBaseMode}");
        builder.AppendLine($"Used merge-base for diff: {service.UsedMergeBaseForDiff}");
        builder.AppendLine($"Diff range: {service.DiffRange}");
        builder.AppendLine($"History previous ancestor of target: {service.IsPreviousAncestorOfTarget}");
        builder.AppendLine($"History target ancestor of previous: {service.IsTargetAncestorOfPrevious}");

        if (!string.IsNullOrWhiteSpace(service.GitHistoryWarning))
        {
            builder.AppendLine();
            builder.AppendLine("Git history warning:");
            builder.AppendLine(service.GitHistoryWarning);
        }

        builder.AppendLine();
    }

private Release? FindPreviousReleaseForTemplate(string templateName)
    {
        var projectName = GetTemplateProjectName();

        return _releases
            .Where(x => x.Services.Count > 0)
            .Where(x => string.Equals(GetReleaseProjectName(x), projectName, StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.Equals(x.Type, "Baseline", StringComparison.OrdinalIgnoreCase))
            .Where(x => string.Equals(x.TemplateName, templateName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.BuiltAt ?? x.CreatedAt)
            .FirstOrDefault();
    }

    private Release? FindLatestBaseline()
    {
        var projectName = GetTemplateProjectName();
        var projectBaselines = _releases
            .Where(x => x.Services.Count > 0)
            .Where(x => string.Equals(GetReleaseProjectName(x), projectName, StringComparison.OrdinalIgnoreCase));

        return projectBaselines
            .OrderByDescending(x => x.BuiltAt ?? x.CreatedAt)
            .FirstOrDefault(x =>
                string.Equals(x.Type, "Baseline", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Source, "InstallerYaml", StringComparison.OrdinalIgnoreCase))
            ?? _releases
                .Where(x => x.Services.Count > 0)
                .OrderByDescending(x => x.BuiltAt ?? x.CreatedAt)
                .FirstOrDefault();
    }

    private ReleaseService? FindServiceInRelease(Release release, string serviceName)
    {
        var keys = BuildServiceKeys(serviceName);

        return release.Services.FirstOrDefault(service =>
        {
            var serviceKeys = BuildServiceKeys(service.ServiceName)
                .Concat(BuildServiceKeys(service.ImageName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return serviceKeys.Any(keys.Contains);
        });
    }

    private ServiceDefinition? FindCatalogService(string serviceName, string imageName)
    {
        var itemKeys = BuildServiceKeys(serviceName)
            .Concat(BuildServiceKeys(imageName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _services.FirstOrDefault(service =>
        {
            var serviceKeys = BuildServiceKeys(service.Name)
                .Concat(BuildServiceKeys(service.InstallerImageName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return serviceKeys.Any(itemKeys.Contains);
        });
    }

    private void TemplateSearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isLoading)
            return;

        RefreshTemplatesList();
    }

    private void CommitTemplateGridEdits()
    {
        TemplateServicesDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        TemplateServicesDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
    }

    private static ReleaseTemplateService CloneRow(ReleaseTemplateService row)
    {
        return new ReleaseTemplateService
        {
            Included = row.Included,
            ServiceName = row.ServiceName,
            GitUrl = row.GitUrl,
            ValidationStatus = row.ValidationStatus,
            TargetBranch = string.IsNullOrWhiteSpace(row.TargetBranch) ? "origin/dev" : row.TargetBranch,
            AskIfChanged = row.AskIfChanged,
            BaselineVersion = row.BaselineVersion,
            BaselineRef = row.BaselineRef,
            BaselineSha = row.BaselineSha,
            Notes = row.Notes
        };
    }

    private static void EnsureRowDefaults(ReleaseTemplateService row)
    {
        if (string.IsNullOrWhiteSpace(row.TargetBranch))
            row.TargetBranch = "origin/dev";

        if (string.IsNullOrWhiteSpace(row.ValidationStatus))
            row.ValidationStatus = string.IsNullOrWhiteSpace(row.GitUrl) ? "Needs Git URL" : "Valid";
    }

    private static bool IsIgnoredOrExternal(string status)
    {
        return string.Equals(status, "Ignore", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "External image", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetEffectiveStatus(ServiceDefinition service)
    {
        if (!string.IsNullOrWhiteSpace(service.ValidationStatus))
            return service.ValidationStatus;

        if (service.NeedsGitUrl || string.IsNullOrWhiteSpace(service.GitUrl))
            return "Needs Git URL";

        if (!service.IsActive)
            return "Ignore";

        return "Valid";
    }

    private string GetDefaultBranch()
    {
        return string.IsNullOrWhiteSpace(TemplateDefaultBranchTextBox.Text)
            ? "origin/dev"
            : TemplateDefaultBranchTextBox.Text.Trim();
    }

    private async Task<string> GetRepositoriesRootAsync()
    {
        var config = await _store.LoadAsync<AppConfiguration>("config.json");

        if (!string.IsNullOrWhiteSpace(config?.RepositoriesPath))
            return config.RepositoriesPath;

        return AppPaths.ReposPath;
    }

    private static string BuildReleaseName(string templatePattern, string templateName, string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            version = "draft";

        if (string.IsNullOrWhiteSpace(templatePattern))
            templatePattern = $"{templateName} {{version}}";

        return templatePattern.Replace("{version}", version).Trim();
    }

    private static string BuildSafeId(string name)
    {
        var value = name;

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');

        return value.Replace(" ", "_");
    }


    private static string BuildReleaseOutputFolderName(string releaseName, DateTime generatedAt)
    {
        var safeName = SanitizeFolderName(string.IsNullOrWhiteSpace(releaseName) ? "release" : releaseName);
        return $"{safeName}__{generatedAt:yyyy-MM-dd_HHmm}";
    }

    private static DateTime GetReleaseGeneratedAt(Release release)
    {
        return release.ScopeGeneratedAt ?? release.BuiltAt ?? release.CreatedAt;
    }

    private static string GetReleaseOutputFolderName(Release release)
    {
        if (!string.IsNullOrWhiteSpace(release.OutputFolderName))
            return release.OutputFolderName;

        var legacyFolderName = SanitizeFolderName(release.Name);
        try
        {
            var legacyPath = Path.Combine(AppPaths.ReleasesPath, legacyFolderName);
            if (Directory.Exists(legacyPath))
                return legacyFolderName;
        }
        catch
        {
            // Keep output path fallback safe even if AppPaths cannot be resolved.
        }

        return BuildReleaseOutputFolderName(release.Name, GetReleaseGeneratedAt(release));
    }

    private static string BuildReleaseMetadataMarkdown(Release release, Release previousRelease)
    {
        var generatedAt = GetReleaseGeneratedAt(release);
        var builder = new StringBuilder();
        builder.AppendLine($"# Release snapshot metadata");
        builder.AppendLine();
        builder.AppendLine($"Release: {release.Name}");
        builder.AppendLine($"Version: {release.Version}");
        builder.AppendLine($"Template: {release.TemplateName}");
        builder.AppendLine($"Project: {release.ProjectName}");
        builder.AppendLine($"Previous release/baseline: {previousRelease.Name}");
        builder.AppendLine($"Generated at: {generatedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Snapshot date: {generatedAt:yyyy-MM-dd}");
        builder.AppendLine($"Output folder: {GetReleaseOutputFolderName(release)}");
        builder.AppendLine($"Has changes: {(release.HasChanges ? "yes" : "no")}");
        builder.AppendLine($"Changed services: {release.ChangedServicesCount}");
        builder.AppendLine($"Included services: {release.IncludedServicesCount}");
        builder.AppendLine();
        builder.AppendLine("## Services");
        builder.AppendLine();
        builder.AppendLine("| Service | Included | Status | Commits | Files | Target refs |");
        builder.AppendLine("|---|---:|---|---:|---:|---|");

        foreach (var service in release.Services.OrderBy(x => x.ServiceName))
        {
            builder.AppendLine($"| {service.ServiceName} | {(service.Included ? "yes" : "no")} | {service.Status} | {service.CommitCount} | {service.ChangedFilesCount} | {service.TargetRef} |");
        }

        return builder.ToString();
    }

    private static string SanitizeFolderName(string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');

        return value;
    }

    private static bool ContainsIgnoreCase(string? value, string query)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(query, StringComparison.OrdinalIgnoreCase);
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
        return (value ?? "").Trim().ToLowerInvariant().Replace("_", "-");
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        return value
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static async Task<string> TryResolveRefAsync(string localRepoPath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "";

        try
        {
            return await ResolveRefAsync(localRepoPath, reference);
        }
        catch
        {
            if (!reference.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return await ResolveRefAsync(localRepoPath, "v" + reference);
                }
                catch
                {
                    return "";
                }
            }

            return "";
        }
    }

    private static string ShortSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "n/a";

        var trimmed = value.Trim();
        return trimmed.Length <= 8 ? trimmed : trimmed[..8];
    }

    private static string CompactLogMessage(string? message, int maxLength = 320)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "n/a";

        var normalized = message.Replace(Environment.NewLine, " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    // RNH_GIT_DIFF_QUALITY_2026_06_18: git history diagnostics helpers.
    private sealed class GitHistoryCheckResult
    {
        public string MergeBaseSha { get; set; } = "";

        public bool IsPreviousAncestorOfTarget { get; set; }

        public bool IsTargetAncestorOfPrevious { get; set; }

        public string Warning { get; set; } = "";
    }

    private sealed class GitCommandResult
    {
        public int ExitCode { get; set; }

        public string Output { get; set; } = "";

        public string Error { get; set; } = "";
    }

    private static async Task<GitHistoryCheckResult> CheckGitHistoryAsync(string localRepoPath, string previousSha, string targetSha)
    {
        var result = new GitHistoryCheckResult();

        try
        {
            result.MergeBaseSha = (await RunGitAsync(localRepoPath, "merge-base", previousSha, targetSha)).Trim();
            result.IsPreviousAncestorOfTarget = await IsGitAncestorAsync(localRepoPath, previousSha, targetSha);
            result.IsTargetAncestorOfPrevious = await IsGitAncestorAsync(localRepoPath, targetSha, previousSha);

            if (result.IsPreviousAncestorOfTarget)
                return result;

            if (result.IsTargetAncestorOfPrevious)
            {
                result.Warning = "Target SHA is behind previous SHA. The selected target may point to an older commit than the baseline.";
                return result;
            }

            if (!string.IsNullOrWhiteSpace(result.MergeBaseSha))
            {
                result.Warning = $"Previous SHA is not an ancestor of target SHA. History appears diverged. Merge-base is {ShortSha(result.MergeBaseSha)}; diff/log may include unexpected changes.";
                return result;
            }

            result.Warning = "Previous SHA is not an ancestor of target SHA, and merge-base could not be determined.";
            return result;
        }
        catch (Exception ex)
        {
            result.Warning = "Git history validation failed: " + CompactLogMessage(ex.Message);
            return result;
        }
    }

    private static async Task<bool> IsGitAncestorAsync(string localRepoPath, string ancestorSha, string descendantSha)
    {
        var result = await RunGitExitCodeAsync(localRepoPath, "merge-base", "--is-ancestor", ancestorSha, descendantSha);

        if (result.ExitCode == 0)
            return true;

        if (result.ExitCode == 1)
            return false;

        var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        throw new InvalidOperationException(message.Trim());
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


    private static string ToTemplateTagDisplayRef(string tagName)
    {
        var value = tagName.Trim();

        if (value.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            return value;

        if (value.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase))
            value = value["refs/tags/".Length..];

        return $"tag: {value}";
    }

    private static string NormalizeTemplateTargetRefForGit(string targetRef)
    {
        var value = targetRef.Trim();

        if (value.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = value["tag:".Length..].Trim();
            return $"refs/tags/{tag}";
        }

        if (value.StartsWith("branch:", StringComparison.OrdinalIgnoreCase))
            return value["branch:".Length..].Trim();

        return value;
    }

    private static async Task<string> ResolveRefAsync(string localRepoPath, string reference)
    {
        var gitReference = NormalizeTemplateTargetRefForGit(reference);
        var output = await RunGitAsync(localRepoPath, "rev-parse", "--verify", "--end-of-options", $"{gitReference}^{{commit}}");
        return output.Trim();
    }

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitExitCodeAsync(workingDirectory, arguments);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());

        return result.Output;
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;

        RunTemplateScopeButton.IsEnabled = !isBusy;
        GenerateReleaseNotesButton.IsEnabled = !isBusy;
        OpenReleaseFolderButton.IsEnabled = !isBusy;
        SaveTemplateButton.IsEnabled = !isBusy;
        DeleteTemplateButton.IsEnabled = !isBusy;
        TemplatesListBox.IsEnabled = !isBusy;
        TemplateServicesDataGrid.IsEnabled = !isBusy;

        ScopeStatusTextBlock.Text = status;
        ScopeStatusTextBlock.Foreground = isBusy
            ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.Green;
    }

    // RNH_TARGET_LEVEL_ARTIFACTS_FIX_001: write branch/ref-level artifacts for multi-target services.
    private void WriteTargetLevelServiceArtifacts(string releasePath, Release release)
    {
        var serviceArtifactsRoot = Path.Combine(releasePath, "service-artifacts");
        Directory.CreateDirectory(serviceArtifactsRoot);

        var rootIndex = new StringBuilder();
        rootIndex.AppendLine($"# Service target artifacts for {release.Name}");
        rootIndex.AppendLine();

        foreach (var service in release.Services
                     .Where(x => x.CommitCount > 0 || x.ChangedFilesCount > 0 || !string.IsNullOrWhiteSpace(x.TargetRef) || !string.IsNullOrWhiteSpace(x.TargetBranch))
                     .OrderBy(x => x.ServiceName))
        {
            var serviceRoot = Path.Combine(serviceArtifactsRoot, SanitizeArtifactPathSegment(service.ServiceName));
            Directory.CreateDirectory(serviceRoot);

            var targets = SplitTargetRefs(service.TargetRef)
                .Concat(SplitTargetRefs(service.TargetBranch))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (targets.Count == 0)
                continue;

            var targetsRoot = Path.Combine(serviceRoot, "targets");
            Directory.CreateDirectory(targetsRoot);

            var index = new StringBuilder();
            index.AppendLine($"# Target refs for {service.ServiceName}");
            index.AppendLine();
            index.AppendLine($"Release: {release.Name}");
            index.AppendLine($"Service: {service.ServiceName}");
            index.AppendLine($"Previous SHA: {service.PreviousSha}");
            index.AppendLine($"All target refs: {string.Join("; ", targets)}");
            index.AppendLine();
            index.AppendLine("| Target ref | Folder | Commits | Files |");
            index.AppendLine("|---|---|---:|---:|");

            foreach (var targetRef in targets)
            {
                var folderName = SanitizeArtifactPathSegment(targetRef);
                var targetFolder = Path.Combine(targetsRoot, folderName);
                Directory.CreateDirectory(targetFolder);

                var targetCommits = SelectArtifactLinesForTarget(service.Commits, targetRef, targets.Count == 1).ToList();
                var targetFiles = SelectArtifactLinesForTarget(service.ChangedFiles, targetRef, targets.Count == 1).ToList();

                var summary = new StringBuilder();
                summary.AppendLine($"# {service.ServiceName} - {targetRef}");
                summary.AppendLine();
                summary.AppendLine($"Release: {release.Name}");
                summary.AppendLine($"Service: {service.ServiceName}");
                summary.AppendLine($"Target ref: {targetRef}");
                summary.AppendLine($"Previous SHA: {service.PreviousSha}");
                summary.AppendLine($"Target SHA: {PickArtifactMetadataForTarget(service.TargetSha, targetRef)}");
                summary.AppendLine($"Merge-base: {PickArtifactMetadataForTarget(service.MergeBaseSha, targetRef)}");
                summary.AppendLine($"Effective diff base: {PickArtifactMetadataForTarget(service.EffectiveDiffBaseSha, targetRef)}");
                summary.AppendLine($"Diff range: {PickArtifactMetadataForTarget(service.DiffRange, targetRef)}");
                summary.AppendLine($"Diff mode: {service.DiffBaseMode}");
                summary.AppendLine($"Used merge-base for diff: {service.UsedMergeBaseForDiff}");
                summary.AppendLine($"Commits: {targetCommits.Count}");
                summary.AppendLine($"Changed files: {targetFiles.Count}");

                var commits = new StringBuilder();
                commits.AppendLine($"# Commits - {service.ServiceName} - {targetRef}");
                commits.AppendLine();
                if (targetCommits.Count == 0)
                    commits.AppendLine("No commits for this target ref.");
                else
                    foreach (var commit in targetCommits)
                        commits.AppendLine("- " + StripArtifactTargetPrefix(commit));

                var files = new StringBuilder();
                files.AppendLine($"# Changed files - {service.ServiceName} - {targetRef}");
                files.AppendLine();
                if (targetFiles.Count == 0)
                    files.AppendLine("No changed files for this target ref.");
                else
                    foreach (var file in targetFiles)
                        files.AppendLine("- " + StripArtifactTargetPrefix(file));

                var gitInfo = new StringBuilder();
                gitInfo.AppendLine($"# Git info - {service.ServiceName} - {targetRef}");
                gitInfo.AppendLine();
                gitInfo.AppendLine($"Service: {service.ServiceName}");
                gitInfo.AppendLine($"Target ref: {targetRef}");
                gitInfo.AppendLine($"Previous SHA: {service.PreviousSha}");
                gitInfo.AppendLine($"Target SHA: {PickArtifactMetadataForTarget(service.TargetSha, targetRef)}");
                gitInfo.AppendLine($"Merge-base: {PickArtifactMetadataForTarget(service.MergeBaseSha, targetRef)}");
                gitInfo.AppendLine($"Effective diff base: {PickArtifactMetadataForTarget(service.EffectiveDiffBaseSha, targetRef)}");
                gitInfo.AppendLine($"Diff range: {PickArtifactMetadataForTarget(service.DiffRange, targetRef)}");
                gitInfo.AppendLine($"Diff mode: {service.DiffBaseMode}");
                gitInfo.AppendLine($"Used merge-base for diff: {service.UsedMergeBaseForDiff}");
                gitInfo.AppendLine();
                gitInfo.AppendLine("## Warnings");
                var targetWarnings = SelectArtifactLinesForTarget(SplitLines(service.GitHistoryWarning), targetRef, false).ToList();
                if (targetWarnings.Count == 0)
                    gitInfo.AppendLine("No target-specific warnings.");
                else
                    foreach (var warning in targetWarnings)
                        gitInfo.AppendLine("- " + StripArtifactTargetPrefix(warning));

                File.WriteAllText(Path.Combine(targetFolder, "summary.md"), summary.ToString(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(targetFolder, "commits.md"), commits.ToString(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(targetFolder, "changed-files.md"), files.ToString(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(targetFolder, "git-info.md"), gitInfo.ToString(), Encoding.UTF8);

                index.AppendLine($"| {targetRef} | targets/{folderName} | {targetCommits.Count} | {targetFiles.Count} |");
                rootIndex.AppendLine($"- {service.ServiceName} / {targetRef}: service-artifacts/{SanitizeArtifactPathSegment(service.ServiceName)}/targets/{folderName} ({targetCommits.Count} commits, {targetFiles.Count} files)");
            }

            File.WriteAllText(Path.Combine(serviceRoot, "targets-index.md"), index.ToString(), Encoding.UTF8);
        }

        File.WriteAllText(Path.Combine(releasePath, "service-target-artifacts-index.md"), rootIndex.ToString(), Encoding.UTF8);
    }

    private static IEnumerable<string> SelectArtifactLinesForTarget(IEnumerable<string> lines, string targetRef, bool includeUnprefixed)
    {
        foreach (var raw in lines ?? Enumerable.Empty<string>())
        {
            var line = raw?.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var refs = ExtractArtifactTargetRefs(line);
            if (refs.Count == 0)
            {
                if (includeUnprefixed)
                    yield return line;
                continue;
            }

            if (refs.Any(x => string.Equals(x, targetRef, StringComparison.OrdinalIgnoreCase)))
                yield return line;
        }
    }

    private static List<string> ExtractArtifactTargetRefs(string line)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("[", StringComparison.Ordinal))
            return result;

        var end = line.IndexOf(']');
        if (end <= 1)
            return result;

        var refsText = line.Substring(1, end - 1);
        result.AddRange(refsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return result;
    }

    private static string StripArtifactTargetPrefix(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("[", StringComparison.Ordinal))
            return line;

        var end = line.IndexOf(']');
        if (end <= 0 || end + 1 >= line.Length)
            return line;

        return line[(end + 1)..].Trim();
    }

    private static string PickArtifactMetadataForTarget(string? value, string targetRef)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.StartsWith(targetRef + "=", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith(targetRef + ":", StringComparison.OrdinalIgnoreCase))
            {
                return part;
            }
        }

        return value;
    }

    private static string SanitizeArtifactPathSegment(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "_empty" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).Distinct().ToArray();
        foreach (var ch in invalid)
            text = text.Replace(ch, '_');
        text = text.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace(';', '_').Replace(',', '_');
        while (text.Contains("__", StringComparison.Ordinal))
            text = text.Replace("__", "_");
        return text.Trim('_');
    }


    // RNH_SAFE_AI_FALLBACK_AND_GUIDE_033:
    // Keep client-facing fallback files safe when the AI provider fails or quota is exhausted.
    private static void SanitizeFallbackMarkdownOutputs(string outputPath, string releaseName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(outputPath) || !Directory.Exists(outputPath))
                return;

            var servicesPath = Path.Combine(outputPath, "services");

            if (Directory.Exists(servicesPath))
            {
                foreach (var serviceFile in Directory.EnumerateFiles(servicesPath, "*.md", SearchOption.TopDirectoryOnly))
                {
                    var content = File.ReadAllText(serviceFile);

                    if (!ContainsUnsafeAiFallback(content))
                        continue;

                    var serviceName = Path.GetFileNameWithoutExtension(serviceFile);
                    File.WriteAllText(serviceFile, BuildSafeServiceFallbackNotes(serviceName), System.Text.Encoding.UTF8);
                    AppLog.Warning($"Unsafe AI fallback service notes were sanitized. Service='{serviceName}', file='{serviceFile}'.");
                }
            }

            var clientReleaseNotesPath = Path.Combine(outputPath, "client-release-notes.md");
            if (File.Exists(clientReleaseNotesPath))
            {
                var clientContent = File.ReadAllText(clientReleaseNotesPath);

                if (ContainsUnsafeAiFallback(clientContent))
                {
                    File.WriteAllText(clientReleaseNotesPath, BuildSafeClientFallbackNotes(releaseName), System.Text.Encoding.UTF8);
                    AppLog.Warning($"Unsafe AI fallback client release notes were sanitized. File='{clientReleaseNotesPath}'.");
                }
            }

            var combinedPromptPath = Path.Combine(outputPath, "combined-ai-prompt.md");
            if (File.Exists(combinedPromptPath))
            {
                var promptContent = File.ReadAllText(combinedPromptPath);

                if (ContainsUnsafeAiFallback(promptContent))
                {
                    var sanitizedPrompt = SanitizeFallbackSectionsInCombinedPrompt(promptContent);
                    File.WriteAllText(combinedPromptPath, sanitizedPrompt, System.Text.Encoding.UTF8);
                    AppLog.Warning($"Unsafe AI fallback sections were sanitized in combined prompt. File='{combinedPromptPath}'.");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Failed to sanitize AI fallback markdown outputs: " + ex.Message);
        }
    }

    private static bool ContainsUnsafeAiFallback(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        return content.Contains("AI fallback:", StringComparison.OrdinalIgnoreCase) &&
               (content.Contains("Комітів:", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("Merge branch", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("Merge remote-tracking", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("| 2026-", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("https://", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSafeServiceFallbackNotes(string serviceName)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"## {serviceName}");
        builder.AppendLine();
        builder.AppendLine("AI notes for this service were not generated because the AI provider returned an error or quota limit.");
        builder.AppendLine();
        builder.AppendLine("Prompt and technical scope artifacts were saved. Retry AI notes after quota reset or use the saved prompt for manual generation.");
        builder.AppendLine();
        builder.AppendLine("This fallback is intentionally safe and does not include raw commits, authors, hashes, branches, internal URLs, or PTS identifiers.");
        return builder.ToString();
    }

    private static string BuildSafeClientFallbackNotes(string releaseName)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"# {releaseName}");
        builder.AppendLine();
        builder.AppendLine("AI fallback: final client release notes were not generated because the AI provider returned an error or quota limit.");
        builder.AppendLine();
        builder.AppendLine("The scan snapshot and prompt files were saved successfully. Retry AI notes after quota reset or use combined-ai-prompt.md for manual generation.");
        builder.AppendLine();
        builder.AppendLine("This file is not client-ready until AI generation is completed successfully or the content is manually prepared.");
        return builder.ToString();
    }

    private static string SanitizeFallbackSectionsInCombinedPrompt(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var pattern = @"(?ms)^### (?<service>[^\r\n]+)\s+## \k<service>\s+AI fallback:.*?(?=^### |\z)";

        return System.Text.RegularExpressions.Regex.Replace(
            content,
            pattern,
            match =>
            {
                var service = match.Groups["service"].Value.Trim();

                return
                    $"### {service}{Environment.NewLine}" +
                    $"AI notes for this service were not generated because the AI provider returned an error or quota limit.{Environment.NewLine}" +
                    $"Use the saved service prompt and technical scope artifacts for retry/manual generation.{Environment.NewLine}" +
                    $"Raw fallback commits were intentionally removed from this combined prompt.{Environment.NewLine}{Environment.NewLine}";
            });
    }


}
