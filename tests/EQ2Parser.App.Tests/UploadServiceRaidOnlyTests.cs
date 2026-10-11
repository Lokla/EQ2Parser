using System.Net;
using System.Net.Http;
using System.Text;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;

namespace EQ2Parser.App.Tests;

/// <summary>The auto-upload gate with the raid-only rule: off = everything
/// (fleet model unchanged), on = boss list OR headcount, skips are counted
/// and named in the status line, and the manual path is not consulted.</summary>
public sealed class UploadServiceRaidOnlyTests : IDisposable
{
    private readonly TestDataDir _dir = new();

    // Every client a service here builds talks to this in-process site.
    // Without it Configure's whoami probe went out through a real
    // HttpClient: a background DNS lookup of a reserved host name per test.
    private readonly FakeLexiconSite _site = new();
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    public UploadServiceRaidOnlyTests()
    {
        // The status line goes through Loc; without the dictionaries it
        // would read as the bare key and the assertion on it would be moot.
        EQ2Parser.App.Localization.Loc.Initialize("en");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    private static Encounter Fight(string title)
    {
        var engine = new ParserEngine(@"C:\EQ2\logs\Varsoon\eq2log_Menludiir.txt", "Menludiir");
        engine.ChangeZone("Veeshan's Peak");
        Assert.True(engine.SetEncounter(T0, "Menludiir", title));
        engine.AddSwing(SwingCategory.Melee, true, "None", "Menludiir", "Strike", 1000, T0, title, "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", Combatant.KillingAbility, DamageValue.Death, T0.AddSeconds(10), title, "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    private sealed class CannedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"version":"v1","bosses":["trakanon"]}""", Encoding.UTF8, "application/json"),
            });
    }

    private UploadService Configured(bool raidOnly, int players, bool withBosses = true)
    {
        var svc = new UploadService
        {
            HandlerFactory = _site.NewHandler,
            PlayerCounter = _ => players,
            RaidBosses = withBosses ? new RaidBossSyncService("https://example.test", new HttpClient(new CannedHandler())) : null,
        };
        // A token + https URL make the service Active; no fight is sent
        // because these tests only consult the gate, never Enqueue.
        svc.Configure("https://example.test", "token", enabled: true, raidOnly: raidOnly);
        return svc;
    }

    [Fact]
    public async Task The_Service_Under_Test_Talks_To_The_Fake_Site_Only()
    {
        // Configure fires a whoami probe at once. It arriving HERE is the
        // proof the client was built over the fake's handler; a client
        // built without it would have sent the probe to the network.
        using var svc = Configured(raidOnly: true, players: 3);
        await FakeLexiconSite.WaitFor(() => _site.OtherRequests >= 1, "the whoami probe reaching the fake site");
        Assert.Empty(_site.Ingested); // and the gate tests upload nothing
    }

    [Fact]
    public void Raid_Only_Off_Uploads_Everything()
    {
        using var svc = Configured(raidOnly: false, players: 1);
        Assert.True(svc.ShouldAutoUpload(Fight("a krait warrior")));
        Assert.True(svc.ShouldAutoUpload(Fight("Lord Bob")));
        Assert.Equal(0, svc.SkippedNonRaid);
    }

    [Fact]
    public async Task Raid_Only_Passes_Curated_Bosses_And_Raid_Sized_Fights()
    {
        using var svc = Configured(raidOnly: true, players: 3);
        await svc.RaidBosses!.EnsureFreshAsync(T0);  // the Configure kick is fire-and-forget; await a fetch here

        Assert.True(svc.ShouldAutoUpload(Fight("Trakanon")));      // on the synced list, 3 players
        Assert.False(svc.ShouldAutoUpload(Fight("a krait warrior")));
        Assert.False(svc.ShouldAutoUpload(Fight("Lord Bob")));     // named but uncurated, group-sized
        Assert.Equal(2, svc.SkippedNonRaid);
        Assert.Contains("Lord Bob", svc.Status);
        Assert.Contains("3", svc.Status);
    }

    [Fact]
    public void Raid_Only_Without_A_List_Falls_Back_To_Headcount()
    {
        using var big = Configured(raidOnly: true, players: 7, withBosses: false);
        Assert.True(big.ShouldAutoUpload(Fight("a krait warrior")));

        using var small = Configured(raidOnly: true, players: 6, withBosses: false);
        Assert.False(small.ShouldAutoUpload(Fight("Trakanon")));
    }

    [Fact]
    public void Disabled_Uploads_Never_Pass_Regardless_Of_Rule()
    {
        using var svc = new UploadService { HandlerFactory = _site.NewHandler, PlayerCounter = _ => 24 };
        svc.Configure("https://example.test", "token", enabled: false, raidOnly: true);
        Assert.False(svc.ShouldAutoUpload(Fight("Trakanon")));
        Assert.Equal(0, svc.SkippedNonRaid);
    }
}
