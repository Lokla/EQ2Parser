using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.Core.Tests;

public class UploadClientTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static Encounter BuildEncounter()
    {
        var engine = new ParserEngine("log-a", "Menludiir");
        engine.ChangeZone("Deathtoll");
        Assert.True(engine.SetEncounter(T0, "Menludiir", "Lord Bob"));
        engine.AddSwing(SwingCategory.Melee, true, "None", "Menludiir", "Strike", 1000, T0, "Lord Bob", "crushing");
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Menludiir", "Smite", 500, T0.AddSeconds(10), "Lord Bob", "divine");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", Combatant.KillingAbility, DamageValue.Death, T0.AddSeconds(10), "Lord Bob", "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    [Fact]
    public void Hmac_Matches_The_Server_Vector()
    {
        // Vector computed with the server's own algorithm (hmac-sha256,
        // key = utf8 token, lowercase hex).
        Assert.Equal(
            "94e4bb02b2c616f2743a031af3b2100e22d8eb9aa74d07a5d5086372f62ca4c7",
            LexiconUploadClient.Sign("""{"logger_name":"Menludiir"}""", "eq2c_test_token"));
    }

    [Fact]
    public void Payload_Wire_Shape_Matches_The_Ingest_Schema()
    {
        var payload = PayloadBuilder.Build(BuildEncounter(), "Varsoon");
        using var doc = JsonDocument.Parse(payload.ToJson());
        var root = doc.RootElement;

        Assert.Equal("Menludiir", root.GetProperty("logger_name").GetString());
        Assert.Equal("Varsoon", root.GetProperty("logger_server").GetString());

        var enc = root.GetProperty("encounter");
        Assert.Equal("Lord Bob", enc.GetProperty("title").GetString());
        Assert.Equal("Deathtoll", enc.GetProperty("zone").GetString());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", enc.GetProperty("starttime").GetString()!);
        Assert.Matches("^[0-9a-f]{8}$", enc.GetProperty("encid").GetString()!);
        Assert.Equal(10, enc.GetProperty("duration").GetInt32());
        Assert.Equal(1500, enc.GetProperty("damage").GetInt64());
        Assert.Equal(150, enc.GetProperty("encdps").GetDouble());
        Assert.Equal((int)SuccessLevel.Win, enc.GetProperty("success").GetInt32());

        var combatant = root.GetProperty("combatants").EnumerateArray().First();
        Assert.Equal("Menludiir", combatant.GetProperty("name").GetString());
        Assert.Equal("T", combatant.GetProperty("ally").GetString());
        Assert.Equal(2, combatant.GetProperty("hits").GetInt32());
        Assert.Equal(1, combatant.GetProperty("crithits").GetInt32());

        // Damage-type rows use the bucket names; attack types carry swingtype.
        var dt = root.GetProperty("damage_types").EnumerateArray()
            .First(d => d.GetProperty("type").GetString() == BucketConfig.OutgoingDamage);
        Assert.Equal(1500, dt.GetProperty("damage").GetInt64());
        var at = root.GetProperty("attack_types").EnumerateArray()
            .First(a => a.GetProperty("type").GetString() == "Smite");
        Assert.Equal(2, at.GetProperty("swingtype").GetInt32());
        Assert.Equal(500, at.GetProperty("damage").GetInt64());
        // dps + critperc back the site's per-attack DPS + CRIT% columns —
        // they were absent pre-v0.3.5 and rendered as 0 on the parse page.
        Assert.True(at.GetProperty("dps").GetDouble() > 0);
        Assert.InRange(at.GetProperty("critperc").GetDouble(), 0, 100);
        Assert.True(dt.GetProperty("dps").GetDouble() > 0);
        Assert.InRange(dt.GetProperty("critperc").GetDouble(), 0, 100);

        // resist: the server's IngestAttackType key (str | None) behind the
        // site's heal-type and threat-kind columns. Every attack-row key is
        // one that model declares (an undeclared key would be accepted and
        // dropped, so a 201 proves nothing).
        Assert.Equal("divine", at.GetProperty("resist").GetString());
        Assert.Equal(
            [
                "attacker", "type", "swingtype", "damage", "encdps", "dps", "critperc", "median",
                "minhit", "maxhit", "resist", "hits", "crithits", "blocked", "misses", "swings",
            ],
            at.EnumerateObject().Select(p => p.Name));
        // A row with nothing to say omits the key — never "resist":null.
        foreach (var row in root.GetProperty("attack_types").EnumerateArray())
            Assert.All(row.EnumerateObject(), p => Assert.NotEqual(JsonValueKind.Null, p.Value.ValueKind));
    }

    [Fact]
    public void EncId_Is_Stable_For_The_Same_Fight()
    {
        Assert.Equal(PayloadBuilder.ComputeEncId(BuildEncounter()), PayloadBuilder.ComputeEncId(BuildEncounter()));
    }

    [Fact]
    public void Attack_Types_Are_Unique_Per_Combatant_And_Ability()
    {
        // Regression: per-BUCKET emission duplicated any ability feeding two
        // buckets (melee → Outgoing Damage + Auto-Attack (Out)) and the
        // site's UNIQUE(combatant_id, swing_type, attack_name) turned every
        // real upload into a 500.
        var payload = PayloadBuilder.Build(BuildEncounter(), "Varsoon");
        var keys = payload.AttackTypes.Select(a => (a.Attacker, a.SwingType, a.Type)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void VictimOnly_Combatants_Get_Fight_Times_Not_Sentinels()
    {
        // An add that dies without ever acting has MaxValue/MinValue
        // windows — the payload must clamp them to the fight, not ship
        // year-9999/year-1 timestamps.
        var payload = PayloadBuilder.Build(BuildEncounter(), "Varsoon");
        foreach (var combatant in payload.Combatants)
        {
            Assert.DoesNotContain("9999", combatant.StartTime);
            Assert.DoesNotContain("0001", combatant.EndTime);
        }
    }

    [Fact]
    public async Task FetchRaidMains_Parses_The_Map_Case_Insensitively()
    {
        var handler = new GetHandler(HttpStatusCode.OK,
            """{"world":"Varsoon","guild":"Exordium","mains":{"Mainy":"Mainy","Alty":"Mainy"}}""");
        using var client = new LexiconUploadClient("https://parses.example.com/", "eq2c_test_token", handler);

        var mains = await client.FetchRaidMainsAsync("Menludiir", "Varsoon");

        Assert.NotNull(mains);
        Assert.Equal("Mainy", mains["ALTY"]); // EQ2 names are case-insensitive
        Assert.Equal("/api/attendance/mains?character=Menludiir&server=Varsoon", handler.Request!.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer eq2c_test_token", handler.Request.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")] // older server without the endpoint
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"detail":"no mains key"}""")]
    public async Task FetchRaidMains_Returns_Null_On_Any_Failure(HttpStatusCode status, string body)
    {
        var handler = new GetHandler(status, body);
        using var client = new LexiconUploadClient("https://parses.example.com/", "eq2c_test_token", handler);
        Assert.Null(await client.FetchRaidMainsAsync("Menludiir", "Varsoon"));
    }

    [Theory]
    [InlineData("""{"is_admin":true,"static_roles":[]}""", true)]
    [InlineData("""{"is_admin":false,"static_roles":["subscriber"]}""", true)]
    [InlineData("""{"is_admin":false,"static_roles":["contributor","subscriber"]}""", true)]
    [InlineData("""{"is_admin":false,"static_roles":["supporter"]}""", false)]
    [InlineData("""{"is_admin":false}""", false)] // pre-static_roles server
    [InlineData("not json", false)]
    public void ParseAttendanceAccess_Reads_Admin_Or_Subscriber(string body, bool expected)
    {
        Assert.Equal(expected, LexiconUploadClient.ParseAttendanceAccess(body));
    }

    private sealed class GetHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public byte[] RequestBody = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsByteArrayAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Upload_Sends_Gzip_Body_With_Hmac_Over_The_Uncompressed_Json()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, """{"status":"inserted","encounter_id":42}""");
        using var client = new LexiconUploadClient("https://parses.example.com/", "eq2c_test_token", handler);

        var result = await client.UploadAsync(PayloadBuilder.Build(BuildEncounter(), "Varsoon"));

        Assert.True(result.Success);
        Assert.Equal(201, result.StatusCode);
        Assert.Equal("Uploaded (inserted).", result.Message);

        var request = handler.Request!;
        Assert.Equal("https://parses.example.com/api/parses/ingest", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("eq2c_test_token", request.Headers.Authorization.Parameter);
        Assert.Contains("gzip", request.Content!.Headers.ContentEncoding);

        // Decompress and verify the signature covers the UNCOMPRESSED json.
        using var gz = new GZipStream(new MemoryStream(handler.RequestBody), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        Assert.Equal(
            LexiconUploadClient.Sign(json, "eq2c_test_token"),
            request.Headers.GetValues(LexiconUploadClient.SignatureHeader).Single());
        Assert.Contains("\"logger_name\":\"Menludiir\"", json);
    }

    [Fact]
    public async Task Upload_Surfaces_Server_Detail_On_Failure()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, """{"detail":"Invalid or expired token."}""");
        using var client = new LexiconUploadClient("https://parses.example.com", "bad", handler);

        var result = await client.UploadAsync(PayloadBuilder.Build(BuildEncounter(), "Varsoon"));
        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal("Invalid or expired token.", result.Message);
    }

    [Fact]
    public void Reject_Header_Name_Matches_The_Server_Constant()
    {
        // Pinned like the HMAC vector: the string is the contract. It must
        // equal REJECT_HEADER in EQ2Lexicon's backend/server/api/parses/ingest.py
        // — a typo on either side silently turns every per-upload refusal
        // back into a queue-wide pause.
        Assert.Equal("X-Lexicon-Reject", LexiconUploadClient.RejectHeader);
        Assert.Equal("server-not-allowed", LexiconUploadClient.RejectServerNotAllowed);
    }

    /// <summary>Answers 403 with a detail body and, when given, the reject
    /// header set to exactly the supplied value.</summary>
    private sealed class RefusingHandler(string? rejectHeaderValue) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"detail":"Server 'Kaladim' is not on the allowed list."}""", Encoding.UTF8, "application/json"),
            };
            if (rejectHeaderValue is not null)
                response.Headers.TryAddWithoutValidation(LexiconUploadClient.RejectHeader, rejectHeaderValue);
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData("server-not-allowed", "server-not-allowed")]
    [InlineData("  some-future-code ", "some-future-code")] // any code passes through, trimmed — codes are not enumerated
    [InlineData(null, "")] //                                  older server, or an account-level refusal
    [InlineData("", "")] //                                    present but blank says nothing — reads as absent
    [InlineData("   ", "")]
    public async Task Upload_Carries_The_Reject_Code_From_The_Response_Header(string? header, string expected)
    {
        using var client = new LexiconUploadClient("https://parses.example.com", "eq2c_test_token", new RefusingHandler(header));

        var result = await client.UploadAsync(PayloadBuilder.Build(BuildEncounter(), "Kaladim"));

        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.Equal(expected, result.RejectCode);
        // The human text is still the server's detail, header or not.
        Assert.Equal("Server 'Kaladim' is not on the allowed list.", result.Message);
    }

    [Fact]
    public async Task Attendance_Upload_Reads_The_Reject_Code_Too()
    {
        // Same signed POST, same header: the site marks its attendance
        // refusals (server not tracked, no claim, no guild) the same way.
        using var client = new LexiconUploadClient("https://parses.example.com", "eq2c_test_token", new RefusingHandler("no-guild"));

        var result = await client.UploadAttendanceAsync(new AttendancePayload { LoggerName = "Menludiir", LoggerServer = "Varsoon" });

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("no-guild", result.RejectCode);
    }
}
