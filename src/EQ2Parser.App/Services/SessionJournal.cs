using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace EQ2Parser.App.Services;

/// <summary>
/// Evidence for deaths the CLR never sees. <see cref="CrashLog"/> catches
/// every MANAGED escape route, but a native access violation, a stack
/// overflow, an OOM kill, or an antivirus terminating the process leaves
/// the crashes folder empty — reported in the field as "the app just
/// closes, no error". Three nets, all best-effort and silent on failure:
///
///  1. Session journal (%LocalAppData%\EQ2Parser\session.log): coarse
///     breadcrumbs (startup stages, sources, raid-file writes, TTS calls)
///     plus a once-a-minute heartbeat with memory numbers. The last lines
///     before a dirty death say what the app was doing (and the heartbeat
///     trend catches OOM creep).
///  2. WER LocalDumps self-registration (HKCU — no admin), keyed on the
///     RUNNING image name: Windows itself writes a minidump to
///     crashes\dumps on any native crash or stack overflow, exactly the
///     deaths we can't catch in-process. The journal records the request,
///     so a later postmortem knows whether "no dump" means anything.
///  3. Startup post-mortem: a previous session.log without the clean-exit
///     mark means the last run died. Its journal is copied into the
///     crashes folder and the Windows Application event log is searched
///     for matching Application Error / .NET Runtime / WER entries
///     (faulting module + exception code) — the user just sends the
///     crashes folder instead of spelunking Event Viewer. The header names
///     any crash report that session wrote: a fatal, crash-loop or
///     failed-startup exit skips the clean-exit mark too, and those DO
///     leave a managed report.
///
/// An empty postmortem (no crash report, no event-log entry, no dump,
/// journal simply stops) is itself diagnostic: outright process
/// termination, i.e. antivirus.
/// </summary>
public static class SessionJournal
{
    private static readonly object Gate = new();
    private const string CleanExitMark = "clean-exit";
    private const string StampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private const string LocalDumpsKey = @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    /// <summary>The image name earlier builds registered. No shipped
    /// executable is called that (the apphost is named after the
    /// assembly), so the key matched nothing — it is ours, and it goes.</summary>
    private const string StaleDumpImage = "EQ2Parser.exe";

    /// <summary>Journal breadcrumb left when the dump request was written.
    /// The NEXT launch's postmortem looks for it: only a session that asked
    /// for dumps can read anything into not having one.</summary>
    internal const string DumpsRequestedMark = "crash dumps requested for ";

    public static string FilePath => Path.Combine(AppSettings.Directory, "session.log");

    private static DateTime _lastHeartbeat;
    private static DateTime _lastTts;

    /// <summary>Call once, immediately after CrashLog.Install — everything
    /// here must survive a startup crash one line later.</summary>
    public static void Begin()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            var previous = ReadPreviousSession();
            // Taken BEFORE this session can write anything: it is the upper
            // bound that keeps this launch's own crash reports out of the
            // previous session's postmortem.
            var startedAt = DateTime.Now;
            lock (Gate)
            {
                File.WriteAllText(FilePath,
                    Line($"session start — EQ2Parser {CrashLog.Version} · {Environment.OSVersion} · .NET {Environment.Version}"));
            }
            RegisterCrashDumps();
            if (previous is not null)
                _ = Task.Run(() => WritePostmortem(previous, startedAt));
        }
        catch
        {
            // The journal must never be the thing that breaks startup.
        }
    }

    /// <summary>Timestamped breadcrumb; cheap enough for coarse events
    /// (startup stages, source add/remove, command-file writes).</summary>
    public static void Mark(string what)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, Line(what));
        }
        catch
        {
        }
    }

    /// <summary>Piggybacks the shell tick: at most one line a minute, with
    /// the numbers that expose a leak marching toward an OOM kill.</summary>
    public static void Heartbeat(int sourceCount)
    {
        var now = DateTime.UtcNow;
        if (now - _lastHeartbeat < TimeSpan.FromMinutes(1))
            return;
        _lastHeartbeat = now;
        long workingSet;
        try
        {
            using var proc = Process.GetCurrentProcess();
            workingSet = proc.WorkingSet64;
        }
        catch
        {
            workingSet = 0;
        }
        Mark($"heartbeat ws={workingSet >> 20}MB gc={GC.GetTotalMemory(false) >> 20}MB sources={sourceCount}");
    }

    /// <summary>The native TTS engine is a prime silent-crash suspect —
    /// mark entry into it (rate-limited; the mark BEFORE the native call
    /// is the breadcrumb that survives).</summary>
    public static void MarkTts(string what)
    {
        var now = DateTime.UtcNow;
        if (now - _lastTts < TimeSpan.FromSeconds(10))
            return;
        _lastTts = now;
        Mark(what);
    }

    /// <summary>Call last in OnExit.</summary>
    public static void End() => Mark(CleanExitMark);

    /// <summary>Invariant on purpose: the postmortem parses the stamp back,
    /// and a current-culture format writes a different YEAR on a machine
    /// whose calendar is not Gregorian.</summary>
    private static string Line(string what) =>
        $"[{DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture)}] {what}{Environment.NewLine}";

    /// <summary>The previous run's journal when it died dirty; null when
    /// there is nothing to report.</summary>
    private static string? ReadPreviousSession()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var text = File.ReadAllText(FilePath);
            return text.Contains(CleanExitMark, StringComparison.Ordinal) || text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static void WritePostmortem(string previousJournal, DateTime nextSessionStart)
    {
        try
        {
            var start = SessionStart(previousJournal);
            var (ending, other) = start is { } from
                ? ReportsFromSession(ReportFileNames(), from, nextSessionStart)
                : ([], []);
            var report = new StringBuilder()
                .Append(PostmortemHeader(
                    startKnown: start is not null,
                    ending,
                    other,
                    dumpsRequested: previousJournal.Contains(DumpsRequestedMark, StringComparison.Ordinal),
                    dumps: start is { } since ? DumpsSince(since) : []))
                .AppendLine()
                .AppendLine("── previous session journal ──────────────────────────────────────────")
                .AppendLine(previousJournal.TrimEnd())
                .AppendLine()
                .AppendLine("── Windows Application event log (last 48h, EQ2Parser-related) ───────");
            HarvestEventLog(report);
            Directory.CreateDirectory(CrashLog.Directory);
            File.WriteAllText(
                Path.Combine(CrashLog.Directory, $"postmortem-{DateTime.Now:yyyy-MM-dd_HHmmss}.txt"),
                report.ToString());
        }
        catch
        {
        }
    }

    /// <summary>When the journal's session began — its first line's stamp,
    /// or null when that line is missing or unreadable (a torn file, or a
    /// journal an older build wrote under a non-Gregorian calendar).</summary>
    internal static DateTime? SessionStart(string journal)
    {
        // "[" + stamp + "]"
        if (journal.Length < StampFormat.Length + 2 || journal[0] != '[' || journal[StampFormat.Length + 1] != ']')
            return null;
        return DateTime.TryParseExact(
            journal.AsSpan(1, StampFormat.Length), StampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? start
            : null;
    }

    /// <summary>The crash reports written while the dead session ran: from
    /// its start up to (not including) the second this launch began. Split
    /// into the ones that END a process (fatal, crash-loop, failed startup
    /// — each explains the missing clean-exit mark on its own) and the
    /// ones the app survived (a UI error it swallowed, a log line it
    /// skipped). Report names carry whole seconds, so both bounds are
    /// floored to match.</summary>
    internal static (List<string> Ending, List<string> Other) ReportsFromSession(
        IEnumerable<string> reportFileNames, DateTime sessionStart, DateTime nextSessionStart)
    {
        var from = WholeSecond(sessionStart);
        var until = WholeSecond(nextSessionStart);
        List<string> ending = [];
        List<string> other = [];
        foreach (var file in reportFileNames.Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!CrashLog.TryParseReportName(name, out var written, out var kind)
                || written < from || written >= until)
                continue;
            (CrashLog.EndsProcess(kind) ? ending : other).Add(name);
        }
        return (ending, other);
    }

    private static DateTime WholeSecond(DateTime time) =>
        new(time.Ticks - (time.Ticks % TimeSpan.TicksPerSecond), time.Kind);

    /// <summary>The words above the journal. They must fit what the session
    /// actually left behind: "no managed crash report" used to be printed
    /// even with a fatal report sitting right beside this file, which sent
    /// the reader hunting for an antivirus.</summary>
    internal static string PostmortemHeader(
        bool startKnown,
        IReadOnlyList<string> ending,
        IReadOnlyList<string> other,
        bool dumpsRequested,
        IReadOnlyList<string> dumps)
    {
        var header = new StringBuilder();
        if (ending.Count > 0)
        {
            header
                .AppendLine("The previous EQ2Parser session ended WITHOUT a clean exit, and it left a")
                .AppendLine("managed crash report — start there, it has the exception and its stack")
                .AppendLine("trace:");
            AppendNames(header, ending);
            header
                .AppendLine("Below, for context: that session's journal, then any matching Windows")
                .AppendLine("event-log entries.");
        }
        else if (!startKnown)
        {
            // Without a start time nothing can be matched to the session —
            // say so rather than claim there is no report.
            header
                .AppendLine("The previous EQ2Parser session ended WITHOUT a clean exit. Its journal")
                .AppendLine("does not say when it started, so this report cannot tell whether a")
                .AppendLine("crash-*.txt beside it belongs to that session — look for one written")
                .AppendLine("shortly before this file. If there is none: a native crash, stack")
                .AppendLine("overflow, OOM kill, or an external process termination (antivirus).")
                .AppendLine("Below: the session journal's final breadcrumbs, then any matching")
                .AppendLine("Windows event-log entries.");
            return header.ToString();
        }
        else
        {
            header
                .AppendLine("The previous EQ2Parser session ended WITHOUT a clean exit and left no")
                .AppendLine("managed crash report that explains it — a native crash, stack overflow,")
                .AppendLine("OOM kill, or an external process termination (antivirus). Below: the")
                .AppendLine("session journal's final breadcrumbs, then any matching Windows")
                .AppendLine("event-log entries.");
        }
        if (other.Count > 0)
        {
            header
                .AppendLine()
                .AppendLine("While it ran, that session also wrote these error reports. The app")
                .AppendLine("carried on after each of them, so none is the reason it ended — but")
                .AppendLine("they may be related:");
            AppendNames(header, other);
        }
        if (dumps.Count > 0)
        {
            header
                .AppendLine()
                .AppendLine("Windows saved a crash dump during that session (crashes\\dumps):");
            AppendNames(header, dumps);
        }
        else if (ending.Count == 0 && dumpsRequested)
        {
            header
                .AppendLine()
                .AppendLine("That session had asked Windows for a dump on a native crash, and")
                .AppendLine("crashes\\dumps has no new .dmp. If the event-log section is empty as")
                .AppendLine("well, the likeliest cause is a termination from outside (check the")
                .AppendLine("AV's protection history).");
        }
        else if (ending.Count == 0)
        {
            // A session that never asked for dumps (older builds registered
            // the wrong executable name) proves nothing by not having one.
            header
                .AppendLine()
                .AppendLine("That session did not record asking Windows for crash dumps (older")
                .AppendLine("builds registered the wrong executable name), so a missing .dmp in")
                .AppendLine("crashes\\dumps says nothing either way. An empty event-log section")
                .AppendLine("still points at a termination from outside (check the AV's")
                .AppendLine("protection history).");
        }
        return header.ToString();
    }

    private static void AppendNames(StringBuilder header, IReadOnlyList<string> names)
    {
        foreach (var name in names)
            header.AppendLine($"  {name}");
    }

    private static string[] ReportFileNames()
    {
        try
        {
            return Directory.Exists(CrashLog.Directory) ? Directory.GetFiles(CrashLog.Directory, "crash-*.txt") : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Dumps Windows wrote since the dead session began. No upper
    /// bound needed: this launch is moments old and has not crashed.</summary>
    private static List<string> DumpsSince(DateTime sessionStart)
    {
        try
        {
            var folder = new DirectoryInfo(Path.Combine(CrashLog.Directory, "dumps"));
            return folder.Exists
                ? [.. folder.GetFiles("*.dmp").Where(f => f.LastWriteTime >= sessionStart).Select(f => f.Name).Order(StringComparer.Ordinal)]
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static void HarvestEventLog(StringBuilder report)
    {
        try
        {
            using var log = new EventLog("Application");
            var cutoff = DateTime.Now.AddHours(-48);
            var entries = log.Entries;
            var found = 0;
            // Newest-first, bounded scan — the Application log can be huge.
            for (int i = entries.Count - 1, scanned = 0; i >= 0 && scanned < 3000 && found < 10; i--, scanned++)
            {
                EventLogEntry entry;
                try
                {
                    entry = entries[i];
                }
                catch (ArgumentException)
                {
                    break; // log rotated under us
                }
                if (entry.TimeGenerated < cutoff)
                    break;
                if (entry.Source is not ("Application Error" or ".NET Runtime" or "Windows Error Reporting"))
                    continue;
                if (!entry.Message.Contains("EQ2Parser", StringComparison.OrdinalIgnoreCase))
                    continue;
                found++;
                report.AppendLine()
                    .AppendLine($"[{entry.TimeGenerated:yyyy-MM-dd HH:mm:ss}] {entry.Source}")
                    .AppendLine(entry.Message.Trim());
            }
            if (found == 0)
                report.AppendLine("(none found — consistent with an external process kill)");
        }
        catch (Exception ex)
        {
            report.AppendLine($"(event log unreadable: {ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Ask Windows Error Reporting for a local minidump on native
    /// deaths. HKCU works without admin and WER honours per-app LocalDumps
    /// keys there; DumpType 1 = minidump, keep the last five.
    ///
    /// WER matches the key on the crashing process's IMAGE FILE NAME, so
    /// the name comes from the running process — the key used to be a
    /// hard-coded "EQ2Parser.exe" while the shipped executable is named
    /// after the assembly, so the request could not match it.</summary>
    private static void RegisterCrashDumps()
    {
        try
        {
            if (DumpImageName(Environment.ProcessPath) is not { } image)
                return;
            RemoveStaleDumpKey(image);
            using var key = Registry.CurrentUser.CreateSubKey($@"{LocalDumpsKey}\{image}");
            key.SetValue("DumpFolder", Path.Combine(CrashLog.Directory, "dumps"), RegistryValueKind.ExpandString);
            key.SetValue("DumpType", 1, RegistryValueKind.DWord);
            key.SetValue("DumpCount", 5, RegistryValueKind.DWord);
            Mark(DumpsRequestedMark + image);
        }
        catch
        {
        }
    }

    /// <summary>Best-effort and separate from the registration: a key that
    /// will not delete must not cost the dumps.</summary>
    private static void RemoveStaleDumpKey(string image)
    {
        // Never the key about to be written, should the executable ever
        // really be called that.
        if (string.Equals(image, StaleDumpImage, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{LocalDumpsKey}\{StaleDumpImage}", throwOnMissingSubKey: false);
        }
        catch
        {
        }
    }

    /// <summary>The image name to register dumps under, or null when this
    /// process is not the app's own executable. A framework-dependent run
    /// ("dotnet EQ2Parser.App.dll") or a test host is a SHARED image:
    /// registering it would have Windows dump every unrelated program that
    /// runs under the same host into our crashes folder.</summary>
    internal static string? DumpImageName(string? processPath)
    {
        var image = Path.GetFileName(processPath);
        if (string.IsNullOrEmpty(image) || !image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return image.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
               || image.Equals("testhost.exe", StringComparison.OrdinalIgnoreCase)
            ? null
            : image;
    }
}
