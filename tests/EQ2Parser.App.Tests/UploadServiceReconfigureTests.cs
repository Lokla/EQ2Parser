using System.Net;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.App.Tests;

/// <summary>What happens to uploads already queued, or already in flight,
/// when the Settings card changes the configuration under them: a cleared
/// token drops the queued fights at once instead of stalling on retries,
/// and an answer to a send made with the OLD token can neither pause the
/// queue nor mark a server refused against the new one.</summary>
public sealed class UploadServiceReconfigureTests : IDisposable
{
    private const string VarsoonLog = @"C:\EQ2\logs\Varsoon\eq2log_Tester.txt";
    private const string KaladimLog = @"C:\EQ2\logs\Kaladim\eq2log_Tester.txt";
    private const string Site = "https://example.test";

    private readonly TestDataDir _dir = new();
    private readonly FakeLexiconSite _site = new();

    public UploadServiceReconfigureTests()
    {
        EQ2Parser.App.Localization.Loc.Initialize("en");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    [Fact]
    public async Task Clearing_The_Token_Drops_Queued_Fights_At_Once()
    {
        // One upload is in flight (held by the site) and two wait behind it
        // when the token is cleared. Without a client the send used to
        // answer "status 0", which the queue retries after 5 s and again
        // after 30 s — per fight. Now each is dropped the moment the drain
        // reaches it, and the line says why.
        using var svc = _site.NewService();
        var held = _site.HoldNextUpload();
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog));
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog, "Lady Alice"));
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog, "Sir Carl"));
        await held.Arrived.WaitAsync(TimeSpan.FromSeconds(5));

        svc.Configure(Site, apiToken: null, enabled: true);
        Assert.False(svc.Configured);
        held.Accept();

        // Well inside the five-second wait; the old behaviour needed 70 s.
        await FakeLexiconSite.WaitFor(() => svc.SkippedNotConfigured == 2, "both queued fights dropped");
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("2 dropped this session"), "the reason on the status line");
        Assert.Equal("Sir Carl not uploaded — No API token configured (2 dropped this session)", svc.Status);
        Assert.Single(_site.Ingested); // only the one already in flight ever reached the site
        Assert.False(svc.UploadsPaused);

        // Dropped, not held: saving a token later sends new fights only.
        svc.Configure(Site, "token-b", enabled: true);
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog, "Dame Edith"));
        await FakeLexiconSite.WaitFor(() => _site.Ingested.Count == 2, "the next fight under the new token");
        await Task.Delay(100);
        Assert.Equal(2, _site.Ingested.Count);
        Assert.Equal(["token", "token-b"], _site.Bearers);
    }

    [Fact]
    public async Task A_Refused_Site_Address_Drops_Queued_Fights_With_That_Reason()
    {
        using var svc = _site.NewService();
        var held = _site.HoldNextUpload();
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog));
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog, "Lady Alice"));
        await held.Arrived.WaitAsync(TimeSpan.FromSeconds(5));

        // Plain http to a remote host: the client refuses to exist.
        svc.Configure("http://example.test", "token", enabled: true);
        held.Accept();

        await FakeLexiconSite.WaitFor(() => svc.SkippedNotConfigured == 1, "the queued fight dropped");
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("1 dropped this session"), "the reason on the status line");
        Assert.StartsWith("Lady Alice not uploaded — Uploads need an https:// server URL", svc.Status, StringComparison.Ordinal);
        Assert.Single(_site.Ingested);
    }

    [Fact]
    public async Task A_Rejection_Of_The_Old_Token_Does_Not_Pause_Uploads_Under_The_New_One()
    {
        // The race: a send goes out with the old token, the user saves a
        // new one (Configure clears any pause), and THEN the old send's 401
        // arrives. It used to pause the queue again, so the freshly saved,
        // perfectly good token uploaded nothing until the user saved it a
        // second time.
        using var svc = _site.NewService();
        var held = _site.HoldNextUpload();
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog));
        await held.Arrived.WaitAsync(TimeSpan.FromSeconds(5));

        svc.Configure(Site, "token-b", enabled: true);
        held.Answer(HttpStatusCode.Unauthorized, null, "Invalid or revoked token");

        // The old token's answer is not the new token's: no pause, and the
        // fight goes out again under the configuration that has not judged it.
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("uploaded this session"), "the re-send under the new token");
        Assert.False(svc.UploadsPaused);
        Assert.Equal(["token", "token-b"], _site.Bearers);

        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog, "Lady Alice"));
        await FakeLexiconSite.WaitFor(() => _site.Ingested.Count == 3, "the next fight");
        Assert.Equal("token-b", _site.Bearers[2]);
    }

    [Fact]
    public async Task A_Server_Refusal_From_The_Old_Site_Is_Not_Held_Against_The_New_One()
    {
        using var svc = _site.NewService();
        var held = _site.HoldNextUpload();
        svc.OnEncounterEnded(FakeLexiconSite.Fight(KaladimLog));
        await held.Arrived.WaitAsync(TimeSpan.FromSeconds(5));

        svc.Configure("https://other.example.test", "token-b", enabled: true);
        held.Answer(HttpStatusCode.Forbidden, LexiconUploadClient.RejectServerNotAllowed, "Server 'Kaladim' is not on the allowed list.");
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("uploaded this session"), "the re-send to the new site");

        // Not remembered: the same server's next fight is sent, not skipped.
        svc.OnEncounterEnded(FakeLexiconSite.Fight(KaladimLog, "Lady Alice"));
        await FakeLexiconSite.WaitFor(() => _site.Ingested.Count == 3, "the same server's next fight");
        Assert.Equal(0, svc.SkippedRefusedServer);
        Assert.Equal(["Kaladim", "Kaladim", "Kaladim"], _site.Ingested);
    }

    [Fact]
    public async Task A_Rejection_Of_The_Current_Token_Still_Pauses()
    {
        // The control: no reconfigure in between, so the 401 is this
        // token's and the queue stops.
        using var svc = _site.NewService();
        var held = _site.HoldNextUpload();
        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog));
        await held.Arrived.WaitAsync(TimeSpan.FromSeconds(5));

        held.Answer(HttpStatusCode.Unauthorized, null, "Invalid or revoked token");

        await FakeLexiconSite.WaitFor(() => svc.UploadsPaused, "the pause");
        Assert.Single(_site.Ingested);
    }
}
