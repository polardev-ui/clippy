using System.Text;

namespace Clippy.Services;

/// <summary>
/// Records unhandled exceptions to a file next to the app's data.
///
/// A WinUI app that throws during startup dies with a stowed exception (0xc000027b) and
/// leaves nothing behind but an address in Event Viewer, which is useless for working out
/// what actually went wrong. Writing the managed exception somewhere the user can find it
/// turns "it doesn't open" into an answerable question.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string Path
    {
        get
        {
            try
            {
                return System.IO.Path.Combine(Models.ClipStorage.AppDataDirectory, "crash.log");
            }
            catch
            {
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clippy-crash.log");
            }
        }
    }

    /// <summary>Subscribes to every unhandled-exception source. Safe to call more than once.</summary>
    public static void Install(Microsoft.UI.Xaml.Application application)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Record("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record("Task", e.Exception);
            e.SetObserved();
        };

        application.UnhandledException += (_, e) =>
        {
            Record("Xaml", e.Exception);

            // Leave Handled alone: swallowing a startup failure would leave the app running
            // in a broken state rather than failing where the user can see it.
        };
    }

    public static void Record(string source, Exception? error)
    {
        if (error == null) return;

        try
        {
            var report = new StringBuilder();
            report.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ===");
            report.AppendLine($"Clippy {typeof(CrashLog).Assembly.GetName().Version}");
            report.AppendLine($"OS {Environment.OSVersion.VersionString} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
            report.AppendLine($"Process {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            report.AppendLine(error.ToString());
            report.AppendLine();

            lock (Gate)
            {
                File.AppendAllText(Path, report.ToString());
            }
        }
        catch
        {
            // Logging a crash must never cause one.
        }

        try
        {
            ClippyDebugLog.Instance.LogError("Crash", error, source);
        }
        catch
        {
        }
    }
}
