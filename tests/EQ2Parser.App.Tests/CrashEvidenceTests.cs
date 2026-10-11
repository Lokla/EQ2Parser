using System.IO;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.Tests;

/// <summary>
/// The evidence trail around a dirty exit: crash reports that never
/// overwrite each other, a postmortem header that fits what the dead
/// session actually left behind, and crash-dump registration under the
/// executable that is really running.
/// </summary>
public sealed class CrashEvidenceTests : IDisposable
{
    private readonly TestDataDir _dir = new();

    public CrashEvidenceTests() => CrashLog.ResetRateLimit();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    private static readonly DateTime Start = new(2026, 3, 14, 20, 0, 0, 250);
    private static readonly DateTime NextStart = new(2026, 3, 14, 22, 30, 0, 750);

    // ── CrashLog ────────────────────────────────────────────────────────────

    [Fact]
    public void A_Report_Carries_Its_Context_Above_The_Stack_Trace()
    {
        var path = CrashLog.Report("log-line", Thrown(), "Log source: eq2log_Tester.txt");

        Assert.NotNull(path);
        var text = File.ReadAllText(path);
        Assert.Contains("log-line exception", text);
        Assert.True(
            text.IndexOf("Log source: eq2log_Tester.txt", StringComparison.Ordinal)
            < text.IndexOf("InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains("   at ", text);
    }

    [Fact]
    public void Reports_Are_Rate_Limited_Unless_Marked_Always()
    {
        Assert.NotNull(CrashLog.Report("log-line", Thrown()));
        Assert.Null(CrashLog.Report("log-line", Thrown()));
        Assert.NotNull(CrashLog.Report("log-reader", Thrown(), always: true));
        // A failed startup is the process's last words — it lands as well.
        Assert.NotNull(CrashLog.ReportStartupFailure(Thrown()));
    }

    [Fact]
    public void Two_Reports_Of_One_Kind_In_One_Second_Do_Not_Overwrite_Each_Other()
    {
        // Several log sources dying together when a drive drops.
        List<string?> paths = [];
        for (var i = 0; i < 4; i++)
            paths.Add(CrashLog.Report("log-reader", Thrown(), $"Log source: {i}", always: true));

        Assert.DoesNotContain(null, paths);
        Assert.Equal(4, paths.Distinct().Count());
        Assert.Equal(4, Directory.GetFiles(CrashLog.Directory, "crash-*-log-reader*.txt").Length);
    }

    [Theory]
    [InlineData("crash-2026-03-14_210501-fatal.txt", "fatal", true)]
    [InlineData("crash-2026-03-14_210501-crash-loop.txt", "crash-loop", true)]
    [InlineData("crash-2026-03-14_210501-fatal-startup.txt", "fatal-startup", true)]
    [InlineData("crash-2026-03-14_210501-ui-thread.txt", "ui-thread", false)]
    [InlineData("crash-2026-03-14_210501-background-task.txt", "background-task", false)]
    [InlineData("crash-2026-03-14_210501-log-line.txt", "log-line", false)]
    // The suffix a same-second sibling gets is not part of the kind.
    [InlineData("crash-2026-03-14_210501-log-reader-3.txt", "log-reader", false)]
    [InlineData("crash-2026-03-14_210501-fatal-2.txt", "fatal", true)]
    public void A_Report_Name_Reads_Back_As_Its_Time_And_Kind(string name, string kind, bool endsProcess)
    {
        Assert.True(CrashLog.TryParseReportName(name, out var written, out var parsedKind));
        Assert.Equal(new DateTime(2026, 3, 14, 21, 5, 1), written);
        Assert.Equal(kind, parsedKind);
        Assert.Equal(endsProcess, CrashLog.EndsProcess(parsedKind));
    }

    [Theory]
    [InlineData("postmortem-2026-03-14_210501.txt")]
    [InlineData("crash-.txt")]
    [InlineData("crash-2026-03-14_210501.txt")]
    [InlineData("crash-2026-03-14_210501-.txt")]
    [InlineData("crash-not-a-timestamp-at-all-fatal.txt")]
    public void A_Name_That_Is_Not_A_Report_Is_Refused(string name) =>
        Assert.False(CrashLog.TryParseReportName(name, out _, out _));

    [Fact]
    public void The_Names_The_Writer_Produces_Are_The_Names_The_Parser_Reads()
    {
        var first = CrashLog.Report("log-reader", Thrown(), always: true);
        var second = CrashLog.Report("log-reader", Thrown(), always: true);

        foreach (var path in new[] { first, second })
        {
            Assert.NotNull(path);
            Assert.True(CrashLog.TryParseReportName(Path.GetFileName(path), out var written, out var kind));
            Assert.Equal("log-reader", kind);
            Assert.InRange(written, DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1));
        }
    }

    // ── Postmortem: which reports belong to the dead session ────────────────

    [Fact]
    public void The_Journal_Start_Is_Read_From_Its_First_Line()
    {
        var journal = "[2026-03-14 20:00:00.250] session start — EQ2Parser 1.2.3\r\n[2026-03-14 20:00:01.000] history restored\r\n";

        Assert.Equal(Start, SessionJournal.SessionStart(journal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("session start with no stamp")]
    [InlineData("[not-a-date-not-a-date-xx] session start")]
    public void An_Unreadable_Journal_Start_Is_Unknown_Not_A_Guess(string journal) =>
        Assert.Null(SessionJournal.SessionStart(journal));

    [Fact]
    public void A_Journal_Written_By_This_Build_Reads_Back()
    {
        SessionJournal.Mark("anything");

        var start = SessionJournal.SessionStart(File.ReadAllText(SessionJournal.FilePath));

        Assert.NotNull(start);
        Assert.InRange(start.Value, DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1));
    }

    [Fact]
    public void Only_Reports_Written_During_The_Dead_Session_Are_Attributed_To_It()
    {
        string[] files =
        [
            @"crashes\crash-2026-03-14_195959-fatal.txt",      // the session before
            @"crashes\crash-2026-03-14_200000-ui-thread.txt",  // same second as the start
            @"crashes\crash-2026-03-14_213000-log-line.txt",
            @"crashes\crash-2026-03-14_222959-fatal.txt",
            @"crashes\crash-2026-03-14_223000-fatal-startup.txt", // this launch's own
            @"crashes\postmortem-2026-03-14_210000.txt",
        ];

        var (ending, other) = SessionJournal.ReportsFromSession(files, Start, NextStart);

        Assert.Equal(["crash-2026-03-14_222959-fatal.txt"], ending);
        Assert.Equal(["crash-2026-03-14_200000-ui-thread.txt", "crash-2026-03-14_213000-log-line.txt"], other);
    }

    // ── Postmortem: the header ──────────────────────────────────────────────

    [Fact]
    public void A_Session_That_Left_A_Fatal_Report_Is_Not_Described_As_Having_None()
    {
        var header = SessionJournal.PostmortemHeader(
            startKnown: true, ending: ["crash-2026-03-14_222959-fatal.txt"], other: [], dumpsEnabled: true, dumps: []);

        Assert.Contains("left a", header);
        Assert.Contains("crash-2026-03-14_222959-fatal.txt", header);
        Assert.DoesNotContain("left no", header);
        // With the cause in hand, nobody is sent to their antivirus.
        Assert.DoesNotContain("antivirus", header);
        Assert.DoesNotContain("AV's", header);
    }

    [Fact]
    public void A_Session_With_No_Report_Keeps_The_Native_Or_External_Reading()
    {
        // On a machine that HAD local dumps enabled, a missing dump leans
        // the same way — and is still not called proof.
        var header = SessionJournal.PostmortemHeader(
            startKnown: true, ending: [], other: [], dumpsEnabled: true, dumps: []);

        Assert.Contains("left no", header);
        Assert.Contains("antivirus", header);
        Assert.Contains("no dump from it was found", header);
        Assert.Contains("It is not proof", header);
    }

    [Fact]
    public void Survivable_Reports_Are_Listed_But_Not_Blamed()
    {
        var header = SessionJournal.PostmortemHeader(
            startKnown: true, ending: [], other: ["crash-2026-03-14_213000-log-line.txt"], dumpsEnabled: true, dumps: []);

        Assert.Contains("left no", header);
        Assert.Contains("crash-2026-03-14_213000-log-line.txt", header);
        Assert.Contains("none is the reason it ended", header);
    }

    [Theory]
    // The machine did not have local dumps enabled: none could exist.
    [InlineData(false)]
    // Nothing recorded: a journal from a build up to v0.5.7, which asked
    // for dumps under a registry hive Windows does not read.
    [InlineData(null)]
    public void No_Dump_Means_Nothing_Unless_The_Machine_Had_Dumps_Enabled(bool? dumpsEnabled)
    {
        var header = SessionJournal.PostmortemHeader(
            startKnown: true, ending: [], other: [], dumpsEnabled, dumps: []);

        Assert.Contains("says nothing either way", header);
        Assert.DoesNotContain("no dump from it was found", header);
        Assert.DoesNotContain("leans", header);
    }

    [Fact]
    public void A_Dump_That_Exists_Is_Named()
    {
        var header = SessionJournal.PostmortemHeader(
            startKnown: true, ending: [], other: [], dumpsEnabled: true, dumps: [@"X:\dumps\EQ2Parser.App.exe.1234.dmp"]);

        Assert.Contains(@"X:\dumps\EQ2Parser.App.exe.1234.dmp", header);
        Assert.DoesNotContain("no dump from it was found", header);
        Assert.DoesNotContain("says nothing either way", header);
    }

    [Fact]
    public void An_Unknown_Start_Claims_Nothing_About_Reports()
    {
        var header = SessionJournal.PostmortemHeader(
            startKnown: false, ending: [], other: [], dumpsEnabled: null, dumps: []);

        Assert.Contains("cannot tell", header);
        Assert.DoesNotContain("left no", header);
    }

    // ── Crash-dump registration ─────────────────────────────────────────────

    [Theory]
    [InlineData(@"X:\apps\EQ2Parser\current\EQ2Parser.App.exe", "EQ2Parser.App.exe")]
    // Follows a rename instead of going stale again.
    [InlineData(@"X:\apps\EQ2Parser\current\Renamed.exe", "Renamed.exe")]
    public void Dumps_Are_Registered_Under_The_Running_Image_Name(string processPath, string expected) =>
        Assert.Equal(expected, SessionJournal.DumpImageName(processPath));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // Shared hosts: registering them would dump unrelated programs.
    [InlineData(@"X:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"X:\tools\testhost.exe")]
    [InlineData(@"X:\apps\not-an-executable")]
    public void A_Shared_Or_Unknown_Host_Is_Never_Registered(string? processPath) =>
        Assert.Null(SessionJournal.DumpImageName(processPath));

    private static InvalidOperationException Thrown()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }
}
