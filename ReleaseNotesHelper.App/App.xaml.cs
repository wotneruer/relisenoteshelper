using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ReleaseNotesHelper.Core.Storage;
using WpfMessageBox = System.Windows.MessageBox;

namespace ReleaseNotesHelper.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandlers();
        base.OnStartup(e);

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            HandleStartupException(ex);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("dispatcher-unhandled", args.Exception);
            WpfMessageBox.Show(BuildUserMessage(args.Exception), "Release Notes Helper startup/runtime error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(-1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                WriteCrashLog("appdomain-unhandled", ex);
            }
            else
            {
                WriteCrashLog("appdomain-unhandled", new Exception(args.ExceptionObject?.ToString() ?? "Unknown non-Exception error"));
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("task-unobserved", args.Exception);
            args.SetObserved();
        };
    }

    private void HandleStartupException(Exception ex)
    {
        WriteCrashLog("startup", ex);
        WpfMessageBox.Show(BuildUserMessage(ex), "Release Notes Helper startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(-1);
    }

    private static string BuildUserMessage(Exception ex)
    {
        return "Application failed to start.\n\n" +
               ex.GetType().FullName + ": " + ex.Message + "\n\n" +
               "Full details were written to " + Path.Combine(AppPaths.LogsPath, "startup-crash-*.log");
    }

    private static void WriteCrashLog(string kind, Exception ex)
    {
        try
        {
            var logDir = AppPaths.LogsPath;
            Directory.CreateDirectory(logDir);

            var file = Path.Combine(logDir, $"startup-crash-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.log");
            var sb = new StringBuilder();
            sb.AppendLine("ReleaseNotesHelper crash log");
            sb.AppendLine("Kind: " + kind);
            sb.AppendLine("Time: " + DateTime.Now.ToString("O"));
            sb.AppendLine("BaseDirectory: " + AppContext.BaseDirectory);
            sb.AppendLine();
            sb.AppendLine(ex.ToString());

            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Avoid secondary crash while handling the original error.
        }
    }
}
