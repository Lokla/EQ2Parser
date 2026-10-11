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
    /// timestamp can hold, is refused by LogLine.TryParse now), so the
    /// fault is injected where real ones come from: a subscriber on the line
    /// path. Every source built by <see cref="Source"/> carries a raid-line
    /// handler that throws on a line with this text.</summary>
    private const string PoisonMark = "private words";

    /// <summary>A live, raid-relevant chat line carrying the mark, so it
    /// reaches the throwing handler.</summary>
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

    private LogSource Source(string name = "eq2log_Tester.txt", bool parseFromStart = true)
    {
        var source = new LogSource(_dir.FileIn(name), parseFromStart, new object(), new EngineOptions(), TimeSpan.FromMilliseconds(5));
        source.Processor.RaidLine += static (message, _) =>
        {
            if (message.Contains(PoisonMark, StringComparison.Ordinal))
                throw new InvalidOperationException("raid-line handler broke");
        };
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
        // Stamped "now": the raid-line stage only runs for LIVE lines, and a
        // line read from a file is live when its own time is moments old.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await File.WriteAllLinesAsync(path, [Hit(0, from: now), PoisonAt(now), Hit(1, from: now), Hit(2, from: now)]);
        using var source = Source();

        source.Start();
        await WaitFor(() => source.Processor.LinesSeen >= 4);

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
