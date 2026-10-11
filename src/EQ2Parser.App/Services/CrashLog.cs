using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using EQ2Parser.App.Localization;

namespace EQ2Parser.App.Services;

/// <summary>
/// Last-resort exception capture: every unhandled exception is written to
/// %LocalAppData%\EQ2Parser\crashes\ before anything else happens, so "the
/// app just died" always leaves evidence with a stack trace.
///
/// UI-thread exceptions are logged and swallowed — mid-raid, a broken
/// button beats a dead parser. Genuinely fatal
/// exceptions still take the process down, but log on the way out. The
/// exception to the swallow rule: five UI exceptions inside a minute is a
/// crash LOOP (a broken template re-throws per frame) — the app exits
/// hard with a crash-loop report instead of wedging unclosable.
///
/// Failures that are CAUGHT where they happen but must still leave a stack
/// trace (a log line the pump had to skip, a log reader that died) come in
/// through <see cref="Report"/>, and a startup that threw through
/// <see cref="ReportStartupFailure"/> — same folder, same format, their own
/// kinds.
/// </summary>
public static class CrashLog
{
    public static string Directory => Path.Combine(AppSettings.Directory, "crashes");

    /// <summary>The kinds whose report is the process's last words. They
    /// bypass the rate limit, and the next launch's postmortem reads them
    /// as "this is why the session ended" (see SessionJournal).</summary>
    internal const string FatalKind = "fatal";
    internal const string CrashLoopKind = "crash-loop";
    internal const string StartupKind = "fatal-startup";

    private const string FilePrefix = "crash-";
    private const string StampFormat = "yyyy-MM-dd_HHmmss";

    private const int KeepFiles = 30;

    /// <summary>Serializes <see cref="Write"/>: reports now arrive from the
    /// log pump threads as well as the UI thread and the finalizer, and two
    /// writers inside the same second would otherwise both pass the rate
    /// limit and race for one file name.</summary>
    private static readonly object Gate = new();
    private static DateTime _lastWrite;
    private static DateTime _lastDialog;

    /// <summary>UI-thread exceptions inside this window count toward the
    /// crash-loop verdict. A broken template re-throws every render pass,
    /// so a genuine loop trips this in a second or two; five distinct
    /// one-off errors inside a minute is a session that's beyond saving
    /// anyway.</summary>
    private static readonly Queue<DateTime> _uiStrikes = new();
    private const int LoopStrikes = 5;
    private static readonly TimeSpan LoopWindow = TimeSpan.FromSeconds(60);

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            // Swallowing is only safe for one-off errors. A RECURRING
            // exception (broken template, poisoned layout) re-fires every
            // frame — swallowing again wedges the app in an unclosable
            // dialog/log loop. Detect the repeat and fail fast with
            // evidence instead.
            var now = DateTime.UtcNow;
            _uiStrikes.Enqueue(now);
            while (_uiStrikes.Count > 0 && now - _uiStrikes.Peek() > LoopWindow)
                _uiStrikes.Dequeue();
            if (_uiStrikes.Count >= LoopStrikes)
            {
                var loopPath = Write(CrashLoopKind, e.Exception);
                _lastDialog = DateTime.MinValue; // the last words always show
                ShowDialog("CrashDlg_CrashLoop", e.Exception, loopPath);
                Environment.Exit(70);
                return;
            }
            var path = Write("ui-thread", e.Exception);
            // Handled BEFORE the dialog so a MessageBox failure can't re-crash.
            e.Handled = true;
            ShowDialog("CrashDlg_KeptRunning", e.Exception, path);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var path = Write(FatalKind, ex);
            if (ex is not null)
                ShowDialog("CrashDlg_Fatal", ex, path);
        };
        // Background tasks that die unobserved don't crash modern .NET —
        // they just vanish. Log them so silent failures are findable too.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("background-task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>A failure that was caught and handled where it happened
    /// but still needs its stack trace on disk. Never throws; returns the
    /// file written, or null when the rate limit (or the disk) swallowed
    /// it. <paramref name="context"/> is printed above the exception — say
    /// what was being done, and keep the token and anything like it out
    /// (SECURITY.md). <paramref name="always"/> bypasses the rate limit:
    /// only for reports that are bounded by construction (one per source
    /// that stops), never for anything that can repeat per line.</summary>
    public static string? Report(string kind, Exception ex, string? context = null, bool always = false) =>
        Write(kind, ex, context, always);

    /// <summary>App.OnStartup threw. The report is this launch's last
    /// words, so it always lands.</summary>
    public static string? ReportStartupFailure(Exception ex) => Write(StartupKind, ex);

    /// <summary>Tell the user the launch failed — once, and regardless of
    /// the dialog rate limit: the process exits right after, so this is
    /// the only explanation they will get.</summary>
    public static void ShowStartupFailure(Exception ex, string? reportPath)
    {
        _lastDialog = DateTime.MinValue; // the last words always show
        ShowDialog("CrashDlg_StartupFailed", ex, reportPath);
    }

    /// <summary>The words of the dialogs, in English, by dictionary key.
    /// The dialogs are interface text and are translated like any other;
    /// the report FILE is not — it is read by whoever debugs the crash.
    ///
    /// The English lives here as well as in strings.en.json because a
    /// crash can arrive before the dictionaries do (a startup that threw
    /// while loading the settings), and the one explanation the user gets
    /// must not be a raw key. LocalizedTextTests holds the two copies
    /// equal; LocalizationTests requires every key here in every language.</summary>
    internal static readonly IReadOnlyDictionary<string, string> DialogEnglish = new Dictionary<string, string>
    {
        ["CrashDlg_Title"] = "EQ2Parser — error report",
        ["CrashDlg_KeptRunning"] = "EQ2Parser hit an unexpected error and kept running.",
        ["CrashDlg_CrashLoop"] = "EQ2Parser hit the same error repeatedly and has to close.",
        ["CrashDlg_Fatal"] = "EQ2Parser hit a fatal error and has to close.",
        ["CrashDlg_StartupFailed"] = "EQ2Parser could not start and has to close.",
        ["CrashDlg_DetailsSavedTo"] = "Details saved to:",
    };

    /// <summary>One dialog string in the interface language, or its
    /// English when the dictionaries cannot supply it.</summary>
    internal static string DialogText(string key) => OrEnglish(key, Loc.Get(key));

    /// <summary><paramref name="resolved"/> is what the dictionaries gave
    /// for <paramref name="key"/>; a lookup that failed gives the key back.</summary>
    internal static string OrEnglish(string key, string resolved) =>
        string.Equals(resolved, key, StringComparison.Ordinal) && DialogEnglish.TryGetValue(key, out var english)
            ? english
            : resolved;

    /// <summary>The dialog body: the headline, the exception's own message
    /// (never translated — it is what the developer searches for), and
    /// where the report was written.</summary>
    internal static string DialogBody(string headlineKey, string message, string location) =>
        $"{DialogText(headlineKey)}\n\n{message}\n\n{DialogText("CrashDlg_DetailsSavedTo")}\n{location}";

    /// <summary>True for the kinds that end the process.</summary>
    internal static bool EndsProcess(string kind) => kind is FatalKind or CrashLoopKind or StartupKind;

    /// <summary>Reads a report's file name back into when it was written
    /// (local time, whole seconds) and its kind — the postmortem uses this
    /// to tell which reports belong to the session that died. Tolerates the
    /// "-2" a same-second sibling gets.</summary>
    internal static bool TryParseReportName(string fileName, out DateTime written, out string kind)
    {
        written = default;
        kind = "";
        var name = Path.GetFileNameWithoutExtension(fileName);
        // crash-<stamp>-<kind>, with at least one character of kind.
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal)
            || name.Length < FilePrefix.Length + StampFormat.Length + 2
            || name[FilePrefix.Length + StampFormat.Length] != '-')
            return false;
        if (!DateTime.TryParseExact(
                name.AsSpan(FilePrefix.Length, StampFormat.Length), StampFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out written))
            return false;
        kind = name[(FilePrefix.Length + StampFormat.Length + 1)..];
        var lastDash = kind.LastIndexOf('-');
        if (lastDash > 0 && lastDash < kind.Length - 1 && kind.AsSpan(lastDash + 1).IndexOfAnyExceptInRange('0', '9') < 0)
            kind = kind[..lastDash];
        return true;
    }

    /// <summary>Test seam: forget the last write so a test's report is not
    /// swallowed by the rate limit of the test before it.</summary>
    internal static void ResetRateLimit()
    {
        lock (Gate)
        {
            _lastWrite = default;
        }
    }

    /// <summary>Never throws — the crash logger crashing is the one
    /// failure mode this class exists to prevent. Rate-limited so an
    /// exception storm (per-frame render errors) can't flood the disk.</summary>
    private static string? Write(string kind, Exception? ex, string? context = null, bool always = false)
    {
        try
        {
            lock (Gate)
            {
                var now = DateTime.Now;
                // The rate limit tames exception storms — but the FATAL and
                // CRASH-LOOP reports are the process's last words and must
                // always land, even when a storm preceded them.
                if (!always && !EndsProcess(kind) && now - _lastWrite < TimeSpan.FromSeconds(5))
                    return null;
                _lastWrite = now;
                System.IO.Directory.CreateDirectory(Directory);
                Prune();
                // Two reports of one kind inside one second (several log
                // sources dying together when a drive drops) must not
                // overwrite each other.
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

    private static void ShowDialog(string headlineKey, Exception ex, string? path)
    {
        var now = DateTime.Now;
        if (now - _lastDialog < TimeSpan.FromSeconds(30))
            return;
        _lastDialog = now;
        try
        {
            MessageBox.Show(
                DialogBody(headlineKey, ex.Message, path ?? Directory),
                DialogText("CrashDlg_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch
        {
            // A dead dialog is fine; the log file is what matters.
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

    internal static string Version =>
        typeof(CrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CrashLog).Assembly.GetName().Version?.ToString()
        ?? "dev";
}
