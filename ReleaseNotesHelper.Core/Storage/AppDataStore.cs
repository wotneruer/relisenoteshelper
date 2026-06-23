using System.Text.Json;
using ReleaseNotesHelper.Core.Models;

namespace ReleaseNotesHelper.Core.Storage;

public class AppDataStore
{
    private readonly string _basePath;

    public AppDataStore()
    {
        _basePath = AppPaths.BasePath;
        Directory.CreateDirectory(_basePath);
    }

    public async Task SaveAsync<T>(string fileName, T data)
    {
        var path = Path.Combine(_basePath, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _basePath);

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // RNH_P0_2026_06_18: atomic write + backup.
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";

        await File.WriteAllTextAsync(tempPath, json);

        if (File.Exists(path))
            File.Copy(path, backupPath, overwrite: true);

        File.Move(tempPath, path, overwrite: true);
    }

    public async Task<T?> LoadAsync<T>(string fileName)
    {
        var path = Path.Combine(_basePath, fileName);

        if (!File.Exists(path))
            return default;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException) when (File.Exists(path + ".bak"))
        {
            var backupJson = await File.ReadAllTextAsync(path + ".bak");
            return JsonSerializer.Deserialize<T>(backupJson);
        }
    }

    public string GetBasePath()
    {
        return _basePath;
    }
}
