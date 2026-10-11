using System.IO;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;

namespace EQ2Parser.App.Tests;

/// <summary>
/// One bad log line must cost one line, not the source: the pump skips it,
/// leaves a crash report and a journal breadcrumb, and keeps tailing. Only
/// an unbroken run of failures, or the reader itself failing, stops a
/// source — and then it says why.
/// </summary>
public sealed class LogSourceFaultIsolationTests : IDisposable
{
    private readonly TestDataDir _dir = new();
    private static readonly long Epoch = 1_775_000_000;
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(Epoch);

    /// <summary>What marks a line as the one whose handling fails. No log
    /// line is known to throw by itself any more (the last one, an epoch no
    /// timestamp can hold, is refused by LogLine.TryParse now), and the
    /// subscriber stages that used to carry the injected fault (a throwing
    /// raid-line handler) are contained by the processor now — such a
    /// throw no longer fails the LINE, which is the point of the tests
    /// further down. So a whole-line failure is injected through the one
    /// seam made for it: every source built by <see cref="Source"/> has a
    /// <see cref="LogSource.BeforeProcess"/> hook that throws on a line
    /// with this text. It runs inside the per-line guard, under the sync
    /// lock, where the processor is about to run — the same place a throw
    /// from the combat path surfaces — and touches no engine state.</summary>
    private const string PoisonMark = "private words";

    /// <summary>A chat line carrying the mark.</summary>
    private static string PoisonAt(long epoch) =>
        $"({epoch})[s] Somebody tells you, \"{PoisonMark}\" about the raid";

    private static readonly string Poison = PoisonAt(Epoch);

    public LogSourceFaultIsolationTests()
    {
        // Each test asserts on the report IT caused; the rate limit of the
        // test before must not swallow it.
        CrashLog.ResetRateLimit();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    private static string Hit(long secondsIn, int damage = 500, long? from = null) =>
        $"({(from ?? Epoch) + secondsIn})[s] YOUR Smite hits a gnoll for {damage} divine damage.";

    private LogSource Source(string name = "eq2log_Tester.txt", bool parseFromStart = true) =>
        new(_dir.FileIn(name), parseFromStart, new object(), new EngineOptions(), TimeSpan.FromMilliseconds(5))
        {
            BeforeProcess = static line =>
            {
                if (line.Message.Contains(PoisonMark, StringComparison.Ordinal))
                    throw new InvalidOperationException("line handling broke");
            },
        };

    /// <summary>A combat line the raid-attendance prefilter also wants
    /// (" the raid"), so it reaches a raid-line handler AND the grammar.</summary>
    private static string RaidHit(long secondsIn, int damage = 500) =>
        $"({Epoch + secondsIn})[s] YOUR Smite hits Bane of the raid for {damage} divine damage.";

    /// <summary>A source whose raid-line handler throws on every line it is
    /// shown — a bug in an alert handler, which must cost no combat data.</summary>
    private LogSource SourceWithBrokenAlertHandler()
    {
        var source = Source();
        source.Processor.RaidLine += static (_, _) => throw new InvalidOperationException("raid-line handler broke");
        return source;
    }

    /// <summary>A line as the tail reader hands it over. The arrival stamp
    /// sits right on the line's own time, i.e. live.</summary>
    private static TailedLine Tailed(string raw) => new(raw, T0);

    private static string[] Reports(string kind) =>
        Directory.Exists(CrashLog.Directory)
            ? Directory.GetFiles(CrashLog.Directory, $"crash-*-{kind}*.txt")
            : [];

    private static string Journal() =>
        File.Exists(SessionJournal.FilePath) ? File.ReadAllText(SessionJournal.FilePath) : "";

    [Fact]
    public void A_Line_That_Throws_Is_Skipped_And_The_Next_Line_Still_Parses()
    {
        using var source = Source();

        Assert.True(source.PumpLine(Tailed(Hit(0))));
        Assert.True(source.PumpLine(Tailed(Poison)));
        Assert.True(source.PumpLine(Tailed(Hit(1))));

        Assert.Null(source.Error);
        Assert.Equal(1, source.SkippedLines);
        Assert.IsType<InvalidOperationException>(source.LastLineError);
        // Both good lines landed in the same live fight — the poison line
        // between them left the engine exactly as it found it.
        Assert.True(source.Engine.InCombat);
        Assert.Equal(2, source.Processor.LinesMatched);
        Assert.Equal(1000, source.Engine.ActiveEncounter!.Combatants["TESTER"].Damage);
    }

    [Fact]
    public void A_Skipped_Line_Leaves_A_Crash_Report_With_The_Stack_And_A_Journal_Line_Without_The_Text()
    {
        using var source = Source();

        source.PumpLine(Tailed(Poison));

        var report = File.ReadAllText(Assert.Single(Reports("log-line")));
        Assert.Contains("InvalidOperationException", report);
        Assert.Contains("   at ", report); // a stack trace, not just a message
        Assert.Contains("eq2log_Tester.txt", report);
        // The crash file may quote the line (it never leaves the machine
        // unless the user sends it)…
        Assert.Contains("private words", report);
        // …the journal never does: it is copied into every postmortem.
        var journal = Journal();
        Assert.Contains("log line skipped: eq2log_Tester.txt", journal);
        Assert.Contains("InvalidOperationException", journal);
        Assert.DoesNotContain("private words", journal);
        Assert.DoesNotContain("tells you", journal);
    }

    [Fact]
    public void A_Long_Line_Is_Quoted_Truncated_In_Its_Report()
    {
        using var source = Source();
        var tail = new string('x', 2000);

        source.PumpLine(Tailed($"({Epoch})[s] head {tail} {PoisonMark} about the raid"));

        var report = File.ReadAllText(Assert.Single(Reports("log-line")));
        Assert.Contains("head xxx", report);
        Assert.DoesNotContain(tail, report);
    }

    [Fact]
    public void A_Handler_That_Throws_Mid_Line_Costs_That_Line_Only()
    {
        // The half-processed case: the idle rule ends a fight at the top of
        // the next line, and a subscriber of that announcement throws. The
        // fight must stay ended and in history, and the tail must go on.
        using var source = Source();
        var announced = 0;
        source.Engine.EncounterEnded += _ =>
        {
            announced++;
            throw new InvalidOperationException("subscriber broke");
        };

        Assert.True(source.PumpLine(Tailed(Hit(0))));
        // 60 s of silence: this line's arrival ends the first fight — and
        // is itself lost to the throw.
        Assert.True(source.PumpLine(Tailed(Hit(60))));
        Assert.True(source.PumpLine(Tailed(Hit(61))));

        Assert.Equal(1, announced);
        Assert.Null(source.Error);
        Assert.Equal(1, source.SkippedLines);
        Assert.Equal(2, source.Engine.History.Count);
        Assert.False(source.Engine.History[0].Active);
        Assert.True(source.Engine.InCombat);
        Assert.Same(source.Engine.History[1], source.Engine.ActiveEncounter);
    }

    [Fact]
    public void An_Unbroken_Run_Of_Failures_Stops_The_Source_With_Evidence()
    {
        using var source = Source();

        for (var i = 1; i < LineFaultBreaker.DefaultTripAfter; i++)
            Assert.True(source.PumpLine(Tailed(Poison)), $"stopped early at failure {i}");
        Assert.Null(source.Error);

        Assert.False(source.PumpLine(Tailed(Poison)));

        Assert.NotNull(source.Error);
        Assert.Contains($"{LineFaultBreaker.DefaultTripAfter} lines in a row", source.Error.Message);
        Assert.IsType<InvalidOperationException>(source.Error.InnerException);
        Assert.Equal(LineFaultBreaker.DefaultTripAfter, source.SkippedLines);
        // The stop report bypasses the rate limit — the first skipped
        // line's report was written a moment earlier.
        var stopped = File.ReadAllText(Assert.Single(Reports("log-source-stopped")));
        Assert.Contains("InvalidOperationException", stopped);
        Assert.Contains("source STOPPED: eq2log_Tester.txt", Journal());
        // And the journal stayed small: 200 failures, not 200 lines.
        Assert.True(Journal().Split('\n').Length < 10);
    }

    [Fact]
    public void Lines_That_Are_Not_Eq2_Shaped_Are_Neither_Failures_Nor_Proof_Of_Health()
    {
        using var source = Source();

        Assert.True(source.PumpLine(Tailed("")));
        Assert.True(source.PumpLine(Tailed("not a log line at all")));

        Assert.Equal(0, source.SkippedLines);
        Assert.Null(source.LastLineError);
        Assert.Empty(Reports("log-line"));
    }

    [Fact]
    public void Line_Reports_Are_Capped_Per_Source_So_A_Storm_Cannot_Rotate_Out_Older_Reports()
    {
        using var source = Source();

        for (var i = 0; i < 20; i++)
        {
            CrashLog.ResetRateLimit(); // as if each failure were seconds apart
            source.PumpLine(Tailed(Poison));
            source.PumpLine(Tailed(Hit(i)));
        }

        Assert.Equal(20, source.SkippedLines);
        Assert.Equal(5, Reports("log-line").Length);
    }

    [Fact]
    public async Task The_Tail_Loop_Survives_A_Poison_Line_In_A_Real_File()
    {
        var path = _dir.FileIn("eq2log_Tester.txt");
        // Stamped "now", as the lines of a live tail are.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await File.WriteAllLinesAsync(path, [Hit(0, from: now), PoisonAt(now), Hit(1, from: now), Hit(2, from: now)]);
        using var source = Source();

        source.Start();
        // Matched, not seen: LinesSeen ticks at the START of a line, so
        // waiting on it can return while the last line is still mid-flight.
        await WaitFor(() => source.Processor.LinesMatched >= 3);

        Assert.Null(source.Error);
        Assert.Equal(1, source.SkippedLines);
        Assert.Equal(3, source.Processor.LinesMatched);

        // Still tailing: a line appended AFTER the poison one is picked up.
        await File.AppendAllLinesAsync(path, [Hit(3, from: now)]);
        await WaitFor(() => source.Processor.LinesMatched >= 4);
        Assert.Null(source.Error);
    }

    [Fact]
    public async Task A_Reader_Failure_Still_Stops_The_Source_But_Now_Leaves_Evidence()
    {
        var path = _dir.FileIn("eq2log_Tester.txt");
        await File.WriteAllLinesAsync(path, [Hit(0), Hit(1)]);
        // A byte-range lock held by another handle: the tail reader can
        // OPEN the file (sharing allows it) but its READ fails with an
        // IOException — a genuine reader failure, not a bad line.
        using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        locker.Lock(0, locker.Length);
        using var source = Source();

        source.Start();
        await WaitFor(() => source.Error is not null);

        Assert.IsAssignableFrom<IOException>(source.Error);
        Assert.Equal(0, source.SkippedLines);
        var report = File.ReadAllText(Assert.Single(Reports("log-reader")));
        Assert.Contains("IOException", report);
        Assert.Contains("eq2log_Tester.txt", report);
        Assert.Contains("source STOPPED: eq2log_Tester.txt — the log reader failed", Journal());
        locker.Unlock(0, locker.Length);
    }

    [Fact]
    public void The_Sources_Row_Shows_Skips_Without_Calling_A_Live_Source_Dead()
    {
        using var source = Source();
        source.PumpLine(Tailed(Hit(0)));
        Assert.DoesNotContain("skipped", SourcesViewModel.StatusFor(source));

        source.PumpLine(Tailed(Poison));

        var status = SourcesViewModel.StatusFor(source);
        Assert.DoesNotContain("error:", status);
        Assert.Contains("1 matched", status);
        Assert.Contains("1 skipped on error (last: InvalidOperationException)", status);
    }

    [Fact]
    public void The_Sources_Row_Says_Error_Once_The_Source_Has_Stopped()
    {
        using var source = Source();
        for (var i = 0; i < LineFaultBreaker.DefaultTripAfter; i++)
            source.PumpLine(Tailed(Poison));

        var status = SourcesViewModel.StatusFor(source);

        Assert.StartsWith("error: stopped after 200 lines in a row", status);
    }

    // ── A stopped source ends its fight ─────────────────────────────────────

    [Fact]
    public void A_Source_Stopped_By_The_Breaker_Ends_Its_Fight_And_Announces_It_Once()
    {
        using var source = Source();
        var announced = 0;
        Exception? errorAtAnnouncement = new InvalidOperationException("never announced");
        var stopReportsAtAnnouncement = -1;
        source.Engine.EncounterEnded += _ =>
        {
            announced++;
            errorAtAnnouncement = source.Error;
            stopReportsAtAnnouncement = Reports("log-source-stopped").Length;
        };
        Assert.True(source.PumpLine(Tailed(Hit(0))));
        Assert.True(source.PumpLine(Tailed(Hit(1))));
        Assert.True(source.Engine.InCombat);

        for (var i = 0; i < LineFaultBreaker.DefaultTripAfter; i++)
            source.PumpLine(Tailed(Poison));

        // Before: the source stopped with its engine still in combat — the
        // fight was never ended, so never saved and never uploaded.
        Assert.NotNull(source.Error);
        Assert.False(source.Engine.InCombat);
        Assert.Null(source.Engine.ActiveEncounter);
        var fight = Assert.Single(source.Engine.History);
        Assert.False(fight.Active);
        // What parsed before the fault is all there.
        Assert.Equal(1000, fight.Combatants["TESTER"].Damage);
        Assert.Equal(1, announced);
        // The order: the stop report first, then the fight, and Error only
        // after both — whoever sees Error finds the rest done.
        Assert.Equal(1, stopReportsAtAnnouncement);
        Assert.Null(errorAtAnnouncement);
        Assert.Contains("lines in a row failed", Journal());
        Assert.Contains("; its fight in progress was ended", Journal());
        Assert.DoesNotContain("IN COMBAT", SourcesViewModel.StatusFor(source));
    }

    [Fact]
    public void A_Source_Stopped_Out_Of_Combat_Announces_Nothing()
    {
        using var source = Source();
        var announced = 0;
        source.Engine.EncounterEnded += _ => announced++;

        for (var i = 0; i < LineFaultBreaker.DefaultTripAfter; i++)
            source.PumpLine(Tailed(Poison));

        Assert.NotNull(source.Error);
        Assert.Equal(0, announced);
        Assert.Empty(source.Engine.History);
        Assert.DoesNotContain("fight in progress", Journal());
    }

    [Fact]
    public void A_Consumer_That_Throws_On_The_Final_Announcement_Does_Not_Block_The_Stop()
    {
        using var source = Source();
        source.Engine.EncounterEnded += static _ => throw new InvalidOperationException("subscriber broke");
        Assert.True(source.PumpLine(Tailed(Hit(0))));

        for (var i = 0; i < LineFaultBreaker.DefaultTripAfter; i++)
            source.PumpLine(Tailed(Poison));

        // The fight is ended and in history even so, and the source still
        // says it stopped.
        Assert.NotNull(source.Error);
        Assert.Contains("lines in a row", source.Error.Message);
        Assert.False(source.Engine.InCombat);
        Assert.False(Assert.Single(source.Engine.History).Active);
        var report = File.ReadAllText(Assert.Single(Reports("fight-consumer")));
        Assert.Contains("because this source stopped", report);
    }

    [Fact]
    public async Task A_Reader_Failure_Mid_Fight_Ends_The_Fight_Before_The_Source_Says_It_Stopped()
    {
        var path = _dir.FileIn("eq2log_Tester.txt");
        await File.WriteAllLinesAsync(path, [Hit(0), Hit(1)]);
        using var source = Source();
        var announced = 0;
        Exception? errorAtAnnouncement = new InvalidOperationException("never announced");
        source.Engine.EncounterEnded += _ =>
        {
            announced++;
            errorAtAnnouncement = source.Error;
        };

        source.Start();
        await WaitFor(() => source.Processor.LinesMatched >= 2);
        Assert.Null(source.Error);

        // The fight is under way. Now the log grows, and the new bytes
        // cannot be read: a byte-range lock held by another handle over
        // exactly the region it appends to.
        using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var end = locker.Length;
        locker.Lock(end, 1 << 16);
        locker.Position = end;
        await locker.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Hit(2) + Environment.NewLine));
        await locker.FlushAsync();

        // Error is set last, so waiting on it is waiting on everything.
        await WaitFor(() => source.Error is not null);

        Assert.IsAssignableFrom<IOException>(source.Error);
        Assert.Equal(1, announced);
        Assert.Null(errorAtAnnouncement);
        Assert.False(source.Engine.InCombat);
        var fight = Assert.Single(source.Engine.History);
        Assert.False(fight.Active);
        Assert.Equal(1000, fight.Combatants["TESTER"].Damage);
        Assert.Single(Reports("log-reader"));
        Assert.Contains("the log reader failed (IOException); its fight in progress was ended", Journal());
        locker.Unlock(end, 1 << 16);
    }

    // ── Alert-handler faults: contained, reported, never a failed line ──────

    [Fact]
    public void A_Throwing_Alert_Handler_Costs_No_Line_And_Leaves_Its_Own_Evidence()
    {
        using var source = SourceWithBrokenAlertHandler();

        Assert.True(source.PumpLine(Tailed(Hit(0))));
        // Before: the handler's throw skipped this line — 500 damage lost
        // to a bug in the attendance code.
        Assert.True(source.PumpLine(Tailed(RaidHit(1))));

        Assert.Null(source.Error);
        Assert.Equal(0, source.SkippedLines);
        Assert.Null(source.LastLineError);
        Assert.Equal(2, source.Processor.LinesMatched);
        Assert.Equal(1000, source.Engine.ActiveEncounter!.Combatants["TESTER"].Damage);
        Assert.Equal(1, source.AlertFaults);
        Assert.IsType<InvalidOperationException>(source.LastAlertError);
        // Its own report kind, saying what did not go wrong…
        Assert.Empty(Reports("log-line"));
        var report = File.ReadAllText(Assert.Single(Reports(LogSource.AlertKind)));
        Assert.Contains("Alert stage: raid line", report);
        Assert.Contains("no combat data is missing", report);
        Assert.Contains("InvalidOperationException", report);
        Assert.Contains("   at ", report);
        Assert.Contains("Bane of the raid", report);
        // …and a journal line with the stage and the type, never the text.
        var journal = Journal();
        Assert.Contains("alert handler FAILED: eq2log_Tester.txt — raid line (InvalidOperationException, 1 this session); the line was still parsed", journal);
        Assert.DoesNotContain("Bane", journal);
        Assert.DoesNotContain("log line skipped", journal);
        // The Sources row does not call a parsed line skipped.
        Assert.DoesNotContain("skipped", SourcesViewModel.StatusFor(source));
    }

    [Fact]
    public void Alert_Faults_Never_Trip_The_Breaker_However_Many_In_A_Row()
    {
        using var source = SourceWithBrokenAlertHandler();

        for (var i = 0; i < 2 * LineFaultBreaker.DefaultTripAfter; i++)
            Assert.True(source.PumpLine(Tailed(RaidHit(0))), $"stopped at line {i}");

        // Every line was parsed, so nothing here is a failed line.
        Assert.Null(source.Error);
        Assert.Equal(0, source.SkippedLines);
        Assert.Equal(2 * LineFaultBreaker.DefaultTripAfter, source.AlertFaults);
        Assert.Equal(2 * LineFaultBreaker.DefaultTripAfter, source.Processor.LinesMatched);
        Assert.Equal(500L * 2 * LineFaultBreaker.DefaultTripAfter, source.Engine.ActiveEncounter!.Combatants["TESTER"].Damage);
        Assert.Empty(Reports("log-source-stopped"));
        // And the evidence stayed small: 400 faults, not 400 lines.
        Assert.InRange(Reports(LogSource.AlertKind).Length, 1, 5);
        Assert.True(Journal().Split('\n').Length < 10);
    }

    [Fact]
    public void Alert_Reports_Are_Capped_Per_Source_Separately_From_Line_Reports()
    {
        using var source = SourceWithBrokenAlertHandler();

        for (var i = 0; i < 20; i++)
        {
            CrashLog.ResetRateLimit(); // as if each fault were seconds apart
            source.PumpLine(Tailed(RaidHit(i)));
            CrashLog.ResetRateLimit();
            source.PumpLine(Tailed(Poison));
        }

        Assert.Equal(20, source.AlertFaults);
        Assert.Equal(20, source.SkippedLines);
        Assert.Equal(5, Reports(LogSource.AlertKind).Length);
        Assert.Equal(5, Reports("log-line").Length);
    }

    [Fact]
    public async Task The_Tail_Loop_Reports_Alert_Faults_From_A_Real_File_And_Keeps_Every_Line()
    {
        var path = _dir.FileIn("eq2log_Tester.txt");
        // Stamped "now": the alert stages only run for LIVE lines, and a
        // line read from a file is live when its own time is moments old.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string LiveRaidHit(int secondsIn) =>
            $"({now + secondsIn})[s] YOUR Smite hits Bane of the raid for 500 divine damage.";
        await File.WriteAllLinesAsync(path, [Hit(0, from: now), Hit(1, from: now), LiveRaidHit(2), LiveRaidHit(3)]);
        using var source = SourceWithBrokenAlertHandler();

        source.Start();
        // A fault's count is published after its evidence, which is
        // written after its line has been processed in full — and the
        // second fault belongs to the LAST line. So once it shows, every
        // line is done and the pump is idle.
        await WaitFor(() => source.AlertFaults >= 2);

        Assert.Null(source.Error);
        Assert.Equal(0, source.SkippedLines);
        Assert.Equal(2, source.AlertFaults);
        Assert.Equal(2000, source.Engine.ActiveEncounter!.Combatants["TESTER"].Damage);
        Assert.NotEmpty(Reports(LogSource.AlertKind));
        Assert.Contains("alert handler FAILED: eq2log_Tester.txt — raid line", Journal());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the log pump");
            await Task.Delay(10);
        }
    }
}
