using System.IO;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.Tests;

/// <summary>
/// Two questions the next launch asks of the last session's journal, and
/// the answers must be true: did it exit cleanly (only when the clean-exit
/// mark is its final line), and did Windows leave a crash dump (found by
/// looking where one could be, never assumed from a request Windows
/// ignores).
/// </summary>
public sealed class SessionJournalTests : IDisposable
{
    private readonly TestDataDir _dir = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    private static string Journal() => File.ReadAllText(SessionJournal.FilePath);

    private static string Line(string what, string stamp = "2026-03-14 20:00:00.250") => $"[{stamp}] {what}\r\n";

    // ── clean-exit must be the session's final mark ─────────────────────────

    [Fact]
    public void A_Session_That_Ends_With_The_Mark_Exited_Cleanly()
    {
        Assert.True(SessionJournal.EndedCleanly(
            Line("session start — EQ2Parser 1.2.3") + Line("exit begin") + Line("clean-exit")));
        // The last line's own line break is not required (a torn write).
        Assert.True(SessionJournal.EndedCleanly(Line("session start") + "[2026-03-14 22:00:00.000] clean-exit"));
        Assert.True(SessionJournal.EndedCleanly(Line("session start").Replace("\r\n", "\n") + "[2026-03-14 22:00:00.000] clean-exit\n"));
    }

    [Theory]
    // The reported hole: the words inside another breadcrumb. A log file
    // can be called anything.
    [InlineData("source added: eq2log_clean-exit.txt")]
    [InlineData("log line skipped: eq2log_Tester.txt — clean-exit (1 skipped this session)")]
    [InlineData("clean-exit was requested but something else happened")]
    [InlineData("not a clean-exit")]
    [InlineData("clean-exit ")]
    [InlineData("clean-exited")]
    public void The_Words_Inside_Another_Breadcrumb_Are_Not_The_Mark(string breadcrumb)
    {
        // Before: any of these anywhere in the file made a session that
        // then died read as a clean one — no postmortem, evidence gone.
        Assert.False(SessionJournal.EndedCleanly(Line("session start") + Line(breadcrumb)));
        Assert.False(SessionJournal.EndedCleanly(Line("session start") + Line(breadcrumb) + Line("heartbeat ws=200MB gc=50MB sources=1")));
    }

    [Fact]
    public void A_Mark_With_Anything_After_It_Is_Not_A_Clean_Exit()
    {
        // The mark must be FINAL. (End closes the journal so that this
        // cannot happen to a session that really did exit cleanly — below.)
        Assert.False(SessionJournal.EndedCleanly(
            Line("session start") + Line("clean-exit") + Line("heartbeat ws=200MB gc=50MB sources=1")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("clean-exit")]
    [InlineData("clean-exit\r\n")]
    [InlineData("[2026-03-14] clean-exit\r\n")]
    [InlineData("2026-03-14 22:00:00.000 clean-exit\r\n")]
    [InlineData("[2026-03-14 22:00:00.000]clean-exit\r\n")]
    public void A_Mark_Not_In_The_Journals_Own_Line_Form_Is_Not_The_Mark(string journal) =>
        Assert.False(SessionJournal.EndedCleanly(journal));

    [Fact]
    public void The_Mark_End_Writes_Is_The_Mark_The_Next_Launch_Reads()
    {
        SessionJournal.Mark("session start — a test");
        SessionJournal.Mark("source added: eq2log_clean-exit.txt");
        Assert.False(SessionJournal.EndedCleanly(Journal()));

        SessionJournal.End();

        Assert.True(SessionJournal.EndedCleanly(Journal()));
    }

    [Fact]
    public void Nothing_Is_Journaled_After_The_Clean_Exit_Mark()
    {
        SessionJournal.Mark("session start — a test");
        SessionJournal.End();

        // A thread that outlived the exit sequence by a moment.
        SessionJournal.Mark("log line skipped: eq2log_Tester.txt — InvalidOperationException (1 skipped this session)");
        SessionJournal.End();

        var journal = Journal();
        Assert.DoesNotContain("log line skipped", journal);
        Assert.True(SessionJournal.EndedCleanly(journal));
        Assert.Equal(1, journal.Split("clean-exit").Length - 1);
    }

    [Fact]
    public void Ending_One_Session_Does_Not_Silence_The_Next_Data_Folder()
    {
        SessionJournal.Mark("first");
        SessionJournal.End();

        // Another data folder is another journal (each test class has its
        // own; a new launch rewrites the file through Begin).
        using var other = new TestDataDir();
        SessionJournal.Mark("second session, second folder");

        Assert.Contains("second session, second folder", Journal());
    }

    // ── crash dumps: found, not assumed ─────────────────────────────────────

    private const string Image = "EQ2Parser.App.exe";
    private const string Own = @"X:\data\crashes\dumps";
    private const string Shared = @"X:\Users\someone\AppData\Local\CrashDumps";
    private static readonly DateTime Start = new(2026, 3, 14, 20, 0, 0, 250);

    private static List<string> Found(string? image, params (string Path, DateTime Written)[] files) =>
        SessionJournal.DumpsFromSession(files, image, Own, Start);

    [Fact]
    public void A_Dump_In_Windows_Default_Folder_Is_Found_By_The_Apps_Image_Name()
    {
        // Where Windows really writes one when the machine has local
        // dumps enabled — earlier builds only ever looked in their own
        // folder, which Windows never used.
        var found = Found(
            Image,
            (Shared + @"\EQ2Parser.App.exe.4321.dmp", Start.AddMinutes(30)));

        Assert.Equal([Shared + @"\EQ2Parser.App.exe.4321.dmp"], found);
    }

    [Fact]
    public void Other_Programs_Dumps_In_The_Shared_Folder_Are_Not_Ours()
    {
        var found = Found(
            Image,
            (Shared + @"\SomethingElse.exe.4321.dmp", Start.AddMinutes(30)),
            // Same prefix, different program.
            (Shared + @"\EQ2Parser.App.exe.helper.exe.4321.dmp", Start.AddMinutes(30)),
            (Shared + @"\EQ2Parser.App.exeX.4321.dmp", Start.AddMinutes(30)),
            // Not Windows' naming at all.
            (Shared + @"\EQ2Parser.App.exe.dmp", Start.AddMinutes(30)),
            (Shared + @"\EQ2Parser.App.exe..dmp", Start.AddMinutes(30)),
            (Shared + @"\EQ2Parser.App.exe.4321.txt", Start.AddMinutes(30)),
            (Shared + @"\.dmp", Start.AddMinutes(30)));

        Assert.Empty(found);
    }

    [Fact]
    public void A_Dump_From_Before_The_Session_Is_Not_This_Sessions()
    {
        var found = Found(
            Image,
            (Shared + @"\EQ2Parser.App.exe.1.dmp", Start.AddDays(-3)),
            (Shared + @"\EQ2Parser.App.exe.2.dmp", Start.AddMilliseconds(-1)),
            (Own + @"\EQ2Parser.App.exe.3.dmp", Start.AddDays(-1)),
            (Shared + @"\EQ2Parser.App.exe.4.dmp", Start));

        Assert.Equal([Shared + @"\EQ2Parser.App.exe.4.dmp"], found);
    }

    [Fact]
    public void Anything_In_The_Apps_Own_Dump_Folder_Counts()
    {
        // Nothing else writes there, so the name is not asked to prove it.
        var found = Found(
            Image,
            (Own + @"\EQ2Parser.App.exe.77.dmp", Start.AddMinutes(5)),
            (Own.ToUpperInvariant() + @"\renamed-by-hand.DMP", Start.AddMinutes(6)),
            (Own + @"\notes.txt", Start.AddMinutes(7)));

        Assert.Equal(2, found.Count);
        Assert.Contains(Own + @"\EQ2Parser.App.exe.77.dmp", found);
        Assert.Contains(Own.ToUpperInvariant() + @"\renamed-by-hand.DMP", found);
    }

    [Fact]
    public void Under_A_Shared_Host_Nothing_In_A_Shared_Folder_Is_Claimed()
    {
        // "dotnet EQ2Parser.App.dll": the image is dotnet.exe, and so is
        // every other .NET program's.
        var found = Found(
            null,
            (Shared + @"\dotnet.exe.4321.dmp", Start.AddMinutes(30)),
            (Shared + @"\EQ2Parser.App.exe.4321.dmp", Start.AddMinutes(30)),
            (Own + @"\dotnet.exe.99.dmp", Start.AddMinutes(30)));

        Assert.Equal([Own + @"\dotnet.exe.99.dmp"], found);
    }

    [Fact]
    public void The_Same_File_Reached_Through_Two_Folders_Is_Named_Once_And_The_List_Is_Sorted()
    {
        var found = Found(
            Image,
            (Shared + @"\EQ2Parser.App.exe.9.dmp", Start.AddMinutes(2)),
            (Shared + @"\EQ2Parser.App.exe.10.dmp", Start.AddMinutes(1)),
            (Shared.ToLowerInvariant() + @"\EQ2Parser.App.exe.9.dmp", Start.AddMinutes(2)));

        Assert.Equal(2, found.Count);
        Assert.Equal(found.Order(StringComparer.OrdinalIgnoreCase), found);
    }

    [Fact]
    public void The_Image_Name_Matches_Without_Regard_To_Case()
    {
        var found = Found(Image, (Shared + @"\eq2parser.app.EXE.4321.DMP", Start.AddMinutes(30)));

        Assert.Single(found);
    }

    [Fact]
    public void The_Journal_Says_Whether_The_Machine_Had_Dumps_Enabled_Or_Says_Nothing()
    {
        Assert.True(SessionJournal.DumpsEnabledDuring(
            Line("session start") + Line(SessionJournal.DumpsOnMark + "Windows local dumps are enabled on this machine")));
        Assert.False(SessionJournal.DumpsEnabledDuring(
            Line("session start") + Line(SessionJournal.DumpsOffMark + "Windows local dumps are not enabled on this machine")));
        // A build up to v0.5.7: it "requested" dumps under a hive Windows
        // does not read, which says nothing about the machine.
        Assert.Null(SessionJournal.DumpsEnabledDuring(
            Line("session start") + Line("crash dumps requested for EQ2Parser.App.exe")));
        Assert.Null(SessionJournal.DumpsEnabledDuring(""));
    }
}
