using System.Text.Json;

namespace ReleaseNotesHelper.Core.Storage;

public sealed class PortableAppSettings
{
    public string DataRoot { get; set; } = @".\data";

    public string RepositoriesRoot { get; set; } = "";

    public string AiProvider { get; set; } = "Gemini";

    public bool HideTechnicalDataFromAi { get; set; } = true;
}

public static class PortableSettings
{
    private static readonly object Sync = new();
    private static PortableAppSettings? _settings;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string AppDirectory => AppContext.BaseDirectory;

    public static string SettingsFilePath => Path.Combine(AppDirectory, "settings.json");

    public static PortableAppSettings Current
    {
        get
        {
            lock (Sync)
            {
                if (_settings is not null)
                    return _settings;

                _settings = LoadOrCreate();
                return _settings;
            }
        }
    }

    public static string DataRoot
    {
        get
        {
            var value = ResolvePath(Current.DataRoot, AppDirectory);

            if (string.IsNullOrWhiteSpace(value))
                value = Path.Combine(AppDirectory, "data");

            Directory.CreateDirectory(value);
            return value;
        }
    }

    public static string RepositoriesRoot
    {
        get
        {
            var value = ResolvePath(Current.RepositoriesRoot, AppDirectory);
            return value;
        }
    }

    public static void Reload()
    {
        lock (Sync)
        {
            _settings = LoadOrCreate();
        }
    }

    public static void Save()
    {
        lock (Sync)
        {
            Directory.CreateDirectory(AppDirectory);
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
    }

    public static string ResolvePath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());

        if (Path.IsPathRooted(expanded))
            return Path.GetFullPath(expanded);

        return Path.GetFullPath(Path.Combine(baseDirectory, expanded));
    }

    private static PortableAppSettings LoadOrCreate()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<PortableAppSettings>(json, JsonOptions);

                if (settings is not null)
                {
                    if (string.IsNullOrWhiteSpace(settings.DataRoot))
                        settings.DataRoot = @".\data";

                    return settings;
                }
            }
        }
        catch
        {
            // If settings.json is broken, start with safe defaults.
        }

        var created = new PortableAppSettings();
        Directory.CreateDirectory(AppDirectory);
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(created, JsonOptions));
        return created;
    }
}
