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
/// does not accept must not stop the main's uploads. A send that had no
/// client to go through (<see cref="UploadResult.NotConfigured"/>) is
/// dropped at once, and an answer that arrives after the configuration
/// changed (<see cref="ConfigurationChanged"/>) never pauses the queue or
/// marks a server refused: it was about a token or site no longer in use.
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

    // Guards the configuration generation together with the two pieces of
    // state an answer from the site can set against it (the pause and the
    // refused servers): "is this answer still about the current
    // configuration?" and "apply it" must be one step, or a reconfigure
    // could land between them.
    private readonly Lock _configGate = new();
    private int _generation;

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
    /// as <see cref="RateLimitWait"/> says each time.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>Wait before re-sending an upload the site answered 429 to
    /// when the answer carried no usable Retry-After — which is the usual
    /// case: the site allows a token 60 uploads a minute over a moving
    /// window, and its 429 for that limit sends no such header (see
    /// <see cref="LexiconUploadClient.ReadRetryAfter"/> for which of its
    /// 429s do). Two of these waits (the default <see cref="RetryDelays"/>
    /// budget) span the whole window, by which time every upload made
    /// before the first 429 has left it. Tests inject zero.</summary>
    public TimeSpan RateLimitDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Shortest wait honoured from a Retry-After: one that says 0
    /// still gets a second, so three attempts cannot be spent in a burst.</summary>
    public static readonly TimeSpan MinRetryAfter = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait honoured from a Retry-After. The drain is held
    /// for the whole wait and every fight queues behind it, so a server
    /// (or a proxy) asking for an hour is asked again after two minutes;
    /// the attempt budget still ends it.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(120);

    /// <summary>The status lines, English unless the host supplies its own
    /// (the app passes its translated set).</summary>
    public UploadQueueMessages Messages { get; init; } = UploadQueueMessages.English;

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

    /// <summary>Queued fights dropped this session because the send had no
    /// client to go through (<see cref="UploadResult.NotConfigured"/>): the
    /// token was cleared, or the site address refused, while they waited.
    /// Nothing was sent for them, so they are not in <see cref="Failed"/>.</summary>
    public int SkippedNotConfigured { get; private set; }

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
    /// A <paramref name="loggerServer"/> the site would refuse on its shape
    /// alone is dropped: an empty one (a log outside any logs/&lt;server&gt;/
    /// folder — <see cref="LogPaths.ParseServerName"/>) and one that fails
    /// <see cref="LogPaths.IsServerNameAccepted"/> (a folder that is not
    /// named like a server) both get a 400 from the site, so the request
    /// could only fail. Callers explain that to the user before they get
    /// here (UploadService does, on both its paths); this is the backstop
    /// that keeps the doomed request off the wire.</summary>
    public void Enqueue(Encounter encounter, string loggerServer, bool withProvenance = true)
    {
        if (_authPaused || encounter.Title == Encounter.PlaceholderTitle || !LogPaths.IsServerNameAccepted(loggerServer))
            return;
        _work.Writer.TryWrite((encounter, loggerServer, withProvenance));
    }

    /// <summary>Call when the user explicitly retries with the SAME
    /// configuration (the Test button succeeded, a manual upload) so a
    /// previous rejection stops gating new uploads. A changed token or
    /// site is <see cref="ConfigurationChanged"/>.</summary>
    public void ResetAuthPause() => _authPaused = false;

    /// <summary>Call when the user explicitly retries with the SAME
    /// configuration — the next fight from a previously refused server is
    /// sent again and the site's answer decides afresh. A changed token or
    /// site is <see cref="ConfigurationChanged"/>.</summary>
    public void ForgetRefusedServers() => _refusedServers.Clear();

    /// <summary>Call whenever the transport behind <c>send</c> may have
    /// changed — another token, another site, or none at all — and call it
    /// AFTER the new transport is in place. It starts a new configuration
    /// generation: the pause and the refused servers are cleared (they were
    /// answers about the old token or site), and a send still in flight on
    /// the old transport can no longer set either. When its answer arrives
    /// it is read as stale: a 401 or 403 from it does not pause, does not
    /// mark a server refused and does not fail the upload — the upload is
    /// sent again under the current configuration, which has not judged it
    /// yet. The order matters because a send reads the generation BEFORE
    /// it picks up the transport: swap first and bump second, and every
    /// answer from the old transport carries an older generation.</summary>
    public void ConfigurationChanged()
    {
        lock (_configGate)
        {
            _generation++;
            _authPaused = false;
            _refusedServers.Clear();
        }
    }

    /// <summary>How long to wait after a 429: the site's Retry-After when
    /// it sent a usable one, held between <see cref="MinRetryAfter"/> and
    /// <see cref="MaxRetryAfter"/>; otherwise <paramref name="fallback"/>
    /// (<see cref="RateLimitDelay"/>), unclamped. Absent and negative both
    /// count as unusable.</summary>
    public static TimeSpan RateLimitWait(TimeSpan? retryAfter, TimeSpan fallback)
    {
        if (retryAfter is not { } asked || asked < TimeSpan.Zero)
            return fallback;
        if (asked < MinRetryAfter)
            return MinRetryAfter;
        return asked > MaxRetryAfter ? MaxRetryAfter : asked;
    }

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
                    Set(Messages.SkippedRefusedServer(encounter.Title, server, SkippedRefusedServer));
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
                    Set(Messages.BuildFailed(encounter.Title, ex.Message));
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
        var attempt = 0;
        while (true)
        {
            // Read BEFORE the send picks up its transport — see
            // ConfigurationChanged for why that order is the guarantee.
            int generation;
            lock (_configGate)
            {
                generation = _generation;
            }
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

            if (result.NotConfigured)
            {
                // Nothing was sent and nothing can be until the user saves
                // a token, so this is neither a transient failure (the 5 s
                // and 30 s waits stalled the drain ~35 s per queued fight)
                // nor a failed upload. The fight is DROPPED, not held: held
                // work would go out later under whatever configuration came
                // next — possibly another account — long after the user
                // withdrew the token, with a provenance probe that no
                // longer says who was writing the log. It stays in local
                // history and can be uploaded by hand from the fight list.
                SkippedNotConfigured++;
                Set(Messages.NotConfigured(title, WithoutFullStop(result.Message), SkippedNotConfigured));
                return;
            }
            if (result.Success)
            {
                Uploaded++;
                Set(Messages.Uploaded(title, result.ServerStatus.Length > 0 ? result.ServerStatus : "ok", Uploaded));
                return;
            }
            if (result.StatusCode is 401 or 403)
            {
                // 401 is always the credential (token or signature). A 403
                // is the credential's ACCOUNT only when the site does not
                // mark it: an older server marks nothing, and a current one
                // leaves an account-level refusal (an unapproved account)
                // unmarked, so the unmarked 403 keeps the cautious reading
                // and pauses. A 403 marked with the reject header is the
                // site refusing THIS upload, whatever the code says (codes
                // are not enumerated — the site adds them freely).
                var pauses = result.StatusCode == 401 || result.RejectCode.Length == 0;
                var serverRefused = !pauses && string.Equals(
                    result.RejectCode, LexiconUploadClient.RejectServerNotAllowed, StringComparison.OrdinalIgnoreCase);
                bool current;
                lock (_configGate)
                {
                    current = generation == _generation;
                    if (current && pauses)
                        _authPaused = true;
                    else if (current && serverRefused)
                        _refusedServers.TryAdd(payload.LoggerServer, 0);
                }
                if (!current)
                {
                    // The configuration changed while this send was in
                    // flight: the refusal is the OLD token's or site's and
                    // says nothing about the new one. Send again under the
                    // current configuration, outside the retry budget —
                    // each such re-send needs another reconfigure, so the
                    // user's own clicks bound it.
                    continue;
                }
                Failed++;
                // Marked: no pause, and no retry — the same payload would
                // get the same answer. The message is the server's detail,
                // which names what it objected to.
                Set(pauses ? PauseStatus(result) : Messages.Refused(title, result.Message));
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
                Set(Messages.Failed(title, result.Message));
                return;
            }
            if (rateLimited)
            {
                // The drain is held on purpose — every upload behind this
                // one would get the same answer — so the status says so,
                // with the length of the wait, instead of looking hung.
                // The wait is the site's own Retry-After when it sent one
                // (bounded), else the fixed RateLimitDelay. Dispose cancels
                // the delay like any other.
                var wait = RateLimitWait(result.RetryAfter, RateLimitDelay);
                Set(Messages.RateLimitWait(title, (int)Math.Ceiling(wait.TotalSeconds), attempt + 1, RetryDelays.Count + 1));
                await Task.Delay(wait, _cts.Token).ConfigureAwait(false);
            }
            else
            {
                Set(Messages.Retrying(title, result.Message));
                await Task.Delay(RetryDelays[attempt], _cts.Token).ConfigureAwait(false);
            }
            attempt++;
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
    private string PauseStatus(UploadResult result)
    {
        if (result.StatusCode == 401)
            return Messages.PausedTokenRejected(result.StatusCode);
        var detail = WithoutFullStop(result.Message);
        return detail.Length == 0
            ? Messages.PausedRefused(result.StatusCode)
            : Messages.PausedRefusedWithDetail(result.StatusCode, detail);
    }

    /// <summary>Text about to be set inside a sentence that brings its own
    /// punctuation: trimmed, without a trailing full stop.</summary>
    private static string WithoutFullStop(string text) => text.Trim().TrimEnd('.');

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
