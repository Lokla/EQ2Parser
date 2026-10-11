using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.App.Tests;

/// <summary>A scripted EQ2Lexicon for <see cref="UploadService"/> tests:
/// nothing leaves the process. Parse ingest answers from a per-server table
/// (201 for a server not listed) and records the logger_server of every
/// upload that reached the wire; every other route (the whoami probe
/// Configure fires) gets an empty 200.</summary>
internal sealed class FakeLexiconSite
{
    private readonly List<string> _ingested = [];

    /// <summary>logger_server → the refusal to answer with. A null reject
    /// code sends no reject header (an older site, or an account-level
    /// refusal).</summary>
    public Dictionary<string, (HttpStatusCode Status, string? RejectCode, string Detail)> Refusals { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>logger_server of every parse upload sent, in order.</summary>
    public List<string> Ingested
    {
        get
        {
            lock (_ingested)
            {
                return [.. _ingested];
            }
        }
    }

    /// <summary>For <c>UploadService.HandlerFactory</c>: a fresh handler per
    /// client (a client disposes its handler), all sharing this site.</summary>
    public HttpMessageHandler NewHandler() => new Handler(this);

    /// <summary>An upload service wired to this site: token saved, https
    /// URL, auto-upload on.</summary>
    public UploadService NewService(bool raidOnly = false, int players = 24)
    {
        var svc = new UploadService { HandlerFactory = NewHandler, PlayerCounter = _ => players };
        svc.Configure("https://example.test", "token", enabled: true, raidOnly: raidOnly);
        return svc;
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    /// <summary>A finished one-kill fight read from <paramref name="logPath"/>
    /// — the path is what decides its logger_server.</summary>
    public static Encounter Fight(string logPath, string title = "Lord Bob")
    {
        var engine = new ParserEngine(logPath, "Tester");
        engine.ChangeZone("Deathtoll");
        Assert.True(engine.SetEncounter(T0, "Tester", title));
        engine.AddSwing(SwingCategory.Melee, true, "None", "Tester", "Strike", 1000, T0, title, "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Tester", Combatant.KillingAbility, DamageValue.Death, T0.AddSeconds(10), title, "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    public static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private sealed class Handler(FakeLexiconSite site) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/api/parses/ingest")
                return Json(HttpStatusCode.OK, "{}");
            using var gz = new GZipStream(await request.Content!.ReadAsStreamAsync(ct), CompressionMode.Decompress);
            using var doc = await JsonDocument.ParseAsync(gz, cancellationToken: ct);
            var server = doc.RootElement.GetProperty("logger_server").GetString()!;
            lock (site._ingested)
            {
                site._ingested.Add(server);
            }
            if (!site.Refusals.TryGetValue(server, out var refusal))
                return Json(HttpStatusCode.Created, """{"status":"inserted","encounter_id":1}""");
            var response = Json(refusal.Status, JsonSerializer.Serialize(new { detail = refusal.Detail }));
            if (refusal.RejectCode is not null)
                response.Headers.Add(LexiconUploadClient.RejectHeader, refusal.RejectCode);
            return response;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
