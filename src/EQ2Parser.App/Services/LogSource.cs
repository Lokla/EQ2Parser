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
/// the source, and then <see cref="Error"/> says so.
/// </summary>
public sealed class LogSource : IDisposable
{
    /// <summary>Crash reports one source may write for skipped lines. The
    /// first few carry the stack trace that matters; a fault repeating all
    /// night would otherwise write one every few seconds and rotate older,
    /// unrelated reports out of the crashes folder (it keeps 30). Every
    /// skip past the cap is still counted, here and in the journal.</summary>
    private const int MaxLineReports = 5;

    /// <summary>How much of a skipped line its crash report quotes.</summary>
    private const int MaxQuotedLineChars = 300;

    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private readonly object _sync;

    private readonly LogTailReader _reader;
    private readonly LineFaultBreaker _faults = new();
    private int _lineReports;

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
    /// skipped lines and no Error is still tailing.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Lines dropped this session because processing them threw.
    /// The source is alive while <see cref="Error"/> is null — the Sources
    /// page shows this beside the normal counters.</summary>
    public long SkippedLines => _faults.SkippedLines;

    /// <summary>Why the most recent line was skipped (null = none was).</summary>
    public Exception? LastLineError => _faults.LastError;

    public LogSource(string path, bool parseFromStart, object sync, EngineOptions engineOptions, TimeSpan pollInterval, TriggerEngine? triggers = null, SpellTimerService? timers = null, long? startOffset = null)
    {
        Path = path;
        ParseFromStart = parseFromStart;
        _sync = sync;
        Owner = DeriveOwner(path);
        Engine = new ParserEngine(path, Owner, engineOptions);
        TriggerEngine = triggers;
        Processor = new LogLineProcessor(Engine, triggers, timers);
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
                    return; // breaker tripped — Error is set, evidence written
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
            Error = ex;
            CrashLog.Report("log-reader", ex, $"Log source: {FileName}", always: true);
            SessionJournal.Mark($"source STOPPED: {FileName} — the log reader failed ({ex.GetType().Name})");
        }
    }

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
    /// next.</summary>
    internal bool PumpLine(TailedLine tailed)
    {
        try
        {
            // Inside the guard as well: the parse helpers are ordinary code
            // run over text any player can put in the log.
            if (!LogLine.TryParse(tailed.Raw, out var line))
                return true;
            line = line with { ObservedAt = tailed.ObservedAt };
            lock (_sync)
            {
                Processor.Process(line);
            }
            _faults.RecordSuccess();
            return true;
        }
        catch (Exception ex)
        {
            return OnLineFailed(ex, tailed.Raw);
        }
    }

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
        Error = stopped;
        // Once per source, so it bypasses the rate limit — the line report
        // a moment ago would otherwise swallow the source's last words.
        CrashLog.Report("log-source-stopped", stopped, LineContext(raw, outcome.Skipped), always: true);
        SessionJournal.Mark(
            $"source STOPPED: {FileName} — {outcome.Consecutive} lines in a row failed ({ex.GetType().Name}, {outcome.Skipped} skipped this session)");
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
        + $"Line: {(raw.Length <= MaxQuotedLineChars ? raw : raw[..MaxQuotedLineChars] + "…")}";

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
