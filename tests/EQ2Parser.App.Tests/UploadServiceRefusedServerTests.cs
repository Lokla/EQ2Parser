using System.Net;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.App.Tests;

/// <summary>The app watches every logs/&lt;server&gt;/ folder, so one account
/// can be logging on a server the site accepts and on one it does not. The
/// site's "server not allowed" refusal (403 + the reject header) fails that
/// fight alone: the other server's uploads carry on, later fights from the
/// refused server are skipped without a request, and a new token/URL or a
/// manual upload asks the site again.</summary>
public sealed class UploadServiceRefusedServerTests : IDisposable
{
    private const string VarsoonLog = @"C:\EQ2\logs\Varsoon\eq2log_Tester.txt";
    private const string KaladimLog = @"C:\EQ2\logs\Kaladim\eq2log_Tester.txt";

    private readonly TestDataDir _dir = new();
    private readonly FakeLexiconSite _site = new();

    public UploadServiceRefusedServerTests()
    {
        EQ2Parser.App.Localization.Loc.Initialize("en");
        _site.Refusals["Kaladim"] = (
            HttpStatusCode.Forbidden,
            LexiconUploadClient.RejectServerNotAllowed,
            "Server 'Kaladim' is not on the allowed list. Allowed: Varsoon, Wuoshi.");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    [Fact]
    public async Task Refused_Server_Does_Not_Stop_Another_Servers_Uploads()
    {
        using var svc = _site.NewService();

        svc.OnEncounterEnded(FakeLexiconSite.Fight(KaladimLog));
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("refused"), "the refusal");
        // The line names what the site objected to, not the token.
        Assert.Contains("not on the allowed list", svc.Status);
        Assert.DoesNotContain("token", svc.Status, StringComparison.OrdinalIgnoreCase);

        svc.OnEncounterEnded(FakeLexiconSite.Fight(KaladimLog, "Lady Alice"));
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("skipped Lady Alice"), "the skip");
        Assert.Equal(1, svc.SkippedRefusedServer);

        svc.OnEncounterEnded(FakeLexiconSite.Fight(VarsoonLog));
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("uploaded this session"), "the other server's upload");

        Assert.Equal(["Kaladim", "Varsoon"], _site.Ingested);
    }

    [Fact]
    public async Task Reconfigure_Forgets_Refused_Servers()
    {
        using var svc = _site.NewService();
        svc.OnEncounterEnded(FakeLexiconSite.Fight(KaladimLog));
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("refused"), "the refusal");

        // A new token or site URL: the old answer came from another site or
        // account, so the next fight is sent and judged afresh.
        _site.Refusals.Clear();
        svc.Configure("https://other.example.test", "another-token", enabled: true);
        svc.OnEncounterEnded(FakeLexiconSite.Fight(KaladimLog));
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("uploaded this session"), "the upload after reconfigure");

        Assert.Equal(["Kaladim", "Kaladim"], _site.Ingested);
        Assert.Equal(0, svc.SkippedRefusedServer);
    }

    [Fact]
    public async Task Manual_Upload_Asks_The_Site_Again_For_A_Refused_Server()
    {
        // Right-clicking Upload is the explicit retry: the fight is sent
        // and the user sees the site's own answer, not a local "skipped".
        using var svc = _site.NewService();
        svc.OnEncounterEnded(FakeLexiconSite.Fight(KaladimLog));
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("refused"), "the refusal");

        svc.UploadFight([FakeLexiconSite.Fight(KaladimLog, "Lady Alice")]);
        await FakeLexiconSite.WaitFor(() => svc.Status.Contains("Lady Alice upload refused"), "the second answer");

        Assert.Equal(["Kaladim", "Kaladim"], _site.Ingested);
        Assert.Equal(0, svc.SkippedRefusedServer);
    }
}
