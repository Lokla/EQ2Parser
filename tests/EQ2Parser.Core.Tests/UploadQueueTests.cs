using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.Core.Tests;

public class LogPathsTests
{
    [Theory]
    [InlineData(@"C:\EQ2\logs\Varsoon\eq2log_Kayleigh.txt", "Varsoon")]
    [InlineData(@"C:\EQ2\logs\Antonia Bayle\eq2log_Bob.txt", "Antonia Bayle")]
    [InlineData("/home/u/eq2/logs/Varsoon/eq2log_X.txt", "Varsoon")]
    [InlineData(@"C:\EQ2\logs\eq2log_Kayleigh.txt", "")] // legacy layout — parent is "logs"
    [InlineData(@"C:\EQ2\LOGS\eq2log_K.txt", "")] //         …case-insensitively
    [InlineData("eq2log_K.txt", "")] //                      no directory at all
    [InlineData(@"C:\", "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Server_Is_The_Parent_Directory_Unless_Its_The_Logs_Root(string? path, string expected) =>
        Assert.Equal(expected, LogPaths.ParseServerName(path));
}

public class UploadUrlGuardTests
{
    [Theory]
    [InlineData("https://varsoon.eq2lexicon.com")]
    [InlineData("https://varsoon.eq2lexicon.com/")]
    [InlineData("http://localhost:8000")] // loopback dev exception
    [InlineData("http://127.0.0.1:8000")]
    public void Accepts_Https_And_Loopback_Http(string url) =>
        Assert.Null(LexiconUploadClient.UrlProblem(url));

    [Theory]
    [InlineData("http://varsoon.eq2lexicon.com")] // cleartext token — refused
    [InlineData("ftp://example.com")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Rejects_Cleartext_And_Garbage(string url) =>
        Assert.NotNull(LexiconUploadClient.UrlProblem(url));
}

public class UploadQueueTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static Encounter BuildEncounter()
    {
        var engine = new ParserEngine(@"C:\EQ2\logs\Varsoon\eq2log_Menludiir.txt", "Menludiir");
        engine.ChangeZone("Deathtoll");
        Assert.True(engine.SetEncounter(T0, "Menludiir", "Lord Bob"));
        engine.AddSwing(SwingCategory.Melee, true, "None", "Menludiir", "Strike", 1000, T0, "Lord Bob", "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", Combatant.KillingAbility, DamageValue.Death, T0.AddSeconds(10), "Lord Bob", "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static UploadResult Ok() => new(true, 200, "Uploaded (inserted).");

    [Fact]
    public async Task Uploads_A_Finished_Encounter_With_Its_Server()
    {
        LexiconPayload? sent = null;
        using var queue = new UploadQueue((p, _) =>
        {
            sent = p;
            return Task.FromResult(Ok());
        });

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "upload");

        Assert.Equal("Varsoon", sent!.LoggerServer);
        Assert.Equal("Menludiir", sent.LoggerName);
        Assert.Equal("Lord Bob", sent.Encounter.Title);
        Assert.Contains("1 uploaded this session", queue.Status);
    }

    [Fact]
    public async Task Transient_Failures_Retry_Then_Succeed()
    {
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            var n = Interlocked.Increment(ref attempts);
            return Task.FromResult(n < 3 ? new UploadResult(false, 503, "unavailable") : Ok());
        })
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "retried upload");
        Assert.Equal(3, attempts);
        Assert.Equal(0, queue.Failed);
    }

    [Fact]
    public async Task Permanent_Rejection_Fails_Without_Retry()
    {
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new UploadResult(false, 422, "Implausible parse."));
        })
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Failed == 1, "permanent failure");
        Assert.Equal(1, attempts);
        Assert.Equal(0, queue.Uploaded);
    }

    [Fact]
    public async Task Rejected_Token_Pauses_The_Queue_Until_Reset()
    {
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(new UploadResult(false, 401, "Invalid or expired token."));
        });

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.AuthPaused, "auth pause");
        Assert.Equal(1, attempts);

        // While paused, new work is dropped — the bad credential is never
        // re-sent for every trash fight of a play session.
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await Task.Delay(100);
        Assert.Equal(1, attempts);

        queue.ResetAuthPause();
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => attempts == 2, "resume after reset");
    }

    /// <summary>Scripted site: answers each ingest POST from a per-server
    /// table (status, reject code, detail), 201 for any server not listed.
    /// The queue under test is wired to a REAL client over this handler, so
    /// these tests cover the header → UploadResult → queue path end to end.</summary>
    private sealed class SiteHandler : HttpMessageHandler
    {
        private readonly List<string> _servers = [];

        public Dictionary<string, (HttpStatusCode Status, string? RejectCode, string Detail)> Refusals { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>logger_server of every request that reached the wire, in order.</summary>
        public List<string> Servers
        {
            get
            {
                lock (_servers)
                {
                    return [.. _servers];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var gz = new GZipStream(await request.Content!.ReadAsStreamAsync(ct), CompressionMode.Decompress);
            using var doc = await JsonDocument.ParseAsync(gz, cancellationToken: ct);
            var server = doc.RootElement.GetProperty("logger_server").GetString()!;
            lock (_servers)
            {
                _servers.Add(server);
            }
            if (!Refusals.TryGetValue(server, out var refusal))
                return Json(HttpStatusCode.Created, """{"status":"inserted","encounter_id":1}""");
            var response = Json(refusal.Status, JsonSerializer.Serialize(new { detail = refusal.Detail }));
            if (refusal.RejectCode is not null)
                response.Headers.Add(LexiconUploadClient.RejectHeader, refusal.RejectCode);
            return response;
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private const string Token = "eq2c_test_token";
    private const string NotAllowedDetail = "Server 'Kaladim' is not on the allowed list. Allowed: Varsoon, Wuoshi.";

    [Fact]
    public async Task Refused_Upload_Does_Not_Pause_And_The_Next_Upload_Is_Sent()
    {
        // The bug: an alt logging on a server the site does not accept got a
        // 403, the queue read it as "token rejected", and the main's uploads
        // stopped until the user re-saved a token that was never bad. A 403
        // carrying the reject header is about THAT upload only. The code is
        // deliberately one this client has never heard of — the rule is
        // "any non-empty value", never a list of known codes.
        var site = new SiteHandler { Refusals = { ["Kaladim"] = (HttpStatusCode.Forbidden, "some-future-code", NotAllowedDetail) } };
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync) { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero] };

        queue.Enqueue(BuildEncounter(), "Kaladim");
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "the upload behind the refused one");

        Assert.False(queue.AuthPaused);
        Assert.Equal(1, queue.Failed);
        Assert.Equal(["Kaladim", "Varsoon"], site.Servers); // refused once, never retried
        // An unknown code gets the generic treatment only: the server is
        // not remembered, so its next fight is sent and judged afresh.
        queue.Enqueue(BuildEncounter(), "Kaladim");
        await WaitFor(() => queue.Failed == 2, "the second refusal");
        Assert.Equal(0, queue.SkippedRefusedServer);
    }

    [Fact]
    public async Task Forbidden_Without_The_Reject_Header_Pauses_The_Queue()
    {
        // What an older site sends for every 403, and what a current one
        // sends for an account-level refusal: the cautious reading stands.
        var site = new SiteHandler { Refusals = { ["Varsoon"] = (HttpStatusCode.Forbidden, null, "Account not approved") } };
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.AuthPaused, "auth pause");
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await Task.Delay(100);

        Assert.Single(site.Servers);
        await WaitFor(() => queue.Status.Contains("paused"), "the pause status");
        Assert.DoesNotContain(Token, queue.Status);
    }

    [Fact]
    public async Task Unauthorized_Pauses_Even_When_The_Reject_Header_Is_Present()
    {
        // 401 is the credential, full stop — a header cannot talk the queue
        // out of pausing on it.
        var site = new SiteHandler { Refusals = { ["Varsoon"] = (HttpStatusCode.Unauthorized, LexiconUploadClient.RejectServerNotAllowed, "Invalid or revoked token") } };
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.AuthPaused, "auth pause");

        await WaitFor(() => queue.Status.Contains("paused"), "the pause status");
        Assert.DoesNotContain(Token, queue.Status);
    }

    [Fact]
    public async Task Refused_Upload_Status_Shows_The_Server_Detail_And_Never_The_Credential()
    {
        var site = new SiteHandler { Refusals = { ["Kaladim"] = (HttpStatusCode.Forbidden, LexiconUploadClient.RejectServerNotAllowed, NotAllowedDetail) } };
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);
        var statuses = new List<string>();
        queue.StatusChanged += () =>
        {
            lock (statuses)
            {
                statuses.Add(queue.Status);
            }
        };

        var refused = BuildEncounter();
        queue.Enqueue(refused, "Kaladim");
        // Waits are on the status itself: the counters move a moment
        // before the line that reports them is published.
        await WaitFor(() => queue.Status.Contains("refused"), "the refusal status");
        Assert.Equal($"Lord Bob upload refused: {NotAllowedDetail}", queue.Status);

        queue.Enqueue(BuildEncounter(), "Kaladim");
        await WaitFor(
            () =>
            {
                lock (statuses)
                {
                    return statuses.Count == 2;
                }
            },
            "the skip status");

        // Every line the user saw, refusal and skip alike: neither the
        // token nor the signature computed from it.
        var signature = LexiconUploadClient.Sign(PayloadBuilder.Build(refused, "Kaladim").ToJson(), Token);
        lock (statuses)
        {
            Assert.Contains("skipped", statuses[1]);
            Assert.All(statuses, s => Assert.DoesNotContain(Token, s));
            Assert.All(statuses, s => Assert.DoesNotContain(signature, s));
        }
    }

    [Fact]
    public async Task Server_Not_Allowed_Stops_Sending_For_That_Server_Only()
    {
        // One refused request per fight is cheap, but the site allows a
        // token 60 uploads a minute: a trash-clearing alt on a server it
        // does not accept must not spend the main's budget. After the site
        // says server-not-allowed once, later fights from that server are
        // skipped locally — counted and named — and everything else flows.
        var site = new SiteHandler { Refusals = { ["Kaladim"] = (HttpStatusCode.Forbidden, LexiconUploadClient.RejectServerNotAllowed, NotAllowedDetail) } };
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);

        // All queued before the first answer arrives (a replayed log does
        // this), in a different case each time (the site's check ignores it).
        queue.Enqueue(BuildEncounter(), "Kaladim");
        queue.Enqueue(BuildEncounter(), "kaladim");
        queue.Enqueue(BuildEncounter(), "KALADIM");
        await WaitFor(() => queue.Status.Contains("2 skipped"), "both skips");
        Assert.Equal(2, queue.SkippedRefusedServer);
        Assert.Contains("Lord Bob", queue.Status);
        Assert.Contains("KALADIM", queue.Status);

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "the allowed server's upload");

        Assert.Equal(["Kaladim", "Varsoon"], site.Servers);
        Assert.Equal(1, queue.Failed); // the one refusal; skips are not failures
        Assert.False(queue.AuthPaused);
    }

    [Fact]
    public async Task Forgetting_Refused_Servers_Sends_Again()
    {
        // Token or site URL changed (or the user retried by hand): the old
        // answer no longer speaks for the server, so ask again.
        var site = new SiteHandler { Refusals = { ["Kaladim"] = (HttpStatusCode.Forbidden, LexiconUploadClient.RejectServerNotAllowed, NotAllowedDetail) } };
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);

        queue.Enqueue(BuildEncounter(), "Kaladim");
        queue.Enqueue(BuildEncounter(), "Kaladim");
        await WaitFor(() => queue.SkippedRefusedServer == 1, "the skip");

        site.Refusals.Clear(); // the new site accepts it
        queue.ForgetRefusedServers();
        queue.Enqueue(BuildEncounter(), "Kaladim");
        await WaitFor(() => queue.Uploaded == 1, "upload after forgetting");

        Assert.Equal(["Kaladim", "Kaladim"], site.Servers);
        Assert.Equal(1, queue.SkippedRefusedServer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_Server_Is_Never_Sent(string server)
    {
        // A log outside any logs/<server>/ folder has no logger_server, and
        // the site answers 400 to an upload without one. The queue is the
        // backstop: whatever a caller does, that request never leaves.
        var site = new SiteHandler();
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);

        queue.Enqueue(BuildEncounter(), server);
        queue.Enqueue(BuildEncounter(), "Varsoon"); // FIFO: anything queued before it would be sent first
        await WaitFor(() => queue.Uploaded == 1, "the upload behind it");

        Assert.Equal(["Varsoon"], site.Servers);
        Assert.Equal(0, queue.Failed);
    }

    [Fact]
    public async Task Provenance_Warnings_Ride_Along_On_Auto_Uploads_Only()
    {
        var payloads = new List<LexiconPayload>();
        using var queue = new UploadQueue(
            (p, _) =>
            {
                lock (payloads)
                {
                    payloads.Add(p);
                }
                return Task.FromResult(Ok());
            },
            provenance: _ => [LogProvenance.WriterVerified]);

        queue.Enqueue(BuildEncounter(), "Varsoon"); //                     auto: probes
        queue.Enqueue(BuildEncounter(), "Varsoon", withProvenance: false); // manual: claims nothing
        await WaitFor(() => queue.Uploaded == 2, "both uploads");

        Assert.Equal([LogProvenance.WriterVerified], payloads[0].ClientWarnings);
        Assert.Null(payloads[1].ClientWarnings);
        // And the wire shape: omitted entirely when null, present when set.
        Assert.DoesNotContain("client_warnings", payloads[1].ToJson());
        Assert.Contains($"\"client_warnings\":[\"{LogProvenance.WriterVerified}\"]", payloads[0].ToJson());
    }

    [Fact]
    public async Task Probe_Failure_Never_Blocks_The_Upload()
    {
        using var queue = new UploadQueue(
            (_, _) => Task.FromResult(Ok()),
            provenance: _ => throw new InvalidOperationException("probe exploded"));

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "upload despite probe failure");
    }

    [Fact]
    public async Task Sender_Exception_Is_A_Failure_Not_A_Crash()
    {
        // The reconfigure race: the client can be disposed mid-send. The
        // drain must survive and count it, not die silently.
        using var queue = new UploadQueue((_, _) =>
            throw new ObjectDisposedException(nameof(LexiconUploadClient)))
        { RetryDelays = [] };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Failed == 1, "surfaced failure");
    }

    [Fact]
    public async Task Unmarked_Forbidden_Pause_Says_What_The_Site_Said()
    {
        // The one account-level 403 the site sends is "Account not
        // approved". "token rejected" sent that user off to re-mint a token
        // that was never the problem; the server's own words name it.
        var site = new SiteHandler { Refusals = { ["Varsoon"] = (HttpStatusCode.Forbidden, null, "Account not approved") } };
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);

        var refused = BuildEncounter();
        queue.Enqueue(refused, "Varsoon");
        await WaitFor(() => queue.Status.Contains("paused"), "the pause status");

        Assert.True(queue.AuthPaused);
        Assert.Equal("uploads paused — refused by the site (403): Account not approved. Fix that, then hit Test.", queue.Status);
        Assert.DoesNotContain(Token, queue.Status);
        Assert.DoesNotContain(LexiconUploadClient.Sign(PayloadBuilder.Build(refused, "Varsoon").ToJson(), Token), queue.Status);
    }

    [Theory]
    [InlineData("Not allowed here.", "uploads paused — refused by the site (403): Not allowed here. Fix that, then hit Test.")] // no doubled full stop
    [InlineData("", "uploads paused — refused by the site (403). Check the token and its account, then hit Test.")]
    [InlineData("   ", "uploads paused — refused by the site (403). Check the token and its account, then hit Test.")]
    public async Task Unmarked_Forbidden_Pause_Reads_Cleanly_Whatever_The_Detail(string detail, string expected)
    {
        using var queue = new UploadQueue((_, _) => Task.FromResult(new UploadResult(false, 403, detail)));

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Status.Contains("paused"), "the pause status");

        Assert.Equal(expected, queue.Status);
    }

    [Fact]
    public async Task Unauthorized_Pause_Keeps_Its_Wording()
    {
        // 401 IS the token (or the signature made from it): the advice to
        // update it stands, word for word.
        var site = new SiteHandler { Refusals = { ["Varsoon"] = (HttpStatusCode.Unauthorized, null, "Invalid or revoked token") } };
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Status.Contains("paused"), "the pause status");

        Assert.Equal("uploads paused — token rejected (401). Update it and hit Test.", queue.Status);
    }

    private static UploadResult RateLimited() => new(false, 429, "Server responded 429");

    [Fact]
    public async Task Rate_Limited_Upload_Waits_And_Retries_Instead_Of_Being_Dropped()
    {
        // The site allows a token 60 uploads a minute. Replaying a long log
        // goes past that, and 429 was treated like a 422: every fight over
        // the limit was dropped on its first answer.
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            var n = Interlocked.Increment(ref attempts);
            return Task.FromResult(n < 3 ? RateLimited() : Ok());
        })
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], RateLimitDelay = TimeSpan.Zero };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "the upload after the limit cleared");

        Assert.Equal(3, attempts);
        Assert.Equal(0, queue.Failed);
        Assert.False(queue.AuthPaused);
    }

    [Fact]
    public async Task Rate_Limit_Retries_Stay_Inside_The_Attempt_Budget()
    {
        // Same budget as any transient failure (RetryDelays.Count + 1
        // attempts) — a site that keeps answering 429 fails the fight
        // rather than holding the drain for ever.
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(RateLimited());
        })
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], RateLimitDelay = TimeSpan.Zero };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Failed == 1, "the failure once the budget is spent");

        Assert.Equal(3, attempts);
        Assert.Equal(0, queue.Uploaded);
        await WaitFor(() => queue.Status.Contains("upload failed"), "the failure status");
    }

    [Fact]
    public async Task Rate_Limit_And_Transient_Failures_Share_One_Budget()
    {
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            var n = Interlocked.Increment(ref attempts);
            return Task.FromResult(n == 1 ? RateLimited() : new UploadResult(false, 503, "unavailable"));
        })
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], RateLimitDelay = TimeSpan.Zero };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Failed == 1, "the failure once the budget is spent");

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Fights_Queued_Behind_A_Rate_Limited_One_Are_Still_Sent()
    {
        var attempts = 0;
        using var queue = new UploadQueue((_, _) =>
        {
            var n = Interlocked.Increment(ref attempts);
            return Task.FromResult(n == 1 ? RateLimited() : Ok());
        })
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], RateLimitDelay = TimeSpan.Zero };

        queue.Enqueue(BuildEncounter(), "Varsoon");
        queue.Enqueue(BuildEncounter(), "Varsoon");
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 3, "every queued fight");

        Assert.Equal(4, attempts);
        Assert.Equal(0, queue.Failed);
    }

    [Fact]
    public async Task Rate_Limit_Wait_Says_What_It_Is_Doing_And_Never_Blocks_Shutdown()
    {
        // The wait is long by design (the limit is per minute), so the
        // status has to say the queue is waiting — and how long — or it
        // looks hung. Dispose must cut it short, not sit it out.
        var queue = new UploadQueue((_, _) => Task.FromResult(RateLimited()))
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], RateLimitDelay = TimeSpan.FromMinutes(10) };
        try
        {
            queue.Enqueue(BuildEncounter(), "Varsoon");
            await WaitFor(() => queue.Status.Contains("waiting"), "the waiting status");

            Assert.Equal("Lord Bob upload waiting 600 s — the site's upload rate limit was reached (attempt 1 of 3 used).", queue.Status);
            Assert.Equal(0, queue.Failed);
        }
        finally
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            queue.Dispose();
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"Dispose took {clock.Elapsed}");
        }
    }
}
