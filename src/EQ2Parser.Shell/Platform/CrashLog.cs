using System.Globalization;
using System.Reflection;
using System.Text;

namespace EQ2Parser.App.Services;

/// <summary>
/// Crash reports for non-Windows shells: the same folder, file names and
/// report format as the WPF app's CrashLog, minus its WPF dispatcher hook
/// and dialogs. <see cref="Report"/> is what the linked services call for
/// a failure they caught; <see cref="Install"/> catches what nothing did.
/// </summary>
public static class CrashLog
{
    public static string Directory => Path.Combine(AppSettings.Directory, "crashes");

    internal const string FatalKind = "fatal";

    private const string FilePrefix = "crash-";
    private const string StampFormat = "yyyy-MM-dd_HHmmss";
    private const int KeepFiles = 30;

    private static readonly object Gate = new();
    private static DateTime _lastWrite;

    /// <summary>Write a report for an exception nothing caught, before the
    /// runtime ends the process.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(FatalKind, e.ExceptionObject as Exception, always: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("unobserved-task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>A failure that was caught and handled where it happened but
    /// still needs its stack trace on disk. Never throws; returns the file
    /// written, or null when the rate limit (or the disk) swallowed it.
    /// <paramref name="always"/> bypasses the rate limit.</summary>
    public static string? Report(string kind, Exception ex, string? context = null, bool always = false) =>
        Write(kind, ex, context, always);

    internal static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "dev";

    private static string? Write(string kind, Exception? ex, string? context = null, bool always = false)
    {
        try
        {
            lock (Gate)
            {
                var now = DateTime.Now;
                if (!always && kind != FatalKind && now - _lastWrite < TimeSpan.FromSeconds(5))
                    return null;
                _lastWrite = now;
                System.IO.Directory.CreateDirectory(Directory);
                Prune();
                var stem = FilePrefix + now.ToString(StampFormat, CultureInfo.InvariantCulture) + "-" + kind;
                var path = Path.Combine(Directory, stem + ".txt");
                for (var n = 2; File.Exists(path) && n < 100; n++)
                    path = Path.Combine(Directory, $"{stem}-{n}.txt");
                var report = new StringBuilder()
                    .AppendLine($"EQ2Parser {Version} — {kind} exception at {now:yyyy-MM-dd HH:mm:ss}")
                    .AppendLine($"{Environment.OSVersion} · .NET {Environment.Version}")
                    .AppendLine();
                if (!string.IsNullOrEmpty(context))
                    report.AppendLine(context).AppendLine();
                report.Append(ex?.ToString() ?? "(no exception object)");
                File.WriteAllText(path, report.ToString());
                return path;
            }
        }
        catch
        {
            return null;
        }
    }

    private static void Prune()
    {
        try
        {
            foreach (var stale in new DirectoryInfo(Directory).GetFiles("crash-*.txt")
                         .OrderByDescending(f => f.CreationTimeUtc).Skip(KeepFiles - 1))
                stale.Delete();
        }
        catch
        {
            // Pruning is best-effort.
        }
    }
}
