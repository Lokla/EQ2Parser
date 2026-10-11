using System.Net;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Raid;

namespace EQ2Parser.App.Tests;

/// <summary>A log read straight from &lt;install&gt;/logs/ (the legacy layout)
/// has no server folder, so its uploads would carry an empty logger_server
/// — which the site answers 400 to, every time (it has no default world).
/// Neither upload path may send that request: the auto path skips, counts
/// and explains; the manual path refuses with the same explanation.</summary>
public sealed class UploadServiceNoServerTests : IDisposable
{
    private const string LegacyLog = @"C:\EQ2\logs\eq2log_Tester.txt";
    private const string VarsoonLog = @"C:\EQ2\logs\Varsoon\eq2log_Tester.txt";

    private readonly TestDataDir _dir = new();
    private readonly FakeLexiconSite _site = new();

    public UploadServiceNoServerTests()
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
    [InlineData(false)]
    [InlineData(true)] // raid-only on, one player: the no-server reason still wins — nothing from this log can upload
    public void Auto_Upload_Skips_A_Log_Outside_A_Server_Folder(bool raidOnly)
    {
        using var svc = _site.NewService(raidOnly, players: 1);

        Assert.False(svc.ShouldAutoUpload(FakeLexiconSite.Fight(LegacyLog)));
        Assert.False(svc.ShouldAutoUpload(FakeLexiconSite.Fight(LegacyLog, "Lady Alice")));

        Assert.Equal(2, svc.SkippedNoServer);
        Assert.Equal(0, svc.SkippedNonRaid);
        Assert.Contains("Lady Alice", svc.Status);
        Assert.Contains("not inside a server folder", svc.Status);
    }

    [Fact]
    public void A_Log_In_A_Server_Folder_Is_Not_Skipped()
    {
        using var svc = _site.NewService();
        Assert.True(svc.ShouldAutoUpload(FakeLexiconSite.Fight(VarsoonLog)));
        Assert.Equal(0, svc.SkippedNoServer);
    }

    [Fact]
    public async Task Auto_Path_Sends_Nothing_For_A_Legacy_Log()
    {
        using var svc = _site.NewService();

        svc.OnEncounterEnded(FakeLexiconSite.Fight(LegacyLog));
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog)); // FIFO: anything queued before it would be sent first
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("uploaded this session"), "the upload behind it");

        Assert.Equal(["Varsoon"], _site.Ingested);
        Assert.Equal(1, svc.SkippedNoServer);
    }

    [Fact]
    public async Task Manual_Upload_Of_A_Legacy_Log_Is_Refused_Locally_With_The_Reason()
    {
        using var svc = _site.NewService();

        svc.UploadFight([FakeLexiconSite.Fight(LegacyLog)]);

        // Synchronous: the reason is on the status line before anything
        // could have reached a queue.
        Assert.Contains("Lord Bob", svc.Status);
        Assert.Contains("not inside a server folder", svc.Status);
        Assert.Equal(0, svc.SkippedNoServer); // a refused click is not an auto-path skip

        svc.UploadFight([FakeLexiconSite.Fight(VarsoonLog)]);
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("uploaded this session"), "the upload behind it");
        Assert.Equal(["Varsoon"], _site.Ingested);
    }

    [Fact]
    public async Task Manual_Upload_Sends_The_Sources_That_Do_Have_A_Server()
    {
        // A fight seen by two logs, one of them in the legacy root: the
        // other view is still worth sending.
        using var svc = _site.NewService();

        svc.UploadFight([FakeLexiconSite.Fight(LegacyLog), FakeLexiconSite.Fight(VarsoonLog)]);
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("uploaded this session"), "the sendable source");

        Assert.Equal(["Varsoon"], _site.Ingested);
    }

    [Fact]
    public async Task Manual_Refusal_Leaves_An_Auth_Pause_In_Place()
    {
        // A manual upload clears a bad-token pause because the click IS the
        // retry. A click that cannot send anything retries nothing, so it
        // must not quietly un-pause the queue for the next automatic fight.
        _site.Refusals["Varsoon"] = (HttpStatusCode.Unauthorized, null, "Invalid or revoked token");
        using var svc = _site.NewService();
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog));
        // The state, not a word in the (translated) status line. The line
        // is waited for as well — it is published a moment after the state
        // — so it cannot land on top of the refusal asserted next.
        await FakeLexiconSite.WaitFor(
            () => svc.UploadsPaused && svc.Status == EQ2Parser.App.Localization.Loc.Format("UploadQ_PausedTokenRejected", 401),
            "the pause");

        svc.UploadFight([FakeLexiconSite.Fight(LegacyLog)]);
        Assert.Contains("not inside a server folder", svc.Status);
        Assert.True(svc.UploadsPaused);
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog));
        await Task.Delay(100);
        Assert.Single(_site.Ingested); // still paused

        // The control: a manual upload that CAN send does clear it.
        svc.UploadFight([FakeLexiconSite.Fight(VarsoonLog)]);
        await FakeLexiconSite.WaitFor(() => _site.Ingested.Count == 2, "the explicit retry");
    }

    [Fact]
    public void Attendance_Snapshot_Is_Not_Built_Without_A_Server_Folder()
    {
        // The attendance ingest and the raid-mains lookup both answer 400
        // without a server, so the tick has nothing it can send.
        var now = DateTimeOffset.FromUnixTimeSeconds(1_775_000_600);
        List<RaidMemberState> snapshot =
        [
            new() { Name = "Tester", InRaid = true, RaidFirstSeen = now.AddMinutes(-5), RaidLastSeen = now },
            new() { Name = "Benched", Online = true, OnlineFirstSeen = now.AddMinutes(-5), OnlineLastSeen = now },
        ];

        Assert.Null(UploadService.BuildAttendancePayload("Tester", LegacyLog, now, snapshot));

        var payload = UploadService.BuildAttendancePayload("Tester", VarsoonLog, now, snapshot);
        Assert.NotNull(payload);
        Assert.Equal("Varsoon", payload.LoggerServer);
        Assert.Equal("Tester", payload.LoggerName);
        Assert.Equal(now.ToUnixTimeSeconds(), payload.SentAt);
        Assert.Equal("Tester", Assert.Single(payload.RaidMembers).Name);
        Assert.Equal("Benched", Assert.Single(payload.OnlineGuildies).Name);
    }
}
