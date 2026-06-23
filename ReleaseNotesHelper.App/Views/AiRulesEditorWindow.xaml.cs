using System.Diagnostics;
using System.IO;
using System.Text;
using ReleaseNotesHelper.Core.Storage;

namespace ReleaseNotesHelper.App.Views;

public partial class AiRulesEditorWindow : System.Windows.Window
{
    private readonly string _projectName;
    private readonly string _templateName;
    private readonly string _releaseName;
    private readonly List<string> _serviceNames;
    private readonly string _rootPath;
    private readonly string _projectRootPath;

    public AiRulesEditorWindow(string projectName, string templateName, string releaseName, IEnumerable<string> serviceNames)
    {
        InitializeComponent();

        _projectName = string.IsNullOrWhiteSpace(projectName) ? "Default" : projectName.Trim();
        _templateName = templateName?.Trim() ?? "";
        _releaseName = releaseName?.Trim() ?? "";
        _serviceNames = serviceNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

        _rootPath = AppPaths.AiRulesPath;
        _projectRootPath = Path.Combine(_rootPath, "projects", SafePathSegment(_projectName));

        HeaderTextBlock.Text = $"Проект: {_projectName}";
        EnsureScaffold(createServiceFiles: true);
        LoadRuleList();
    }

    private sealed class RuleFileItem
    {
        public string GroupName { get; init; } = "";
        public string Label { get; init; } = "";
        public string FullPath { get; init; } = "";
        public string RelativePath { get; init; } = "";
    }

    private void LoadRuleList()
    {
        var items = new List<RuleFileItem>();

        AddRule(items, Path.Combine(_rootPath, "global.md"), "Global rules");
        AddRule(items, Path.Combine(_projectRootPath, "project-rules.md"), "Project rules");
        AddRule(items, Path.Combine(_projectRootPath, "glossary.md"), "Glossary");
        AddRule(items, Path.Combine(_projectRootPath, "service-release-notes.md"), "Service notes rules");
        AddRule(items, Path.Combine(_projectRootPath, "final-release-notes.md"), "Final notes rules");

        foreach (var serviceName in _serviceNames)
            AddRule(items, Path.Combine(_projectRootPath, "services", SafePathSegment(serviceName) + ".md"), "Service: " + serviceName);

        if (!string.IsNullOrWhiteSpace(_releaseName))
            AddRule(items, Path.Combine(_projectRootPath, "releases", SafePathSegment(_releaseName) + ".md"), "Release: " + _releaseName);

        if (!string.IsNullOrWhiteSpace(_templateName))
            AddRule(items, Path.Combine(_projectRootPath, "templates", SafePathSegment(_templateName) + ".md"), "Template: " + _templateName);

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(items);
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(RuleFileItem.GroupName)));

        RulesListBox.ItemsSource = view;

        if (items.Count > 0 && RulesListBox.SelectedItem == null)
            RulesListBox.SelectedIndex = 0;
    }

    private void AddRule(List<RuleFileItem> items, string fullPath, string label)
    {
        items.Add(new RuleFileItem
        {
            GroupName = GetRuleGroupName(label),
            Label = label,
            FullPath = fullPath,
            RelativePath = MakeRelative(fullPath)
        });
    }

    private static string GetRuleGroupName(string label)
    {
        if (label.Equals("Global rules", StringComparison.OrdinalIgnoreCase))
            return "Global rules";

        if (label.Equals("Project rules", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Glossary", StringComparison.OrdinalIgnoreCase))
        {
            return "Project context";
        }

        if (label.Equals("Service notes rules", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("Final notes rules", StringComparison.OrdinalIgnoreCase))
        {
            return "Output format rules";
        }

        if (label.StartsWith("Service:", StringComparison.OrdinalIgnoreCase))
            return "Service-specific rules";

        if (label.StartsWith("Release:", StringComparison.OrdinalIgnoreCase))
            return "Release-specific rules";

        if (label.StartsWith("Template:", StringComparison.OrdinalIgnoreCase))
            return "Template-specific rules";

        return "Other rules";
    }

    private string MakeRelative(string fullPath)
    {
        try
        {
            return Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
        }
        catch
        {
            return fullPath;
        }
    }

    private void RulesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        LoadSelectedRule();
    }

    private void LoadSelectedRule()
    {
        if (RulesListBox.SelectedItem is not RuleFileItem item)
        {
            SelectedRuleTitleTextBlock.Text = "Файл не вибрано";
            SelectedRulePathTextBlock.Text = "";
            RuleTextBox.Text = "";
            return;
        }

        SelectedRuleTitleTextBlock.Text = item.Label;
        SelectedRulePathTextBlock.Text = item.RelativePath;

        if (!File.Exists(item.FullPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.FullPath) ?? _projectRootPath);
            File.WriteAllText(item.FullPath, BuildDefaultFileContent(item), Encoding.UTF8);
        }

        RuleTextBox.Text = File.ReadAllText(item.FullPath, Encoding.UTF8);
        StatusTextBlock.Text = "Завантажено";
    }

    private void SaveRule_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RulesListBox.SelectedItem is not RuleFileItem item)
            return;

        if (!IsPathUnderRoot(item.FullPath, _rootPath))
        {
            System.Windows.MessageBox.Show(
                "Файл правил має бути всередині каталогу ai-rules.",
                "AI rules",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(item.FullPath) ?? _projectRootPath);
        File.WriteAllText(item.FullPath, RuleTextBox.Text ?? "", Encoding.UTF8);
        StatusTextBlock.Text = "Збережено: " + item.RelativePath;
    }

    private void ReloadRule_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        LoadSelectedRule();
    }

    private void CreateMissingRules_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        EnsureScaffold(createServiceFiles: true);
        LoadRuleList();
        StatusTextBlock.Text = "Створено відсутні файли правил";
    }

    private void OpenRulesFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        EnsureScaffold(createServiceFiles: false);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "\"" + _projectRootPath + "\"",
            UseShellExecute = true
        });
    }

    private void EnsureScaffold(bool createServiceFiles)
    {
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_projectRootPath);
        Directory.CreateDirectory(Path.Combine(_projectRootPath, "services"));
        Directory.CreateDirectory(Path.Combine(_projectRootPath, "releases"));
        Directory.CreateDirectory(Path.Combine(_projectRootPath, "templates"));

        WriteFileIfMissing(Path.Combine(_rootPath, "global.md"), "# Global AI rules\n\n- Не згадуй Git, hash комітів, гілки, авторів та внутрішні URL у клієнтських release notes.\n- Пиши коротко, предметно і без маркетингового вступу.\n");
        WriteFileIfMissing(Path.Combine(_projectRootPath, "project-rules.md"), $"# {_projectName} — project rules\n\n- Опиши зміни мовою користувацької цінності.\n- Не вигадуй функціональність, якої немає у вхідних змінах.\n");
        WriteFileIfMissing(Path.Combine(_projectRootPath, "glossary.md"), BuildDefaultGlossary(_projectName));
        WriteFileIfMissing(Path.Combine(_projectRootPath, "service-release-notes.md"), "# Service release notes rules\n\n- Починай одразу зі списку змін.\n- Групуй технічно схожі коміти в один клієнтський пункт.\n");
        WriteFileIfMissing(Path.Combine(_projectRootPath, "final-release-notes.md"), "# Final release notes rules\n\n- Об'єднуй дублікати між сервісами.\n- Не згадуй назви сервісів, якщо це не потрібно клієнту.\n");

        if (createServiceFiles)
        {
            foreach (var serviceName in _serviceNames)
            {
                WriteFileIfMissing(
                    Path.Combine(_projectRootPath, "services", SafePathSegment(serviceName) + ".md"),
                    $"# {serviceName}\n\n- Додай правила формулювання release notes для цього сервісу за потреби.\n");
            }
        }

        if (!string.IsNullOrWhiteSpace(_releaseName))
            WriteFileIfMissing(Path.Combine(_projectRootPath, "releases", SafePathSegment(_releaseName) + ".md"), $"# {_releaseName}\n\n- Додай правила для цього релізу за потреби.\n");

        if (!string.IsNullOrWhiteSpace(_templateName))
            WriteFileIfMissing(Path.Combine(_projectRootPath, "templates", SafePathSegment(_templateName) + ".md"), $"# {_templateName}\n\n- Додай правила для цього шаблону за потреби.\n");
    }

    private string BuildDefaultFileContent(RuleFileItem item)
    {
        if (item.Label.StartsWith("Service:", StringComparison.OrdinalIgnoreCase))
            return "# " + item.Label.Replace("Service:", "").Trim() + "\n\n- Додай правила формулювання release notes для цього сервісу за потреби.\n";

        return "# " + item.Label + "\n\n- Додай правила за потреби.\n";
    }

    private static void WriteFileIfMissing(string path, string content)
    {
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppPaths.AiRulesPath);
        File.WriteAllText(path, content, Encoding.UTF8);
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

    private static string SafePathSegment(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Default" : value.Trim();

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');

        return value.Replace('/', '_').Replace('\\', '_');
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
