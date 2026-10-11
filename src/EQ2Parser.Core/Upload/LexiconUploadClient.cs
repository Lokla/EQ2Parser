using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EQ2Parser.Core.Upload;

/// <param name="Success">2xx.</param>
/// <param name="StatusCode">HTTP status; 0 when no response arrived
/// (network error, timeout).</param>
/// <param name="Message">User-facing line: the server's status on success,
/// its <c>detail</c> on failure.</param>
/// <param name="Body">Raw response body.</param>
/// <param name="RejectCode">Value of the
/// <see cref="LexiconUploadClient.RejectHeader"/> response header on a failed
/// signed upload, trimmed; "" when the header is absent or blank. A
/// machine code for the queue's pause decision — never shown to the user
/// (the <c>detail</c> in <paramref name="Message"/> is the human text).</param>
public sealed record UploadResult(bool Success, int StatusCode, string Message, string Body = "", string RejectCode = "")
{
    /// <summary>How long the site asked the client to wait before trying
    /// again (the <c>Retry-After</c> response header on a failed signed
    /// upload), or null when it sent none or one that cannot be used — see
    /// <see cref="LexiconUploadClient.ReadRetryAfter"/>. Unclamped: the
    /// reader (<see cref="UploadQueue.RateLimitWait"/>) bounds it.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>The site's own <c>status</c> word on a successful signed
    /// upload ("inserted", "skipped", "quarantined"); "" when there is none.
    /// Kept apart from <see cref="Message"/> so a status line can be written
    /// around it in the user's language.</summary>
    public string ServerStatus { get; init; } = "";

    /// <summary>True when NOTHING was sent because there is no client to
    /// send with (no saved token, or a site address the client refuses).
    /// Not a transport failure: retrying cannot help until the user changes
    /// the configuration, so <see cref="UploadQueue"/> drops the upload at
    /// once instead of walking its retry waits.</summary>
    public bool NotConfigured { get; init; }

    /// <summary>The outcome of a send that had no client to go through;
    /// <paramref name="reason"/> is the user-facing why (never a credential
    /// — there is none to name).</summary>
    public static UploadResult NoClient(string reason) => new(false, 0, reason) { NotConfigured = true };
}

/// <summary>
/// HTTP client for the EQ2Lexicon site — the exact transport contract the
/// ACT plugin v0.1.17 ships (and the server's gzip middleware expects):
///   * Bearer token auth,
///   * HMAC-SHA256 over the UNCOMPRESSED JSON in X-Lexicon-Signature,
///   * body gzip-compressed with Content-Encoding: gzip,
///   * 60 s timeout (uploads are background work; patience is free).
/// HttpMessageHandler is injectable for tests.
/// </summary>
public sealed class LexiconUploadClient : IDisposable
{
    public const string SignatureHeader = "X-Lexicon-Signature";

    /// <summary>Response header the site sets on a refusal that is about
    /// THIS upload and not about the caller's credentials — a different
    /// upload with the same token could still succeed. Mirrors
    /// <c>REJECT_HEADER</c> in EQ2Lexicon's
    /// <c>backend/server/api/parses/ingest.py</c>; change both together.
    /// Its value is a short lowercase machine code. The site may add codes
    /// at any time, so the rule in <see cref="UploadQueue"/> is "a 403 with
    /// any non-empty value does not pause", never a list of known codes: a
    /// 403 WITHOUT the header is what an older server sends for everything
    /// and what a current one sends for an account-level refusal (an
    /// unapproved account), and that still pauses.</summary>
    public const string RejectHeader = "X-Lexicon-Reject";

    /// <summary>The one reject code the client reads for more than "do not
    /// pause": the upload's <c>logger_server</c> is not on the site's allow
    /// list (<c>ingest_parse</c> in the same server module). Every later
    /// fight from that log folder would be refused the same way, so the
    /// queue stops sending for that server for the session instead of
    /// spending the token's per-minute upload budget on them. An unknown
    /// code gets the generic treatment — nothing breaks when the site adds
    /// one.</summary>
    public const string RejectServerNotAllowed = "server-not-allowed";

    private readonly HttpClient _http;

    public LexiconUploadClient(string serverUrl, string apiToken, HttpMessageHandler? handler = null)
    {
        ServerUrl = serverUrl.TrimEnd('/');
        ApiToken = apiToken;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"EQ2Parser/{typeof(LexiconUploadClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}");
    }

    public string ServerUrl { get; }
    private string ApiToken { get; }

    public void Dispose() => _http.Dispose();

    /// <summary>Refuses to put the bearer token on the wire in cleartext:
    /// https only, with a loopback exception for local dev servers. Returns
    /// the problem, or null when the URL is acceptable.</summary>
    public static string? UrlProblem(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Server URL is not a valid http(s) address.";
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            return "Uploads need an https:// server URL — your API token would otherwise travel unencrypted.";
        return null;
    }

    /// <summary>HMAC-SHA256 lowercase hex, keyed by the API token — must match
    /// the server's recomputation over the decompressed body.</summary>
    public static string Sign(string payloadJson, string apiToken) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(apiToken), Encoding.UTF8.GetBytes(payloadJson)));

    public static byte[] CompressUtf8Gzip(string json)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw);
        return ms.ToArray();
    }

    public Task<UploadResult> UploadAsync(LexiconPayload payload, CancellationToken ct = default) =>
        PostSignedAsync("/api/parses/ingest", payload.ToJson(), ct);

    /// <summary>Attendance snapshots ride the same signed+gzipped POST
    /// contract as parse uploads, on their own endpoint (Phase 2 server
    /// work; a 404 from an older server is expected and non-fatal).</summary>
    public Task<UploadResult> UploadAttendanceAsync(AttendancePayload payload, CancellationToken ct = default) =>
        PostSignedAsync("/api/attendance/ingest", payload.ToJson(), ct);

    /// <summary>One canonical signed upload: bearer + HMAC over the
    /// UNCOMPRESSED json in the signature header, body gzipped.</summary>
    private async Task<UploadResult> PostSignedAsync(string relativePath, string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}{relativePath}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        request.Headers.Add(SignatureHeader, Sign(json, ApiToken));

        var content = new ByteArrayContent(CompressUtf8Gzip(json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        request.Content = content;

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UploadResult(
                    false,
                    (int)response.StatusCode,
                    ExtractDetail(body) ?? $"Server responded {(int)response.StatusCode}",
                    body,
                    ReadRejectCode(response))
                {
                    RetryAfter = ReadRetryAfter(response.Headers.RetryAfter, DateTimeOffset.UtcNow),
                };
            }
            var status = ExtractField(body, "status") ?? "ok";
            return new UploadResult(true, (int)response.StatusCode, $"Uploaded ({status}).", body) { ServerStatus = status };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UploadResult(false, 0, "Upload timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new UploadResult(false, 0, $"Network error: {ex.Message}");
        }
    }

    /// <summary>Best-effort raid-main map from the site: {character → main}
    /// for every rostered character in the logger's guild (raiders map to
    /// themselves, alts to their owner's main). Null on ANY failure — an
    /// older server (404), auth trouble, network — callers fall back to the
    /// bulk raid DKP grant. Case-insensitive keys (EQ2 names are).</summary>
    public async Task<Dictionary<string, string>?> FetchRaidMainsAsync(string character, string server, CancellationToken ct = default)
    {
        var query = $"character={Uri.EscapeDataString(character)}&server={Uri.EscapeDataString(server)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/attendance/mains?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("mains", out var mains) || mains.ValueKind != JsonValueKind.Object)
                return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in mains.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } main)
                    map[prop.Name] = main;
            }
            return map;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<UploadResult> WhoAmIAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/auth/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new UploadResult(response.IsSuccessStatusCode, (int)response.StatusCode,
                response.IsSuccessStatusCode ? "OK" : ExtractDetail(body) ?? "Auth failed", body);
        }
        catch (HttpRequestException ex)
        {
            return new UploadResult(false, 0, $"Network error: {ex.Message}");
        }
    }

    /// <summary>Reads the attendance-preview entitlement from a whoami
    /// response body: is_admin, or 'subscriber' in static_roles (the site's
    /// limited-preview role for the raid-attendance feature set). Absent
    /// fields (older server) read as no access.</summary>
    public static bool ParseAttendanceAccess(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("is_admin", out var admin) && admin.ValueKind == JsonValueKind.True)
                return true;
            return root.TryGetProperty("static_roles", out var roles)
                && roles.ValueKind == JsonValueKind.Array
                && roles.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == "subscriber");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The <see cref="RejectHeader"/> value, trimmed; "" when the
    /// header is absent or carries only whitespace (a blank value says
    /// nothing, so it reads as absent and the queue's cautious branch —
    /// pause — applies).</summary>
    private static string ReadRejectCode(HttpResponseMessage response) =>
        response.Headers.TryGetValues(RejectHeader, out var values)
            ? values.Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0) ?? ""
            : "";

    /// <summary>A <c>Retry-After</c> header as a wait from
    /// <paramref name="now"/>, in either form HTTP allows: delta-seconds
    /// ("30") or an HTTP-date. Null when the header is absent, does not
    /// parse (the typed header is null then), or names a date that is not
    /// in the future — a past date is more likely a skewed clock than
    /// "retry at once", so it reads as no answer and the caller's own wait
    /// applies. The value is NOT clamped here.
    ///
    /// Which of the site's 429s carry it (EQ2Lexicon, read 2026-10):
    ///   * The per-token limit on parse ingest (60 a minute, the slowapi
    ///     <c>@limiter.limit</c> on <c>ingest_parse</c>) does NOT. It is
    ///     answered by <c>_rate_limit_handler</c> in
    ///     <c>backend/server/app.py</c> with slowapi's stock response, and
    ///     the limiter in <c>backend/server/limiter.py</c> is built without
    ///     <c>headers_enabled</c>, so no header is injected. Its body has
    ///     an <c>error</c> key and no <c>detail</c>.
    ///   * The per-client flood budget (<c>_client_flood_gate</c> in
    ///     <c>backend/server/api/parses/ingest.py</c>, counted by
    ///     <c>backend/server/core/client_throttle.py</c>) DOES, as
    ///     delta-seconds of at least 1 (<c>ThrottleVerdict.retry_after</c>),
    ///     and <c>_http_exception_handler</c> keeps the header. It applies
    ///     only to a User-Agent prefix named in INGEST_CLIENT_LIMITS; the
    ///     default names none of this client's, so it reaches the parser
    ///     only when the site configures one.
    /// A proxy in front of the site may add either form on its own
    /// 429 or 503.</summary>
    public static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null)
            return null;
        if (header.Delta is { } delta)
            return delta >= TimeSpan.Zero ? delta : null;
        if (header.Date is { } date && date > now)
            return date - now;
        return null;
    }

    private static string? ExtractDetail(string body) => ExtractField(body, "detail");

    private static string? ExtractField(string body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
