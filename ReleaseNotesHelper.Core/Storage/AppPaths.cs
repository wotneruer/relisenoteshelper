using System;
using System.IO;

namespace ReleaseNotesHelper.Core.Storage;

public static class AppPaths
{
    


    public static string BasePath => PortableSettings.DataRoot;
    public static string RepositoriesRoot => PortableSettings.RepositoriesRoot;
// RNH_P0_2026_06_18
    // За замовчуванням дані зберігаються у %LOCALAPPDATA%\ReleaseNotesHelper.
    // Для локального override можна задати змінну середовища RNH_BASE_PATH.

    public static string ReposPath => Path.Combine(BasePath, "repos");
    public static string OutputPath => Path.Combine(BasePath, "output");
    public static string LogsPath => Path.Combine(BasePath, "logs");
    public static string AiRulesPath => Path.Combine(BasePath, "ai-rules");
    public static string ReleasesPath => Path.Combine(OutputPath, "releases");
}
