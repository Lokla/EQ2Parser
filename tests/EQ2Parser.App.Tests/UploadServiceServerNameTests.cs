using EQ2Parser.App.Services;
using EQ2Parser.Core.Raid;

namespace EQ2Parser.App.Tests;

/// <summary>A log in a folder under logs/ whose name the site would not
/// take as a server name (it starts with a digit, holds a character outside
/// the site's set, or is too long) gets a 400 "malformed" for every fight.
/// It is treated exactly like the log with no server folder at all: the
/// auto path skips, counts and explains; the manual path refuses with the
/// same explanation; nothing is sent, attendance included.</summary>
public sealed class UploadServiceServerNameTests : IDisposable
{
    private const string DigitFirstLog = @"C:\EQ2\logs\2nd account\eq2log_Tester.txt";
    private const string OddCharacterLog = @"C:\EQ2\logs\Varsoon (old)\eq2log_Tester.txt";
    private const string TooLongLog = @"C:\EQ2\logs\Abcdefghijklmnopqrstuvwxyz012345\eq2log_Tester.txt"; // 32 characters
    private const string VarsoonLog = @"C:\EQ2\logs\Varsoon\eq2log_Tester.txt";
    private const string SpacedLog = @"C:\EQ2\logs\Antonia Bayle\eq2log_Tester.txt";

    private readonly TestDataDir _dir = new();
    private readonly FakeLexiconSite _site = new();

    public UploadServiceServerNameTests()
    {
        // The status line goes through Loc; without the dictionaries it
        // would read as the bare key and the assertions on it would be moot.
        EQ2Parser.App.Localization.Loc.Initialize("en");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    [Theory]
    [InlineData(DigitFirstLog, "2nd account")]
    [InlineData(OddCharacterLog, "Varsoon (old)")]
    [InlineData(TooLongLog, "Abcdefghijklmnopqrstuvwxyz012345")]
    public void Auto_Upload_Skips_A_Log_In_A_Folder_That_Is_Not_A_Server_Name(string log, string folder)
    {
        using var svc = _site.NewService(raidOnly: true, players: 1); // the folder reason wins over the raid-only one

        Assert.False(svc.ShouldAutoUpload(FakeLexiconSite.Fight(log)));
        Assert.False(svc.ShouldAutoUpload(FakeLexiconSite.Fight(log, "Lady Alice")));

        Assert.Equal(2, svc.SkippedServerName);
        Assert.Equal(0, svc.SkippedNoServer); // its own count: this log HAS a folder
        Assert.Equal(0, svc.SkippedNonRaid);
        // The line names the fight, the folder and what a server name is.
        Assert.StartsWith($"Lady Alice not uploaded — its log is in a folder named \"{folder}\"", svc.Status, StringComparison.Ordinal);
        Assert.Contains("does not accept as a server name", svc.Status);
        Assert.Contains("31 characters", svc.Status);
    }

    [Theory]
    [InlineData(VarsoonLog)]
    [InlineData(SpacedLog)] // a space inside is part of the site's set
    public void A_Log_In_A_Folder_Named_Like_A_Server_Is_Not_Skipped(string log)
    {
        using var svc = _site.NewService();
        Assert.True(svc.ShouldAutoUpload(FakeLexiconSite.Fight(log)));
        Assert.Equal(0, svc.SkippedServerName);
        Assert.Equal(0, svc.SkippedNoServer);
    }

    [Fact]
    public async Task Auto_Path_Sends_Nothing_For_Such_A_Log()
    {
        using var svc = _site.NewService();

        svc.OnEncounterEnded(FakeLexiconSite.Fight(DigitFirstLog));
        svc.OnEncounterEnded(FakeLexiconSite.Fight(OddCharacterLog));
        svc.OnEncounterEnded(FakeLexiconSite.Fight(SpacedLog)); // FIFO: anything queued before it would be sent first
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("uploaded this session"), "the upload behind them");

        Assert.Equal(["Antonia Bayle"], _site.Ingested);
        Assert.Equal(2, svc.SkippedServerName);
    }

    [Fact]
    public async Task Manual_Upload_Of_Such_A_Log_Is_Refused_Locally_With_The_Reason()
    {
        using var svc = _site.NewService();

        svc.UploadFight([FakeLexiconSite.Fight(DigitFirstLog)]);

        // Synchronous: the reason is on the status line before anything
        // could have reached a queue.
        Assert.StartsWith("Lord Bob not uploaded — its log is in a folder named \"2nd account\"", svc.Status, StringComparison.Ordinal);
        Assert.Equal(0, svc.SkippedServerName); // a refused click is not an auto-path skip

        // A fight seen by two logs: the view from the usable folder still goes.
        svc.UploadFight([FakeLexiconSite.Fight(TooLongLog), FakeLexiconSite.Fight(VarsoonLog)]);
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("uploaded this session"), "the sendable source");
        Assert.Equal(["Varsoon"], _site.Ingested);
    }

    [Fact]
    public void A_Very_Long_Folder_Name_Is_Shortened_In_The_Status_Line()
    {
        // The name can be as long as the file system allows; the line the
        // Settings card shows has to stay readable.
        var folder = new string('x', 200);
        using var svc = _site.NewService();

        Assert.False(svc.ShouldAutoUpload(FakeLexiconSite.Fight($@"C:\EQ2\logs\{folder}\eq2log_Tester.txt")));

        Assert.Contains("\"" + new string('x', 40) + "…\"", svc.Status);
        Assert.DoesNotContain(new string('x', 41), svc.Status);
    }

    [Fact]
    public void Attendance_Snapshot_Is_Not_Built_For_Such_A_Log()
    {
        // The attendance ingest and the raid-mains lookup validate the
        // server with the same function as parse ingest and answer 400.
        var now = DateTimeOffset.FromUnixTimeSeconds(1_775_000_600);
        List<RaidMemberState> snapshot =
        [
            new() { Name = "Tester", InRaid = true, RaidFirstSeen = now.AddMinutes(-5), RaidLastSeen = now },
        ];

        Assert.Null(UploadService.BuildAttendancePayload("Tester", DigitFirstLog, now, snapshot));
        Assert.Null(UploadService.BuildAttendancePayload("Tester", OddCharacterLog, now, snapshot));
        Assert.Null(UploadService.BuildAttendancePayload("Tester", TooLongLog, now, snapshot));
        Assert.Equal("Antonia Bayle", UploadService.BuildAttendancePayload("Tester", SpacedLog, now, snapshot)!.LoggerServer);
    }
}
