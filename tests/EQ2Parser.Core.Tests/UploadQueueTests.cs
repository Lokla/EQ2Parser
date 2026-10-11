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

    [Fact]
    public void Server_Name_Pattern_Matches_The_Server_Constant()
    {
        // Pinned like the reject header: the string is the contract. It
        // must equal WORLD_NAME_RE in EQ2Lexicon's
        // backend/server/core/validation.py, character for character — a
        // looser copy sends requests the site answers 400 to, a tighter
        // one refuses a server the site would have taken.
        Assert.Equal(@"^[A-Za-z][A-Za-z0-9 '_-]{0,30}$", LogPaths.ServerNamePattern);
    }

    [Theory]
    [InlineData("Varsoon")]
    [InlineData("wuoshi")] //                           either case
    [InlineData("A")] //                                one letter is enough
    [InlineData("Antonia Bayle")] //                    a space inside
    [InlineData("Kael'Drakkel")] //                     an apostrophe
    [InlineData("Test_Server-2")] //                    underscore, hyphen, a digit after the first letter
    [InlineData("  Varsoon ")] //                       the site strips surrounding whitespace before it looks
    [InlineData("Abcdefghijklmnopqrstuvwxyz01234")] //  31 characters: the letter plus 30
    public void Server_Names_The_Site_Would_Accept(string name) =>
        Assert.True(LogPaths.IsServerNameAccepted(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2ndAccount")] //                        starts with a digit
    [InlineData("_Varsoon")] //                          … or anything else that is not a letter
    [InlineData("-Varsoon")]
    [InlineData("'Varsoon")]
    [InlineData("Varsoon!")] //                          a character outside the set
    [InlineData("Var.soon")]
    [InlineData("Varsoon (old)")]
    [InlineData("Var\nsoon")]
    [InlineData("Var\tsoon")] //                         the set has the space, not every whitespace
    [InlineData("Vársoon")] //                           ASCII letters only
    [InlineData("Варсун")]
    [InlineData("Abcdefghijklmnopqrstuvwxyz012345")] // 32 characters: one too many
    public void Server_Names_The_Site_Would_Call_Malformed(string? name) =>
        Assert.False(LogPaths.IsServerNameAccepted(name));
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

    /// <summary>A success as the client reports one: the site's status word
    /// rides beside the client's own sentence.</summary>
    private static UploadResult Ok() => new(true, 200, "Uploaded (inserted).") { ServerStatus = "inserted" };

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
        await WaitFor(() => queue.Status.Contains("1 uploaded this session"), "the uploaded status");
        // Word for word the line this always was.
        Assert.Equal("Lord Bob — Uploaded (inserted). 1 uploaded this session.", queue.Status);
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

    // ── Retry-After ─────────────────────────────────────────────────────────

    public static TheoryData<double?, double> RetryAfterWaits => new()
    {
        { null, 30 }, //    no header: the fixed wait
        { -5, 30 }, //      unusable: the fixed wait
        { 0, 1 }, //        "retry now" still waits the minimum
        { 0.2, 1 },
        { 1, 1 },
        { 7, 7 }, //        honoured as sent
        { 59.5, 59.5 },
        { 120, 120 },
        { 121, 120 }, //    held to the maximum
        { 3600, 120 },
    };

    [Theory]
    [MemberData(nameof(RetryAfterWaits))]
    public void Rate_Limit_Wait_Is_The_Sites_Retry_After_Held_To_A_Sensible_Range(double? retryAfterSeconds, double expectedSeconds)
    {
        TimeSpan? retryAfter = retryAfterSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), UploadQueue.RateLimitWait(retryAfter, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void The_Fixed_Wait_Is_Not_Clamped()
    {
        // The range is for what a server asks. The fallback is ours: tests
        // set it to zero and a host may set it past the maximum.
        Assert.Equal(TimeSpan.Zero, UploadQueue.RateLimitWait(null, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(10), UploadQueue.RateLimitWait(null, TimeSpan.FromMinutes(10)));
    }

    [Theory]
    [InlineData(7, "Lord Bob upload waiting 7 s — the site's upload rate limit was reached (attempt 1 of 3 used).")]
    [InlineData(3600, "Lord Bob upload waiting 120 s — the site's upload rate limit was reached (attempt 1 of 3 used).")]
    [InlineData(0, "Lord Bob upload waiting 1 s — the site's upload rate limit was reached (attempt 1 of 3 used).")]
    public async Task Rate_Limited_Upload_Waits_What_The_Site_Asked_And_Shutdown_Still_Cuts_It_Short(int retryAfterSeconds, string expected)
    {
        // The fixed wait is ten minutes here, so a status that names any
        // other length can only have come from the header.
        var attempts = 0;
        var queue = new UploadQueue(async (_, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return RateLimited() with { RetryAfter = TimeSpan.FromSeconds(retryAfterSeconds) };
            // Only the first answer is a 429. Whatever follows it stays in
            // flight until shutdown, so the queue is still inside this
            // fight when Dispose arrives, however short the wait was.
            await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        })
        { RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], RateLimitDelay = TimeSpan.FromMinutes(10) };
        try
        {
            queue.Enqueue(BuildEncounter(), "Varsoon");
            await WaitFor(() => queue.Status.Contains("waiting"), "the waiting status");

            Assert.Equal(expected, queue.Status);
            Assert.Equal(0, queue.Failed);
        }
        finally
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            queue.Dispose();
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1.5), $"Dispose took {clock.Elapsed}");
        }
    }

    /// <summary>Answers the first ingest POST 429 — with a Retry-After when
    /// given one — and every later one 201.</summary>
    private sealed class RateLimitedOnceHandler(string? retryAfter, string body) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _requests) > 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{"status":"inserted","encounter_id":1}""", Encoding.UTF8, "application/json"),
                });
            }
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (retryAfter is not null)
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task A_Retry_After_Header_Shortens_The_Wait_End_To_End()
    {
        // Header → client → UploadResult → queue, through a real client.
        // The body is the site's flood-budget 429 (the one that carries the
        // header). With the ten-minute fixed wait this could not finish
        // inside the test's five seconds; with "Retry-After: 1" it does.
        var site = new RateLimitedOnceHandler("1", """{"detail":"Over the upload budget. Retry after 1 s."}""");
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync) { RateLimitDelay = TimeSpan.FromMinutes(10) };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "the upload after the wait the site asked for");

        Assert.Equal(2, site.Requests);
        Assert.Equal(0, queue.Failed);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.9), $"re-sent after only {clock.Elapsed}");
    }

    [Theory]
    [InlineData(null)] //    the site's own 60-a-minute limit sends none
    [InlineData("soon")] //  not a number, not a date
    [InlineData("-3")]
    [InlineData("Wed, 21 Oct 2015 07:28:00 GMT")] // a date long past: a skewed clock, not "retry now"
    public async Task Without_A_Usable_Retry_After_The_Fixed_Wait_Applies(string? retryAfter)
    {
        // The body is slowapi's stock 429: an "error" key and no "detail".
        var site = new RateLimitedOnceHandler(retryAfter, """{"error":"Rate limit exceeded: 60 per 1 minute"}""");
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        var queue = new UploadQueue(client.UploadAsync) { RateLimitDelay = TimeSpan.FromMinutes(10) };
        try
        {
            queue.Enqueue(BuildEncounter(), "Varsoon");
            await WaitFor(() => queue.Status.Contains("waiting"), "the waiting status");

            Assert.Equal("Lord Bob upload waiting 600 s — the site's upload rate limit was reached (attempt 1 of 3 used).", queue.Status);
        }
        finally
        {
            queue.Dispose();
        }
    }

    // ── No client ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_Send_With_No_Client_Drops_The_Fight_At_Once_And_Never_Retries()
    {
        // The token was cleared with fights still queued. The send used to
        // report that as status 0, which reads as a network error: each
        // queued fight then sat through the 5 s and 30 s retry waits. The
        // DEFAULT waits are left in place here — three fights at 35 s each
        // could never finish inside this test's five seconds.
        var attempts = 0;
        var configured = false;
        using var queue = new UploadQueue((_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult(Volatile.Read(ref configured) ? Ok() : UploadResult.NoClient("No API token configured."));
        });

        queue.Enqueue(BuildEncounter(), "Varsoon");
        queue.Enqueue(BuildEncounter(), "Varsoon");
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Status.Contains("3 dropped"), "all three dropped");

        Assert.Equal(3, attempts); // asked once each, never again
        Assert.Equal(3, queue.SkippedNotConfigured);
        Assert.Equal(0, queue.Failed); // nothing was sent, so nothing failed
        Assert.Equal(0, queue.Uploaded);
        Assert.False(queue.AuthPaused);
        Assert.Equal("Lord Bob not uploaded — No API token configured (3 dropped this session)", queue.Status);

        // Dropped, not held: a token saved later sends what is queued from
        // then on, and none of the three that were waiting.
        Volatile.Write(ref configured, true);
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 1, "the next fight once configured");
        Assert.Equal(4, attempts);
    }

    // ── A changed configuration ─────────────────────────────────────────────

    /// <summary>A transport whose FIRST send does not answer until the test
    /// lets it, so the configuration can be changed while it is in flight.
    /// Every later send answers <see cref="Ok"/> at once.</summary>
    private sealed class HeldFirstSend
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<UploadResult> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sends;

        public int Sends => Volatile.Read(ref _sends);

        /// <summary>Completes once the first send is in flight.</summary>
        public Task Entered => _entered.Task;

        public void Answer(UploadResult result) => _answer.SetResult(result);

        public Task<UploadResult> SendAsync(LexiconPayload payload, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _sends) > 1)
                return Task.FromResult(Ok());
            _entered.SetResult();
            return _answer.Task;
        }
    }

    [Theory]
    [InlineData(401, "")]
    [InlineData(403, "")] // unmarked: the account-level refusal
    public async Task A_Rejection_From_Before_A_Reconfigure_Does_Not_Pause_The_New_Configuration(int status, string rejectCode)
    {
        // The send went out with the old token; the user saved a new one
        // while it was in flight. The old token's rejection used to land
        // AFTER the reconfigure had cleared the pause, and pause the queue
        // again — against a token the site had not judged.
        var transport = new HeldFirstSend();
        using var queue = new UploadQueue(transport.SendAsync);

        queue.Enqueue(BuildEncounter(), "Varsoon");
        await transport.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        queue.ConfigurationChanged();
        transport.Answer(new UploadResult(false, status, "Invalid or revoked token", RejectCode: rejectCode));

        // Not paused, not failed: sent again, under the configuration that
        // has not judged it yet, and accepted.
        await WaitFor(() => queue.Uploaded == 1, "the re-send under the new configuration");
        Assert.False(queue.AuthPaused);
        Assert.Equal(0, queue.Failed);
        Assert.Equal(2, transport.Sends);

        // And the queue is open for what comes next.
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 2, "the next fight");
    }

    [Fact]
    public async Task A_Server_Refusal_From_Before_A_Reconfigure_Is_Not_Remembered()
    {
        // "server-not-allowed" is one site's answer for one account. Landing
        // after the reconfigure, it must not make the queue skip that
        // server's fights under the new site.
        var transport = new HeldFirstSend();
        using var queue = new UploadQueue(transport.SendAsync);

        queue.Enqueue(BuildEncounter(), "Kaladim");
        await transport.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        queue.ConfigurationChanged();
        transport.Answer(new UploadResult(false, 403, NotAllowedDetail, RejectCode: LexiconUploadClient.RejectServerNotAllowed));
        await WaitFor(() => queue.Uploaded == 1, "the re-send under the new configuration");

        queue.Enqueue(BuildEncounter(), "Kaladim");
        await WaitFor(() => queue.Uploaded == 2, "the same server's next fight");
        Assert.Equal(0, queue.SkippedRefusedServer);
        Assert.Equal(0, queue.Failed);
        Assert.Equal(3, transport.Sends);
    }

    [Fact]
    public async Task A_Rejection_Under_The_Current_Configuration_Still_Pauses()
    {
        // The control for the two tests above: the reconfigure came BEFORE
        // the send, so the 401 is the current token's and the pause stands.
        var transport = new HeldFirstSend();
        using var queue = new UploadQueue(transport.SendAsync);

        queue.ConfigurationChanged();
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await transport.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        transport.Answer(new UploadResult(false, 401, "Invalid or revoked token"));

        await WaitFor(() => queue.AuthPaused, "the pause");
        await WaitFor(() => queue.Failed == 1, "the failure");
        Assert.Equal(1, transport.Sends);
        Assert.Equal(0, queue.Uploaded);
    }

    [Fact]
    public async Task A_Reconfigure_Clears_The_Pause_And_The_Refused_Servers()
    {
        var site = new SiteHandler
        {
            Refusals =
            {
                ["Kaladim"] = (HttpStatusCode.Forbidden, LexiconUploadClient.RejectServerNotAllowed, NotAllowedDetail),
                ["Varsoon"] = (HttpStatusCode.Unauthorized, null, "Invalid or revoked token"),
            },
        };
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);
        queue.Enqueue(BuildEncounter(), "Kaladim");
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.AuthPaused, "the pause");

        site.Refusals.Clear(); // the new site accepts both
        queue.ConfigurationChanged();
        Assert.False(queue.AuthPaused);
        queue.Enqueue(BuildEncounter(), "Kaladim");
        queue.Enqueue(BuildEncounter(), "Varsoon");
        await WaitFor(() => queue.Uploaded == 2, "both uploads after the reconfigure");

        Assert.Equal(["Kaladim", "Varsoon", "Kaladim", "Varsoon"], site.Servers);
        Assert.Equal(0, queue.SkippedRefusedServer);
    }

    // ── A server name the site would call malformed ─────────────────────────

    [Theory]
    [InlineData("2ndAccount")]
    [InlineData("Varsoon (old)")]
    [InlineData("Abcdefghijklmnopqrstuvwxyz012345")] // 32 characters
    public async Task A_Server_Name_The_Site_Would_Call_Malformed_Is_Never_Sent(string server)
    {
        // The site answers 400 "logger_server '…' is malformed." to a name
        // outside its shape — every fight from that log folder, for ever.
        // The queue is the backstop, as it is for an empty server.
        var site = new SiteHandler();
        using var client = new LexiconUploadClient("https://parses.example.com", Token, site);
        using var queue = new UploadQueue(client.UploadAsync);

        queue.Enqueue(BuildEncounter(), server);
        queue.Enqueue(BuildEncounter(), "Antonia Bayle"); // FIFO: anything queued before it would be sent first
        await WaitFor(() => queue.Uploaded == 1, "the upload behind it");

        Assert.Equal(["Antonia Bayle"], site.Servers);
        Assert.Equal(0, queue.Failed);
    }

    // ── Status lines ────────────────────────────────────────────────────────

    /// <summary>Stands in for the app's translated lines: every line is its
    /// method's name and arguments, so a test can see which one was used.</summary>
    private sealed class TaggedMessages : UploadQueueMessages
    {
        public override string Uploaded(string title, string serverStatus, int uploadedThisSession) =>
            $"[uploaded|{title}|{serverStatus}|{uploadedThisSession}]";

        public override string Retrying(string title, string reason) => $"[retrying|{title}|{reason}]";

        public override string Failed(string title, string reason) => $"[failed|{title}|{reason}]";

        public override string Refused(string title, string detail) => $"[refused|{title}|{detail}]";

        public override string PausedTokenRejected(int statusCode) => $"[paused-token|{statusCode}]";

        public override string PausedRefused(int statusCode) => $"[paused-refused|{statusCode}]";

        public override string PausedRefusedWithDetail(int statusCode, string detail) => $"[paused-refused|{statusCode}|{detail}]";

        public override string SkippedRefusedServer(string title, string server, int skippedThisSession) =>
            $"[skipped|{title}|{server}|{skippedThisSession}]";

        public override string RateLimitWait(string title, int seconds, int attemptsUsed, int attemptsAllowed) =>
            $"[waiting|{title}|{seconds}|{attemptsUsed}|{attemptsAllowed}]";

        public override string BuildFailed(string title, string problem) => $"[build-failed|{title}|{problem}]";

        public override string NotConfigured(string title, string reason, int droppedThisSession) =>
            $"[not-configured|{title}|{reason}|{droppedThisSession}]";
    }

    /// <summary>Runs one fight through a queue that answers from
    /// <paramref name="answers"/> in order (the last one repeats), with the
    /// tagged messages, and returns every status line it published.</summary>
    private static async Task<List<string>> StatusesFor(string server, int enqueue, params UploadResult[] answers)
    {
        var sends = 0;
        var statuses = new List<string>();
        var queue = new UploadQueue((_, _) =>
        {
            var n = Interlocked.Increment(ref sends);
            return Task.FromResult(answers[Math.Min(n, answers.Length) - 1]);
        })
        { RetryDelays = [TimeSpan.Zero], RateLimitDelay = TimeSpan.Zero, Messages = new TaggedMessages() };
        using (queue)
        {
            queue.StatusChanged += () =>
            {
                lock (statuses)
                {
                    statuses.Add(queue.Status);
                }
            };
            for (var i = 0; i < enqueue; i++)
                queue.Enqueue(BuildEncounter(), server);
            // Each fight ends in exactly one terminal line; wait for the last.
            await WaitFor(
                () =>
                {
                    lock (statuses)
                    {
                        return statuses.Count(s => !s.StartsWith("[retrying", StringComparison.Ordinal) && !s.StartsWith("[waiting", StringComparison.Ordinal)) == enqueue;
                    }
                },
                "a terminal status per fight");
        }
        lock (statuses)
        {
            return [.. statuses];
        }
    }

    [Fact]
    public async Task Every_Status_Line_Comes_From_The_Injected_Messages()
    {
        // The queue has no wording of its own left: each line it shows is
        // one call on Messages, with the facts as arguments and the site's
        // text passed through untouched.
        Assert.Equal(["[uploaded|Lord Bob|inserted|1]"], await StatusesFor("Varsoon", 1, Ok()));
        Assert.Equal(
            ["[retrying|Lord Bob|unavailable]", "[failed|Lord Bob|unavailable]"],
            await StatusesFor("Varsoon", 1, new UploadResult(false, 503, "unavailable")));
        Assert.Equal(["[failed|Lord Bob|Implausible parse.]"], await StatusesFor("Varsoon", 1, new UploadResult(false, 422, "Implausible parse.")));
        Assert.Equal(
            ["[waiting|Lord Bob|0|1|2]", "[uploaded|Lord Bob|inserted|1]"],
            await StatusesFor("Varsoon", 1, RateLimited(), Ok()));
        Assert.Equal(["[paused-token|401]"], await StatusesFor("Varsoon", 1, new UploadResult(false, 401, "Invalid or revoked token")));
        Assert.Equal(["[paused-refused|403]"], await StatusesFor("Varsoon", 1, new UploadResult(false, 403, " ")));
        Assert.Equal(
            ["[paused-refused|403|Account not approved]"],
            await StatusesFor("Varsoon", 1, new UploadResult(false, 403, "Account not approved.")));
        Assert.Equal(
            ["[refused|Lord Bob|" + NotAllowedDetail + "]", "[skipped|Lord Bob|Kaladim|1]"],
            await StatusesFor("Kaladim", 2, new UploadResult(false, 403, NotAllowedDetail, RejectCode: LexiconUploadClient.RejectServerNotAllowed)));
        Assert.Equal(
            ["[not-configured|Lord Bob|No API token configured|1]"],
            await StatusesFor("Varsoon", 1, UploadResult.NoClient("No API token configured.")));
    }

    [Fact]
    public void The_Default_Messages_Are_The_English_Lines_Word_For_Word()
    {
        // The app's English dictionary carries these same strings under
        // UploadQ_* keys (an App test compares the two), so this is the
        // one place the wording is pinned.
        var en = UploadQueueMessages.English;
        Assert.Equal("Lord Bob — Uploaded (inserted). 3 uploaded this session.", en.Uploaded("Lord Bob", "inserted", 3));
        Assert.Equal("Lord Bob upload retrying — unavailable", en.Retrying("Lord Bob", "unavailable"));
        Assert.Equal("Lord Bob upload failed: Implausible parse.", en.Failed("Lord Bob", "Implausible parse."));
        Assert.Equal("Lord Bob upload refused: Not here.", en.Refused("Lord Bob", "Not here."));
        Assert.Equal("uploads paused — token rejected (401). Update it and hit Test.", en.PausedTokenRejected(401));
        Assert.Equal("uploads paused — refused by the site (403). Check the token and its account, then hit Test.", en.PausedRefused(403));
        Assert.Equal("uploads paused — refused by the site (403): Account not approved. Fix that, then hit Test.", en.PausedRefusedWithDetail(403, "Account not approved"));
        Assert.Equal("skipped Lord Bob — the site does not accept uploads from Kaladim (2 skipped this session)", en.SkippedRefusedServer("Lord Bob", "Kaladim", 2));
        Assert.Equal("Lord Bob upload waiting 30 s — the site's upload rate limit was reached (attempt 1 of 3 used).", en.RateLimitWait("Lord Bob", 30, 1, 3));
        Assert.Equal("couldn't build the upload for Lord Bob (boom)", en.BuildFailed("Lord Bob", "boom"));
        Assert.Equal("Lord Bob not uploaded — No API token configured (2 dropped this session)", en.NotConfigured("Lord Bob", "No API token configured", 2));
    }
}
