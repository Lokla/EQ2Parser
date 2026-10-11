using System.IO;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>
/// One tailed log file: reader + processor + engine, pumped on a background
/// task. All engine mutation happens under the manager's shared sync object,
/// which also serializes the correlator callbacks EncounterEnded fires —
/// take the same lock to read engine state from the UI.
///
/// Fault isolation: a line whose processing throws is SKIPPED, with a crash
/// report and a journal line, and the tail carries on — one bad line used
/// to end the loop silently, and with it every fight for that character for
/// the rest of the raid. Only a long unbroken run of failures (see
/// <see cref="LineFaultBreaker"/>) or a failure of the reader itself stops
/// the source, and then <see cref="Error"/> says so. A source that stops
/// ends its fight in progress first (<see cref="EndFightInProgress"/>), so
/// the fight is saved and uploaded like any other instead of sitting "in
/// combat" until the app closes.
///
/// A fault in an ALERT handler is smaller still: the processor contains it
/// and the line's combat data is parsed all the same (see
/// <see cref="LogLineProcessor.AlertFaulted"/>). It is reported from here
/// — its own crash-report kind, its own journal line, <see cref="AlertFaults"/>
/// — and never counted as a skipped line or toward the breaker.
/// </summary>
public sealed class LogSource : IDisposable
{
    /// <summary>Crash reports one source may write for skipped lines. The
    /// first few carry the stack trace that matters; a fault repeating all
    /// night would otherwise write one every few seconds and rotate older,
    /// unrelated reports out of the crashes folder (it keeps 30). Every
    /// skip past the cap is still counted, here and in the journal.</summary>
    private const int MaxLineReports = 5;

    /// <summary>The same cap, kept separately, for contained alert-stage
    /// faults: a broken alert handler usually throws on every line it
    /// sees, and its reports must not use up the budget of a line fault
    /// (or the other way round).</summary>
    private const int MaxAlertReports = 5;

    /// <summary>How much of a skipped line its crash report quotes.</summary>
    private const int MaxQuotedLineChars = 300;

    /// <summary>Crash-report kind for a contained alert-stage fault.</summary>
    internal const string AlertKind = "log-alert";

    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private readonly object _sync;

    private readonly LogTailReader _reader;
    private readonly LineFaultBreaker _faults = new();
    private int _lineReports;

    /// <summary>Alert-stage faults the processor contained during the line
    /// being processed. Filled by <see cref="NoteAlertFault"/> inside
    /// Processor.Process and emptied by <see cref="TakeAlertFaults"/>, both
    /// under the sync lock; the file writes happen after it is released.</summary>
    private readonly List<(AlertStage Stage, Exception Error)> _pendingAlertFaults = [];
    private long _alertFaults;
    private volatile Exception? _lastAlertError;
    private int _alertReports;
    private DateTimeOffset? _lastAlertJournal;

    public string Path { get; }
    public string Owner { get; }
    public bool ParseFromStart { get; }

    /// <summary>Attached by the folder watcher rather than by hand.</summary>
    public bool AutoDiscovered { get; init; }
    public ParserEngine Engine { get; }
    public LogLineProcessor Processor { get; }
    public TriggerEngine? TriggerEngine { get; }

    /// <summary>Byte position of the last fully-consumed log line —
    /// persisted so the next session resumes where this one left off.</summary>
    public long LastPosition => _reader.ConsumedPosition;

    /// <summary>Bytes between the consumed position and the file's current
    /// end — large while a parse-from-start import chews backlog, ~zero at
    /// live tail. UI surfaces read this to throttle themselves during
    /// imports so the pump keeps the lock.</summary>
    public long PendingBytes
    {
        get
        {
            try
            {
                return Math.Max(0, new FileInfo(Path).Length - _reader.ConsumedPosition);
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    /// <summary>Set when the tail loop has STOPPED: the reader failed (file
    /// unreadable etc.) or the line-fault breaker tripped. A source with
    /// skipped lines and no Error is still tailing. Set LAST: by the time
    /// anyone can see it, the evidence is written and the fight that was
    /// in progress has been ended and announced.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Lines dropped this session because processing them threw.
    /// The source is alive while <see cref="Error"/> is null — the Sources
    /// page shows this beside the normal counters.</summary>
    public long SkippedLines => _faults.SkippedLines;

    /// <summary>Why the most recent line was skipped (null = none was).</summary>
    public Exception? LastLineError => _faults.LastError;

    /// <summary>Alert-stage faults this session: a trigger, raid-line,
    /// callout or timer handler threw, and the line was parsed anyway. No
    /// combat data is missing because of these — an alert may be.</summary>
    public long AlertFaults => Interlocked.Read(ref _alertFaults);

    /// <summary>The most recent alert-stage fault (null = none).</summary>
    public Exception? LastAlertError => _lastAlertError;

    /// <summary>Test seam, never set by the app: runs for each parsed line
    /// just before the processor, inside the per-line guard and under the
    /// sync lock. A throw from it is a whole-LINE failure that touches no
    /// engine state — the clean way to inject one now that no log line
    /// throws by itself and the subscriber stages are contained.</summary>
    internal Action<LogLine>? BeforeProcess { get; init; }

    public LogSource(string path, bool parseFromStart, object sync, EngineOptions engineOptions, TimeSpan pollInterval, TriggerEngine? triggers = null, SpellTimerService? timers = null, long? startOffset = null)
    {
        Path = path;
        ParseFromStart = parseFromStart;
        _sync = sync;
        Owner = DeriveOwner(path);
        Engine = new ParserEngine(path, Owner, engineOptions);
        TriggerEngine = triggers;
        Processor = new LogLineProcessor(Engine, triggers, timers);
        Processor.AlertFaulted += NoteAlertFault;
        _startOffset = parseFromStart ? null : startOffset;
        _tailOptions = new LogTailOptions
        {
            StartAtEnd = !parseFromStart,
            StartOffset = _startOffset,
            PollInterval = pollInterval,
        };
        _reader = new LogTailReader(path, _tailOptions);
    }

    private readonly long? _startOffset;
    private readonly LogTailOptions _tailOptions;

    /// <summary>Current length for the StartAtEnd case (no saved offset) —
    /// 0 on any IO failure so the look-behind is skipped, never fatal.</summary>
    private long TryFileLength()
    {
        try
        {
            return new FileInfo(Path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Begin pumping. The manager calls this AFTER attaching the
    /// engine to the correlator and wiring the history/callout handlers —
    /// the constructor used to start the pump itself, so a fast log's first
    /// lines could be processed with none of that wired.</summary>
    public void Start() => _task ??= Task.Run(() => PumpAsync(_reader, _cts.Token));

    /// <summary>eq2log_Charname.[…].txt → Charname.</summary>
    public static string DeriveOwner(string path) =>
        System.IO.Path.GetFileNameWithoutExtension(path).Replace("eq2log_", "").Split('.')[0];

    private async Task PumpAsync(LogTailReader reader, CancellationToken ct)
    {
        try
        {
            // Tailing mid-file means the "You have entered …" line is
            // behind the start offset — look back for it so zone-scoped
            // spell timers know the zone before the next zoning. Runs
            // before the first pumped line, so a live zone line still
            // overrides the seed in order.
            if (!ParseFromStart)
            {
                var behind = _startOffset ?? TryFileLength();
                if (behind > 0 && ZoneLookbehind.FindLastZoneSeed(
                        Path, behind, encoding: _tailOptions.Encoding) is { } seed)
                {
                    lock (_sync)
                    {
                        Engine.ChangeZone(seed.Zone);
                        if (seed.InstanceExpiry is { } expiry)
                            Engine.SeedInstanceExpiry(expiry);
                    }
                }
            }

            await foreach (var tailed in reader.ReadLinesAsync(ct))
            {
                if (!PumpLine(tailed))
                    return; // breaker tripped — evidence written, fight ended, Error set
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // The READER failed (IO error mid-read, file on a drive that
            // went away): nothing to skip past, the tail is over. Same
            // outcome as before — but no longer a silent one.
            // Evidence first, Error last: Error is what other threads (the
            // Sources page, tests) watch, so by the time anyone can see the
            // source has stopped, the report and the journal line exist
            // and the fight in progress has been ended.
            CrashLog.Report("log-reader", ex, $"Log source: {FileName}", always: true);
            var endedFight = EndFightInProgress();
            SessionJournal.Mark($"source STOPPED: {FileName} — the log reader failed ({ex.GetType().Name}){FightNote(endedFight)}");
            Error = ex;
        }
    }

    /// <summary>The source is stopping for good: end the fight it was in,
    /// through the engine's normal path, so it is announced exactly once to
    /// everything that wants a finished fight (correlator, archive, upload,
    /// raid roster). Returns whether there was one.
    ///
    /// Why at all. The engine's idle rule only runs when the NEXT line
    /// arrives, and a stopped source has no next line: its fight stayed
    /// active for the rest of the session — never saved, never uploaded,
    /// the live views showing it in combat for hours.
    ///
    /// Why it is right for both ways a source stops:
    ///  * the reader failed. The engine is sound and every line up to the
    ///    failure was applied; the fight is simply cut short where the log
    ///    became unreadable. That is the same fight a link-death or a
    ///    camp-out mid-pull leaves, and those are ended and saved by the
    ///    idle rule every night.
    ///  * the breaker tripped. Lines were failing, so this deserves the
    ///    harder look — but a failed line never leaves a wrong swing
    ///    behind, only a missing one (see <see cref="PumpLine"/>: each
    ///    step of processing completes or leaves a state the next line
    ///    handles). So the fight holds everything that parsed before the
    ///    fault began and nothing invented: it is short by the lines that
    ///    failed, which at raid pace is the last second or two before the
    ///    trip, on top of what is now a cut-off fight. Discarding it would
    ///    throw away up to a whole boss attempt of sound data to hide that
    ///    gap, with no way to get it back short of a re-import that trips
    ///    at the same place. And it is no new kind of fight: every fight
    ///    that had a line skipped WITHOUT tripping the breaker is already
    ///    saved and uploaded with its gap; the crash reports and the
    ///    journal say which session that was.
    /// In both cases the fight's end time is its last recorded swing (the
    /// engine's own rule), not the moment the source stopped.
    ///
    /// EndCombat does nothing when no fight is active, so a second call
    /// cannot announce twice. It clears the active fight BEFORE it raises
    /// the announcement, so even a subscriber that throws leaves the fight
    /// ended and in the engine's history; that throw is reported and the
    /// stop carries on (SourceManager isolates its consumers, so in the
    /// app nothing reaches here). Pump thread, outside the sync lock.</summary>
    private bool EndFightInProgress()
    {
        var inFight = false;
        try
        {
            lock (_sync)
            {
                inFight = Engine.InCombat;
                if (inFight)
                    Engine.EndCombat();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Report(
                "fight-consumer", ex,
                $"Log source: {FileName}{Environment.NewLine}"
                + $"Raised while the fight in progress was being ended because this source stopped.{Environment.NewLine}"
                + "The fight is ended and in the session; a consumer of its announcement threw.",
                // At most one per source: a source stops once.
                always: true);
        }
        return inFight;
    }

    private static string FightNote(bool endedFight) =>
        endedFight ? "; its fight in progress was ended" : "";

    /// <summary>One tailed line through parse + processing, with the
    /// failure of THIS line contained. Returns false when the source must
    /// stop (the breaker tripped). Pump thread only.
    ///
    /// Why carrying on after a throw is safe: the engine is only ever
    /// mutated by this thread under the sync lock, the lock is released by
    /// the unwind, and every step of LogLineProcessor.Process either
    /// completes or leaves a state the next line handles — a line that
    /// threw before its swing was recorded simply contributes nothing; one
    /// that threw while a finished fight was being announced leaves that
    /// fight ended and in the engine's history (see SourceManager, which
    /// isolates each announcement consumer for exactly that case). The
    /// reader has already moved past the line, so it is not retried — a
    /// poison line cannot wedge the tail on itself, in this session or the
    /// next.
    ///
    /// Alert-stage faults are a separate, smaller thing. The processor
    /// contains them and finishes the line, so they arrive here as notes
    /// taken during Process, not as an exception: the line counts as a
    /// SUCCESS (it was parsed), and the notes are written up once the lock
    /// is released. If the line then fails as well, the notes are written
    /// first — they were raised first.</summary>
    internal bool PumpLine(TailedLine tailed)
    {
        try
        {
            // Inside the guard as well: the parse helpers are ordinary code
            // run over text any player can put in the log.
            if (!LogLine.TryParse(tailed.Raw, out var line))
                return true;
            line = line with { ObservedAt = tailed.ObservedAt };
            (AlertStage Stage, Exception Error)[]? alertFaults;
            lock (_sync)
            {
                BeforeProcess?.Invoke(line);
                Processor.Process(line);
                alertFaults = TakeAlertFaults();
            }
            if (alertFaults is not null)
                ReportAlertFaults(alertFaults, tailed.Raw);
            _faults.RecordSuccess();
            return true;
        }
        catch (Exception ex)
        {
            (AlertStage Stage, Exception Error)[]? alertFaults;
            lock (_sync)
            {
                alertFaults = TakeAlertFaults();
            }
            if (alertFaults is not null)
                ReportAlertFaults(alertFaults, tailed.Raw);
            return OnLineFailed(ex, tailed.Raw);
        }
    }

    /// <summary>LogLineProcessor.AlertFaulted handler. Runs inside
    /// Processor.Process, so under the sync lock: it only takes a note.
    /// Bounded, in case something other than the pump ever drives the
    /// processor and nothing collects the notes.</summary>
    private void NoteAlertFault(AlertStage stage, Exception error)
    {
        if (_pendingAlertFaults.Count < 16)
            _pendingAlertFaults.Add((stage, error));
    }

    /// <summary>The notes for the line just processed, or null (the usual
    /// case, which must stay allocation-free on the per-line path). Caller
    /// holds the sync lock.</summary>
    private (AlertStage Stage, Exception Error)[]? TakeAlertFaults()
    {
        if (_pendingAlertFaults.Count == 0)
            return null;
        var taken = _pendingAlertFaults.ToArray();
        _pendingAlertFaults.Clear();
        return taken;
    }

    /// <summary>Evidence for alert-stage faults the processor contained.
    /// Pump thread, outside the sync lock. Rate-limited like skipped lines
    /// — a capped number of crash reports, a journal line for the first
    /// and then at most one per <see cref="LineFaultBreaker.JournalInterval"/>,
    /// every fault counted — because a broken handler throws on every line
    /// it is shown, and an AE is one such line per raid member.
    ///
    /// Deliberately NOT a line failure: nothing here touches the breaker
    /// or <see cref="SkippedLines"/>. The line was parsed; calling it
    /// skipped would send someone looking for missing damage that is not
    /// missing, and 200 of them in a row must not stop a source whose
    /// numbers are all correct.
    ///
    /// The journal names the stage and the exception type only. The crash
    /// report quotes the line, as a skipped line's does: a handler that
    /// throws on one particular line cannot be reproduced without it.</summary>
    private void ReportAlertFaults((AlertStage Stage, Exception Error)[] faults, string raw)
    {
        foreach (var (stage, error) in faults)
        {
            var total = Interlocked.Read(ref _alertFaults) + 1;
            if (_alertReports < MaxAlertReports
                && CrashLog.Report(AlertKind, error, AlertContext(stage, raw, total)) is not null)
            {
                _alertReports++;
            }
            var now = DateTimeOffset.UtcNow;
            if (_lastAlertJournal is not { } last || now - last >= LineFaultBreaker.JournalInterval)
            {
                _lastAlertJournal = now;
                SessionJournal.Mark(
                    $"alert handler FAILED: {FileName} — {StageName(stage)} ({error.GetType().Name}, {total} this session); the line was still parsed");
            }
            // Published last, as Error is: whoever sees the count move
            // finds the evidence already written.
            _lastAlertError = error;
            Interlocked.Exchange(ref _alertFaults, total);
        }
    }

    private static string StageName(AlertStage stage) => stage switch
    {
        AlertStage.Triggers => "triggers",
        AlertStage.TriggerShare => "trigger share",
        AlertStage.RaidLine => "raid line",
        AlertStage.StatusCallout => "status callout",
        AlertStage.SpellTimers => "spell timers",
        _ => stage.ToString(),
    };

    /// <summary>Evidence for a skipped line, then the breaker's verdict.
    /// Runs on the pump thread OUTSIDE the sync lock (the lock statement
    /// has unwound), so the file writes here never hold up the UI.</summary>
    private bool OnLineFailed(Exception ex, string raw)
    {
        var outcome = _faults.RecordFailure(ex, DateTimeOffset.UtcNow);
        if (_lineReports < MaxLineReports
            && CrashLog.Report("log-line", ex, LineContext(raw, outcome.Skipped)) is not null)
        {
            _lineReports++;
        }
        // The journal gets the exception TYPE only — never the line (chat
        // from arbitrary players) and never the message, which can quote it.
        if (outcome.Journal && !outcome.Stop)
            SessionJournal.Mark($"log line skipped: {FileName} — {ex.GetType().Name} ({outcome.Skipped} skipped this session)");
        if (!outcome.Stop)
            return true;

        var stopped = new InvalidOperationException(
            $"stopped after {outcome.Consecutive} lines in a row failed to process — last: {ex.GetType().Name}: {ex.Message}", ex);
        // Once per source, so it bypasses the rate limit — the line report
        // a moment ago would otherwise swallow the source's last words.
        CrashLog.Report("log-source-stopped", stopped, LineContext(raw, outcome.Skipped), always: true);
        var endedFight = EndFightInProgress();
        SessionJournal.Mark(
            $"source STOPPED: {FileName} — {outcome.Consecutive} lines in a row failed ({ex.GetType().Name}, {outcome.Skipped} skipped this session){FightNote(endedFight)}");
        // Set last, for the same reason as the reader failure in PumpAsync:
        // whoever sees Error finds the evidence already written and the
        // fight already ended.
        Error = stopped;
        return false;
    }

    private string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>What a line's crash report says above the stack trace. The
    /// line itself is quoted (truncated) because a poison line cannot be
    /// reproduced without it — crash files stay on this machine until the
    /// user chooses to send them, and the report says what it contains.</summary>
    private string LineContext(string raw, long skipped) =>
        $"Log source: {FileName}{Environment.NewLine}"
        + $"Lines skipped this session: {skipped}{Environment.NewLine}"
        + $"The log line that failed is quoted below. It comes from your EQ2 log and can{Environment.NewLine}"
        + $"contain chat text — delete it before sharing this file if you would rather not{Environment.NewLine}"
        + $"pass that on.{Environment.NewLine}"
        + $"Line: {Quoted(raw)}";

    /// <summary>The same for a contained alert-stage fault. It says first
    /// what did NOT go wrong, because the reader's first question is
    /// whether numbers are missing.</summary>
    private string AlertContext(AlertStage stage, string raw, long total) =>
        $"Log source: {FileName}{Environment.NewLine}"
        + $"Alert stage: {StageName(stage)}{Environment.NewLine}"
        + $"Alert faults this session: {total}{Environment.NewLine}"
        + $"An alert handler threw while this line was processed. The fault was contained: the{Environment.NewLine}"
        + $"line was still parsed, so no combat data is missing because of it. An alert (a{Environment.NewLine}"
        + $"trigger, a timer, a callout, a raid-roster update) may not have fired.{Environment.NewLine}"
        + $"The log line is quoted below. It comes from your EQ2 log and can contain chat text —{Environment.NewLine}"
        + $"delete it before sharing this file if you would rather not pass that on.{Environment.NewLine}"
        + $"Line: {Quoted(raw)}";

    private static string Quoted(string raw) =>
        raw.Length <= MaxQuotedLineChars ? raw : raw[..MaxQuotedLineChars] + "…";

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces here; nothing to report on shutdown.
        }
        _cts.Dispose();
    }
}
