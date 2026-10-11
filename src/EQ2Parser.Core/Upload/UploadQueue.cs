using System.Collections.Concurrent;
using System.Threading.Channels;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Upload;

/// <summary>
/// Serializes encounter uploads off the log-pump thread — the engine's
/// EncounterEnded fires inside the global sync lock, so Enqueue only writes
/// to a channel; payload building and transport happen on the background
/// drain (the HistoryService.QueueSave pattern). Transient failures and the
/// site's rate limit (429) retry a bounded number of times; a rejected token
/// pauses the queue instead of re-sending a bad credential for every
/// fight. A refusal the site marks as
/// being about ONE upload (<see cref="LexiconUploadClient.RejectHeader"/>)
/// fails that upload alone and the rest of the queue carries on — the app
/// watches every logs/&lt;server&gt;/ folder, so an alt on a server the site
/// does not accept must not stop the main's uploads.
/// </summary>
public sealed class UploadQueue : IDisposable
{
    private readonly Channel<(Encounter Encounter, string LoggerServer, bool WithProvenance)> _work =
        Channel.CreateUnbounded<(Encounter, string, bool)>();
    private readonly Func<LexiconPayload, CancellationToken, Task<UploadResult>> _send;
    private readonly Func<Encounter, IReadOnlyList<string>?>? _provenance;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _drain;
    private volatile bool _authPaused;

    // logger_server values the site has answered server-not-allowed for
    // this session. Written by the drain, cleared from the UI thread
    // (ForgetRefusedServers), hence concurrent. Case-insensitive like the
    // site's allow-list check; bounded by the number of log folders watched.
    private readonly ConcurrentDictionary<string, byte> _refusedServers = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="send">Transport — the client's UploadAsync.</param>
    /// <param name="provenance">Optional client_warnings provider (the log
    /// writer probe), invoked on the drain thread right before the payload
    /// builds — never on the pump thread.</param>
    public UploadQueue(
        Func<LexiconPayload, CancellationToken, Task<UploadResult>> send,
        Func<Encounter, IReadOnlyList<string>?>? provenance = null)
    {
        _send = send;
        _provenance = provenance;
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>Waits between attempts after a transient failure (network
    /// error or 5xx) — total attempts = Count + 1. Tests inject zeros. A
    /// rate-limited upload (429) draws on the same attempt count but waits
    /// <see cref="RateLimitDelay"/> each time.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>Wait before re-sending an upload the site answered 429 to.
    /// The site allows a token 60 uploads a minute over a moving window,
    /// and its 429 for that limit carries no Retry-After, so this is a
    /// fixed wait: two of them (the default <see cref="RetryDelays"/>
    /// budget) span the whole window, by which time every upload made
    /// before the first 429 has left it. Tests inject zero.</summary>
    public TimeSpan RateLimitDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Fights uploaded this session (drain thread; reads are for
    /// status display and test polling).</summary>
    public int Uploaded { get; private set; }

    public int Failed { get; private set; }

    /// <summary>Fights NOT sent this session because the site had already
    /// answered <see cref="LexiconUploadClient.RejectServerNotAllowed"/> for
    /// their logger_server (drain thread, like <see cref="Uploaded"/>). Each
    /// one would have been a request guaranteed to be refused, charged to
    /// the token's per-minute upload budget — a trash-clearing alt on a
    /// server the site does not accept must not eat the main's allowance.
    /// The first refusal is counted in <see cref="Failed"/>, not here.</summary>
    public int SkippedRefusedServer { get; private set; }

    /// <summary>True after a 401, or a 403 that does NOT carry
    /// <see cref="LexiconUploadClient.RejectHeader"/> — work is dropped
    /// until the token changes (<see cref="ResetAuthPause"/>) rather than
    /// hammering the server with a credential it already rejected.</summary>
    public bool AuthPaused => _authPaused;

    /// <summary>Human-readable last event — the Settings card shows it.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Raised from the drain thread whenever Status changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Safe from any thread; never blocks. Pass
    /// <paramref name="withProvenance"/> = false when the fight ended long
    /// ago (manual re-uploads of archived fights) — probing the log NOW
    /// would say nothing about who wrote it THEN, so nothing is claimed.
    /// An empty <paramref name="loggerServer"/> (a log outside any
    /// logs/&lt;server&gt;/ folder — <see cref="LogPaths.ParseServerName"/>) is
    /// dropped: the site answers 400 to an upload without a server, so the
    /// request could only fail. Callers explain that to the user before
    /// they get here (UploadService does, on both its paths); this is the
    /// backstop that keeps the doomed request off the wire.</summary>
    public void Enqueue(Encounter encounter, string loggerServer, bool withProvenance = true)
    {
        if (_authPaused || encounter.Title == Encounter.PlaceholderTitle || string.IsNullOrWhiteSpace(loggerServer))
            return;
        _work.Writer.TryWrite((encounter, loggerServer, withProvenance));
    }

    /// <summary>Call when the token changes (or the user explicitly retries)
    /// so a previous rejection stops gating new uploads.</summary>
    public void ResetAuthPause() => _authPaused = false;

    /// <summary>Call when the token or the site URL changes (another site,
    /// or another account's view of it, may accept the server), or when the
    /// user explicitly retries — the next fight from a previously refused
    /// server is sent again and the site's answer decides afresh.</summary>
    public void ForgetRefusedServers() => _refusedServers.Clear();

    private void Set(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var (encounter, server, withProvenance) in _work.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_authPaused)
                    continue; // drained but dropped — queued before the pause landed
                if (_refusedServers.ContainsKey(server))
                {
                    // Checked here, not in Enqueue: a replayed log queues a
                    // whole file's fights before the first answer arrives,
                    // and those must be caught too. Skipping before the
                    // probe and the payload build also saves that work.
                    SkippedRefusedServer++;
                    Set($"skipped {encounter.Title} — the site does not accept uploads from {server} ({SkippedRefusedServer} skipped this session)");
                    continue;
                }
                LexiconPayload payload;
                try
                {
                    IReadOnlyList<string>? warnings = null;
                    if (withProvenance && _provenance is { } provenance)
                    {
                        try
                        {
                            warnings = provenance(encounter);
                        }
                        catch (Exception)
                        {
                            // A failed probe never blocks the upload — the
                            // payload just carries no provenance claim.
                        }
                    }
                    payload = PayloadBuilder.Build(encounter, server, warnings);
                }
                catch (Exception ex)
                {
                    Failed++;
                    Set($"couldn't build the upload for {encounter.Title} ({ex.Message})");
                    continue;
                }
                await SendWithRetryAsync(payload, encounter.Title).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose — drain exits.
        }
    }

    private async Task SendWithRetryAsync(LexiconPayload payload, string title)
    {
        for (var attempt = 0; ; attempt++)
        {
            UploadResult result;
            try
            {
                result = await _send(payload, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The client converts its own failures to UploadResult; this
                // catches the reconfigure race (client disposed mid-send).
                result = new UploadResult(false, 0, $"Network error: {ex.Message}");
            }

            if (result.Success)
            {
                Uploaded++;
                Set($"{title} — {result.Message} {Uploaded} uploaded this session.");
                return;
            }
            // 401 is always the credential (token or signature). A 403 is
            // the credential's ACCOUNT only when the site does not mark it:
            // an older server marks nothing, and a current one leaves an
            // account-level refusal (an unapproved account) unmarked, so
            // the unmarked 403 keeps the cautious reading and pauses.
            if (result.StatusCode == 401 || (result.StatusCode == 403 && result.RejectCode.Length == 0))
            {
                _authPaused = true;
                Failed++;
                Set(PauseStatus(result));
                return;
            }
            if (result.StatusCode == 403)
            {
                // Marked with the reject header: the site refused THIS
                // upload, whatever the code says (codes are not enumerated
                // — the site adds them freely). No pause, and no retry: the
                // same payload would get the same answer. The message is
                // the server's detail, which names what it objected to.
                Failed++;
                if (string.Equals(result.RejectCode, LexiconUploadClient.RejectServerNotAllowed, StringComparison.OrdinalIgnoreCase))
                    _refusedServers.TryAdd(payload.LoggerServer, 0);
                Set($"{title} upload refused: {result.Message}");
                return;
            }
            // 429 is the site's per-token upload rate limit: nothing is
            // wrong with the upload, it is early. Dropping it (what a
            // non-transient status gets) lost every fight past the limit
            // when a long log was replayed. It retries inside the same
            // attempt budget as a transient failure, after its own wait.
            var rateLimited = result.StatusCode == 429;
            var transient = result.StatusCode == 0 || result.StatusCode >= 500;
            if ((!transient && !rateLimited) || attempt >= RetryDelays.Count)
            {
                Failed++;
                Set($"{title} upload failed: {result.Message}");
                return;
            }
            if (rateLimited)
            {
                // The drain is held on purpose — every upload behind this
                // one would get the same answer — so the status says so,
                // with the length of the wait, instead of looking hung.
                // Dispose cancels the delay like any other.
                var seconds = (int)Math.Ceiling(RateLimitDelay.TotalSeconds);
                Set($"{title} upload waiting {seconds} s — the site's upload rate limit was reached (attempt {attempt + 1} of {RetryDelays.Count + 1} used).");
                await Task.Delay(RateLimitDelay, _cts.Token).ConfigureAwait(false);
                continue;
            }
            Set($"{title} upload retrying — {result.Message}");
            await Task.Delay(RetryDelays[attempt], _cts.Token).ConfigureAwait(false);
        }
    }

    /// <summary>The status line for a pause. A 401 is the token itself (or
    /// the signature made from it), and says so as it always has. An
    /// unmarked 403 is the token's ACCOUNT — the one such refusal the site
    /// sends is "Account not approved" — or anything an older site refuses
    /// with a 403, so the server's own <c>detail</c> is shown: "token
    /// rejected" sent an unapproved user to re-mint a token that was never
    /// the problem. The detail is the site's text about the request; the
    /// queue never holds the token, so it cannot put it here.</summary>
    private static string PauseStatus(UploadResult result)
    {
        if (result.StatusCode == 401)
            return $"uploads paused — token rejected ({result.StatusCode}). Update it and hit Test.";
        var detail = result.Message.Trim().TrimEnd('.');
        return detail.Length == 0
            ? $"uploads paused — refused by the site ({result.StatusCode}). Check the token and its account, then hit Test."
            : $"uploads paused — refused by the site ({result.StatusCode}): {detail}. Fix that, then hit Test.";
    }

    public void Dispose()
    {
        _work.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _drain.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfacing on shutdown — nothing to report.
        }
        _cts.Dispose();
    }
}
