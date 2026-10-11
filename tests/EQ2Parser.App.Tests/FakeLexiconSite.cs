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
/// Configure fires) gets an empty 200 and is counted. One upload can be
/// held unanswered (<see cref="HoldNextUpload"/>) so a test can act while a
/// send is in flight. Every service test goes through
/// <c>UploadService.HandlerFactory</c>: a service built without it would
/// send its whoami probe to the real network stack.</summary>
internal sealed class FakeLexiconSite
{
    private readonly List<string> _ingested = [];
    private readonly List<string> _bearers = [];
    private int _otherRequests;
    private HeldRequest? _hold;

    /// <summary>One parse upload the site keeps waiting: it has arrived
    /// (<see cref="Arrived"/> completes) but gets no answer until
    /// <see cref="Answer"/> is called. Lets a test change the service's
    /// configuration while a send is in flight, deterministically.</summary>
    public sealed class HeldRequest
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<(HttpStatusCode Status, string? RejectCode, string Detail)?> _answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the held upload has reached the site.</summary>
        public Task Arrived => _arrived.Task;

        /// <summary>Lets the held upload go with this refusal (same shape
        /// as a <see cref="Refusals"/> entry).</summary>
        public void Answer(HttpStatusCode status, string? rejectCode, string detail) =>
            _answer.TrySetResult((status, rejectCode, detail));

        /// <summary>Lets the held upload go with a 201.</summary>
        public void Accept() => _answer.TrySetResult(null);

        internal Task<(HttpStatusCode Status, string? RejectCode, string Detail)?> WaitAsync(CancellationToken ct)
        {
            _arrived.TrySetResult();
            return _answer.Task.WaitAsync(ct);
        }
    }

    /// <summary>The NEXT parse upload to arrive is held until the returned
    /// handle answers it; uploads after it are answered as usual.</summary>
    public HeldRequest HoldNextUpload()
    {
        var hold = new HeldRequest();
        _hold = hold;
        return hold;
    }

    /// <summary>The bearer value of every parse upload sent, in order — to
    /// tell which configuration a request went out under. Test tokens only.</summary>
    public List<string> Bearers
    {
        get
        {
            lock (_ingested)
            {
                return [.. _bearers];
            }
        }
    }

    /// <summary>Requests that reached this site on any other route (the
    /// whoami probe every <c>Configure</c> fires). Above zero proves a
    /// service's traffic is going HERE and not to the real network stack.</summary>
    public int OtherRequests => Volatile.Read(ref _otherRequests);

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
            {
                Interlocked.Increment(ref site._otherRequests);
                return Json(HttpStatusCode.OK, "{}");
            }
            using var gz = new GZipStream(await request.Content!.ReadAsStreamAsync(ct), CompressionMode.Decompress);
            using var doc = await JsonDocument.ParseAsync(gz, cancellationToken: ct);
            var server = doc.RootElement.GetProperty("logger_server").GetString()!;
            lock (site._ingested)
            {
                site._ingested.Add(server);
                site._bearers.Add(request.Headers.Authorization?.Parameter ?? "");
            }
            (HttpStatusCode Status, string? RejectCode, string Detail) refusal;
            if (Interlocked.Exchange(ref site._hold, null) is { } hold)
            {
                if (await hold.WaitAsync(ct) is not { } held)
                    return Json(HttpStatusCode.Created, """{"status":"inserted","encounter_id":1}""");
                refusal = held;
            }
            else if (!site.Refusals.TryGetValue(server, out refusal))
            {
                return Json(HttpStatusCode.Created, """{"status":"inserted","encounter_id":1}""");
            }
            var response = Json(refusal.Status, JsonSerializer.Serialize(new { detail = refusal.Detail }));
            if (refusal.RejectCode is not null)
                response.Headers.Add(LexiconUploadClient.RejectHeader, refusal.RejectCode);
            return response;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
