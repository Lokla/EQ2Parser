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
///  2. Windows crash dumps, FOUND rather than requested. Windows Error
///     Reporting writes a minidump on a native crash or stack overflow —
///     exactly the deaths we can't catch in-process — but only when the
///     MACHINE has local dumps enabled (a key under HKEY_LOCAL_MACHINE,
///     which takes an administrator to create). Earlier builds wrote the
///     same settings under HKEY_CURRENT_USER instead; Windows ignores them
///     there (see <see cref="NoteCrashDumps"/>), so this build writes
///     nothing, removes those keys, and records in the journal whether the
///     machine has dumps on and where one would land. The next launch's
///     postmortem then looks in every place a dump could be.
///  3. Startup post-mortem: a previous session.log that does not END with
///     the clean-exit mark means the last run died. Its journal is copied
///     into the crashes folder and the Windows Application event log is
///     searched for matching Application Error / .NET Runtime / WER
///     entries (faulting module + exception code) — the user just sends
///     the crashes folder instead of spelunking Event Viewer. The header
///     names any crash report that session wrote: a fatal, crash-loop or
///     failed-startup exit skips the clean-exit mark too, and those DO
///     leave a managed report.
///
/// An empty postmortem (no crash report, no event-log entry, journal
/// simply stops) is itself diagnostic: outright process termination, i.e.
/// antivirus. A missing DUMP is not part of that reading unless the
/// machine had dumps enabled — on most machines none could exist.
/// </summary>
public static class SessionJournal
{
    private static readonly object Gate = new();
    private const string CleanExitMark = "clean-exit";
    private const string StampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private const string LocalDumpsKey = @"Software\Microsoft\Windows\Windows Error Reporting\LocalDumps";

    /// <summary>The image name builds up to v0.5.7 registered under
    /// HKEY_CURRENT_USER. No shipped executable is called that (the
    /// apphost is named after the assembly).</summary>
    private const string StaleDumpImage = "EQ2Parser.exe";

    /// <summary>Where Windows puts local dumps when nothing names another
    /// folder, as its own setting spells it.</summary>
    internal const string DefaultDumpFolder = @"%LOCALAPPDATA%\CrashDumps";

    /// <summary>Journal breadcrumbs for what <see cref="NoteCrashDumps"/>
    /// found. The NEXT launch's postmortem looks for them: only a session
    /// on a machine with dumps enabled can read anything into not having
    /// one, and a journal with neither (an older build's) says nothing.</summary>
    internal const string DumpsOnMark = "crash dumps ON — ";
    internal const string DumpsOffMark = "crash dumps OFF — ";

    public static string FilePath => Path.Combine(AppSettings.Directory, "session.log");

    private static DateTime _lastHeartbeat;
    private static DateTime _lastTts;

    /// <summary>The journal file <see cref="End"/> closed, while it is
    /// still the current one. Guarded by <see cref="Gate"/>.</summary>
    private static string? _endedFile;

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
                _endedFile = null;
                File.WriteAllText(FilePath,
                    Line($"session start — EQ2Parser {CrashLog.Version} · {Environment.OSVersion} · .NET {Environment.Version}"));
            }
            NoteCrashDumps();
            if (previous is not null)
                _ = Task.Run(() => WritePostmortem(previous, startedAt));
        }
        catch
        {
            // The journal must never be the thing that breaks startup.
        }
    }

    /// <summary>Timestamped breadcrumb; cheap enough for coarse events
    /// (startup stages, source add/remove, command-file writes). Ignored
    /// once <see cref="End"/> has closed this session's journal.</summary>
    public static void Mark(string what)
    {
        try
        {
            lock (Gate)
            {
                var file = FilePath;
                if (IsEnded(file))
                    return;
                File.AppendAllText(file, Line(what));
            }
        }
        catch
        {
        }
    }

    private static bool IsEnded(string file) =>
        _endedFile is { } ended && string.Equals(ended, file, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Call last in OnExit. Writes the clean-exit mark and closes
    /// the journal: nothing is appended after it for this session.
    ///
    /// The closing matters because the next launch reads a session as
    /// clean only when the mark is its FINAL line (<see cref="EndedCleanly"/>).
    /// A thread that outlives the exit sequence by a moment — a log pump
    /// that did not stop inside its two-second join — could otherwise add
    /// a breadcrumb after the mark and turn an ordinary exit into a
    /// postmortem. The closing is tied to the journal FILE, so a later
    /// session on another data folder (each test has its own) is not
    /// affected, and <see cref="Begin"/> reopens it.</summary>
    public static void End()
    {
        try
        {
            lock (Gate)
            {
                var file = FilePath;
                if (IsEnded(file))
                    return;
                File.AppendAllText(file, Line(CleanExitMark));
                _endedFile = file;
            }
        }
        catch
        {
        }
    }

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
            return text.Length == 0 || EndedCleanly(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Did the session that wrote this journal exit cleanly? Only
    /// when its LAST line is the mark <see cref="End"/> writes, whole and
    /// in the journal's own line form: "[stamp] clean-exit".
    ///
    /// It used to be "the text clean-exit appears anywhere", which any
    /// breadcrumb could satisfy — a log named eq2log_clean-exit.txt in a
    /// "source added" line, an exception type, a future mark that merely
    /// mentions it — and a session that then died was read as a clean one:
    /// no postmortem, the evidence overwritten by the next start. Now the
    /// last line has to BE the mark, stamp and all, so text inside another
    /// breadcrumb cannot pass for it, and neither can a real mark that
    /// something was still written after.
    ///
    /// The stamp is checked for shape, not parsed: a journal an older build
    /// wrote under a non-Gregorian calendar has the shape and a year this
    /// build would not read back.</summary>
    internal static bool EndedCleanly(string journal)
    {
        var text = journal.AsSpan().TrimEnd("\r\n");
        var last = text[(text.LastIndexOfAny('\r', '\n') + 1)..];
        // "[" + stamp + "] " + mark
        var markAt = StampFormat.Length + 3;
        return last.Length == markAt + CleanExitMark.Length
            && last[0] == '['
            && last[StampFormat.Length + 1] == ']'
            && last[StampFormat.Length + 2] == ' '
            && last[markAt..].SequenceEqual(CleanExitMark);
    }

    private static void WritePostmortem(string previousJournal, DateTime nextSessionStart)
    {
        try
        {
            var start = SessionStart(previousJournal);
            var (ending, other) = start is { } from
                ? ReportsFromSession(ReportFileNames(), from, nextSessionStart)
                : ([], []);
            var image = DumpImageName(Environment.ProcessPath);
            var report = new StringBuilder()
                .Append(PostmortemHeader(
                    startKnown: start is not null,
                    ending,
                    other,
                    dumpsEnabled: DumpsEnabledDuring(previousJournal),
                    dumps: start is { } since
                        ? DumpsFromSession(DumpCandidates(image), image, OwnDumpFolder, since)
                        : []))
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
    /// the reader hunting for an antivirus.
    ///
    /// <paramref name="dumpsEnabled"/> is what the dead session's journal
    /// recorded about the machine (<see cref="DumpsEnabledDuring"/>): true
    /// or false, or null when it recorded nothing. <paramref name="dumps"/>
    /// are full paths (<see cref="DumpsFromSession"/>).</summary>
    internal static string PostmortemHeader(
        bool startKnown,
        IReadOnlyList<string> ending,
        IReadOnlyList<string> other,
        bool? dumpsEnabled,
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
                .AppendLine("Windows saved a crash dump during that session. It is still where")
                .AppendLine("Windows wrote it (nothing was copied or opened):");
            AppendNames(header, dumps);
        }
        else if (ending.Count == 0 && dumpsEnabled == true)
        {
            // The one case where a missing dump carries any weight — and
            // even then it is a lean, not a proof.
            header
                .AppendLine()
                .AppendLine("That session recorded that this machine has Windows local crash dumps")
                .AppendLine("enabled, and no dump from it was found. A native crash would normally")
                .AppendLine("have left one, so together with an empty event-log section this leans")
                .AppendLine("toward a termination from outside (check the AV's protection history)")
                .AppendLine("or a forced end (Task Manager, power loss). It is not proof.");
        }
        else if (ending.Count == 0 && dumpsEnabled == false)
        {
            header
                .AppendLine()
                .AppendLine("That session recorded that this machine does NOT have Windows local")
                .AppendLine("crash dumps enabled (turning them on takes an administrator), so no dump")
                .AppendLine("could have been written: a missing one says nothing either way. An")
                .AppendLine("empty event-log section still points at a termination from outside")
                .AppendLine("(check the AV's protection history).");
        }
        else if (ending.Count == 0)
        {
            // A journal from a build up to v0.5.7, or one that could not
            // read the setting: nothing is known, so nothing is claimed.
            header
                .AppendLine()
                .AppendLine("That session did not record whether this machine has Windows local")
                .AppendLine("crash dumps enabled (older builds asked for dumps in a way Windows")
                .AppendLine("ignores), so a missing dump says nothing either way. An empty")
                .AppendLine("event-log section still points at a termination from outside (check")
                .AppendLine("the AV's protection history).");
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

    /// <summary>The app's own dump folder: where the per-application
    /// setting a tester can add points (the triage notes have the
    /// command), and where earlier builds asked for dumps.</summary>
    private static string OwnDumpFolder => Path.Combine(CrashLog.Directory, "dumps");

    /// <summary>What the dead session's journal recorded about the machine:
    /// local dumps on, off, or (an older build's journal, or a setting that
    /// could not be read) nothing at all.</summary>
    internal static bool? DumpsEnabledDuring(string journal) =>
        journal.Contains(DumpsOnMark, StringComparison.Ordinal) ? true
        : journal.Contains(DumpsOffMark, StringComparison.Ordinal) ? false
        : null;

    /// <summary>Which of the candidate files are crash dumps of THIS app
    /// from the dead session — as full paths, sorted. Pure: the caller
    /// lists the folders (<see cref="DumpCandidates"/>).
    ///
    ///  * Written at or after the session began. No upper bound needed:
    ///    this launch is moments old and has not crashed.
    ///  * In the app's own dump folder, any .dmp counts — nothing else
    ///    writes there.
    ///  * Anywhere else the folder is SHARED (Windows' default
    ///    %LOCALAPPDATA%\CrashDumps holds every program's dumps), so only
    ///    the name Windows gives this executable's dumps counts:
    ///    "&lt;image&gt;.&lt;process id&gt;.dmp". With no image name (a
    ///    run under a shared host such as dotnet.exe) nothing in a shared
    ///    folder can be told apart, and nothing there is claimed.
    /// The files are only named. They are never opened, copied or moved:
    /// a dump is a copy of the process's memory and stays where Windows
    /// put it until its owner decides otherwise.</summary>
    internal static List<string> DumpsFromSession(
        IEnumerable<(string Path, DateTime Written)> candidates, string? image, string ownFolder, DateTime sessionStart)
    {
        var own = Path.TrimEndingDirectorySeparator(ownFolder);
        SortedSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, written) in candidates)
        {
            if (written < sessionStart || !path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                continue;
            var inOwnFolder = string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? ""), own, StringComparison.OrdinalIgnoreCase);
            if (inOwnFolder || IsDumpOf(Path.GetFileName(path), image))
                found.Add(path);
        }
        return [.. found];
    }

    /// <summary>"EQ2Parser.App.exe.1234.dmp" for image "EQ2Parser.App.exe".</summary>
    private static bool IsDumpOf(string fileName, string? image)
    {
        if (string.IsNullOrEmpty(image)
            || fileName.Length <= image.Length + 1 + ".dmp".Length
            || !fileName.StartsWith(image + ".", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            return false;
        var processId = fileName.AsSpan(image.Length + 1, fileName.Length - image.Length - 1 - ".dmp".Length);
        return processId.Length > 0 && processId.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>Every .dmp in the places a dump of this app could be: its
    /// own folder, Windows' default folder, and the folder the machine's
    /// LocalDumps settings name (if any). Names and write times only.</summary>
    private static List<(string Path, DateTime Written)> DumpCandidates(string? image)
    {
        List<string> folders = [OwnDumpFolder, Environment.ExpandEnvironmentVariables(DefaultDumpFolder)];
        try
        {
            if (image is not null && MachineDumpFolder(image) is { Length: > 0 } configured)
                folders.Add(Environment.ExpandEnvironmentVariables(configured));
        }
        catch
        {
            // Settings unreadable: the two known folders still get checked.
        }
        List<(string, DateTime)> candidates = [];
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new DirectoryInfo(folder);
                if (info.Exists)
                    candidates.AddRange(info.GetFiles("*.dmp").Select(f => (f.FullName, f.LastWriteTime)));
            }
            catch
            {
                // An unreadable folder is a folder with nothing to report.
            }
        }
        return candidates;
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

    /// <summary>Record, for the next launch's postmortem, whether a native
    /// death of this process can leave a Windows crash dump — and tidy up
    /// after the builds that tried to make sure of it.
    ///
    /// What is true, checked on Windows 11 with a process made to die both
    /// ways (a fail-fast and an unhandled exception):
    ///  * Windows Error Reporting reads its LocalDumps settings from
    ///    HKEY_LOCAL_MACHINE only. The same values under HKEY_CURRENT_USER
    ///    — which is what every earlier build wrote, because that hive
    ///    needs no administrator — are IGNORED: nothing arrived in the
    ///    folder they named.
    ///  * When the machine-wide LocalDumps key exists (other software, or
    ///    an administrator, created it) Windows does write a dump, named
    ///    "&lt;image&gt;.&lt;process id&gt;.dmp", into
    ///    %LOCALAPPDATA%\CrashDumps.
    ///  * When it does not exist there is no dump, and creating it takes
    ///    administrator rights a per-user install does not have.
    /// One thing here is from Microsoft's documentation rather than from
    /// that check: the machine key, or a per-application key beneath it,
    /// can name a different folder (DumpFolder). <see cref="MachineDumpFolder"/>
    /// reads it so the journal and the lookup follow it.
    /// So the app can only LOOK: this reads the machine key (read-only),
    /// journals what it found, and the postmortem searches the folders a
    /// dump could be in (<see cref="DumpCandidates"/>). A missing dump
    /// means something only on a machine that had dumps on.
    ///
    /// The cleanup removes the per-user keys those builds left: the image
    /// name builds up to v0.5.7 hard-coded, and this executable's own name
    /// (later builds). They do nothing, and a setting that does nothing but
    /// looks like the reason dumps appear, or fail to, is worth removing.
    ///
    /// Only when this process is the app's own executable: under a shared
    /// host (dotnet.exe, a test host) there is no image name to speak for
    /// and no business in the registry.</summary>
    private static void NoteCrashDumps()
    {
        try
        {
            if (DumpImageName(Environment.ProcessPath) is not { } image)
                return;
            RemovePerUserDumpKeys(image);
            var folder = MachineDumpFolder(image);
            Mark(folder is null
                ? $"{DumpsOffMark}Windows local dumps are not enabled on this machine (it takes an administrator), so a native crash of {image} leaves no dump"
                : $"{DumpsOnMark}Windows local dumps are enabled on this machine; a native crash of {image} leaves {image}.<pid>.dmp in {(folder.Length > 0 ? folder : DefaultDumpFolder)}");
        }
        catch
        {
            // Unreadable settings: no line, and the postmortem then claims
            // nothing about dumps either way.
        }
    }

    /// <summary>The machine's answer for this image: null when local dumps
    /// are not enabled (no LocalDumps key under HKEY_LOCAL_MACHINE), ""
    /// when they are and no folder is named (Windows' default applies), or
    /// the folder as the setting spells it, environment variables
    /// unexpanded — a per-application key's value before the machine-wide
    /// one. Read-only; throws when the registry cannot be read. The 64-bit
    /// view explicitly: that is the one Windows Error Reporting uses.</summary>
    private static string? MachineDumpFolder(string image)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var all = machine.OpenSubKey(LocalDumpsKey);
        if (all is null)
            return null;
        using var mine = all.OpenSubKey(image);
        return mine?.GetValue("DumpFolder", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
            ?? all.GetValue("DumpFolder", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
            ?? "";
    }

    /// <summary>Best-effort, each key by itself: one that will not delete
    /// must not keep the other, or cost the journal its line.</summary>
    private static void RemovePerUserDumpKeys(string image)
    {
        foreach (var name in new[] { StaleDumpImage, image }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"{LocalDumpsKey}\{name}", throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }
    }

    /// <summary>The image name Windows files this process's dumps under
    /// ("&lt;image&gt;.&lt;process id&gt;.dmp"), or null when this process
    /// is not the app's own executable. A framework-dependent run
    /// ("dotnet EQ2Parser.App.dll") or a test host is a SHARED image: its
    /// dumps cannot be told from those of every unrelated program run
    /// under the same host, and its registry keys are not ours to tidy.</summary>
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
