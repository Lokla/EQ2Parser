using System.Net.Http;
using EQ2Parser.App.Localization;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.App.Services;

/// <summary>
/// Wires finished encounters into the EQ2Lexicon upload queue. The engine's
/// EncounterEnded fires under the global sync lock, so the handler only
/// enqueues — payload building and HTTP happen on the queue's background
/// drain. Configure runs at startup and whenever the Settings card changes
/// the token, URL, or toggle.
/// </summary>
public sealed class UploadService : IDisposable
{
    private readonly UploadQueue _queue;

    // Read on the queue's drain thread (SendAsync) and written on the UI
    // thread (Configure) — always by ONE assignment per reconfigure, so a
    // send sees the old client or the new one, never a null in between.
    private volatile LexiconUploadClient? _client;

    // Why there is no client, in the user's words, for the queue's status
    // line when a queued fight meets one. Only meaningful while _client is
    // null; written just before it.
    private volatile string _noClientReason = "";
    private volatile bool _enabled;
    private volatile bool _raidOnly;

    /// <summary>Player-ally count for the raid-only rule (SourceManager
    /// wires the classifier — pets out, the same filter attendance uses).
    /// Null = count unknown = 0, so only the boss list can pass a fight.</summary>
    public Func<Encounter, int>? PlayerCounter { get; set; }

    /// <summary>The site's raid-boss list for the raid-only rule; null =
    /// no sync configured (headcount alone decides).</summary>
    public RaidBossSyncService? RaidBosses { get; set; }

    /// <summary>Fights skipped this session by the raid-only rule.</summary>
    public int SkippedNonRaid { get; private set; }

    /// <summary>Fights skipped this session by the auto path because their
    /// log is not inside a logs/&lt;server&gt;/ folder — there is no server to
    /// put on the upload, and the site refuses one without it.</summary>
    public int SkippedNoServer { get; private set; }

    /// <summary>Fights skipped this session by the auto path because their
    /// log IS in a folder under logs/, but the folder's name is not one the
    /// site accepts as a server name
    /// (<see cref="LogPaths.IsServerNameAccepted"/>) — it would answer 400
    /// "malformed" to every one of them.</summary>
    public int SkippedServerName { get; private set; }

    /// <summary>Fights the queue did not send this session because the site
    /// had already refused their server as not on its allow list — see
    /// <see cref="UploadQueue.SkippedRefusedServer"/>.</summary>
    public int SkippedRefusedServer => _queue.SkippedRefusedServer;

    /// <summary>Queued fights dropped this session because the token was
    /// cleared (or the site address refused) before they were sent — see
    /// <see cref="UploadQueue.SkippedNotConfigured"/>.</summary>
    public int SkippedNotConfigured => _queue.SkippedNotConfigured;

    /// <summary>The queue has stopped sending because the site rejected the
    /// token or its account (<see cref="UploadQueue.AuthPaused"/>). The
    /// state, for code and tests — the status line's wording is translated
    /// and must not be searched for it.</summary>
    public bool UploadsPaused => _queue.AuthPaused;

    /// <summary>Test seam: the handler behind each client
    /// <see cref="Configure"/> builds (one per call — a client disposes its
    /// handler). Null in the app = the real network stack.</summary>
    internal Func<HttpMessageHandler>? HandlerFactory { get; init; }

    public UploadService()
    {
        // The queue lives in Core and speaks English on its own; here it
        // gets the app's translated lines (UploadQ_* keys).
        _queue = new UploadQueue(SendAsync, ProbeProvenance) { Messages = LocalizedUploadMessages.Instance };
        _queue.StatusChanged += () => Set(_queue.Status);
    }

    /// <summary>Log-writer provenance for an auto upload: who holds the log
    /// file right now (seconds after the fight ended)? EverQuest2 among the
    /// holders → the positive live-log stamp; see LogProvenance.</summary>
    private static List<string>? ProbeProvenance(Encounter encounter) =>
        OperatingSystem.IsWindows()
            ? LogProvenance.BuildWarnings(
                Core.Logs.LogFileHolders.Probe(encounter.SourceId), Environment.ProcessId)
            : null;

    /// <summary>Last event line — the Settings card shows it.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Raised (on a background thread) whenever Status changes.</summary>
    public event Action? StatusChanged;

    /// <summary>A usable client exists (token saved + acceptable URL).</summary>
    public bool Configured => _client is not null;

    /// <summary>Auto-upload is switched on AND configured.</summary>
    public bool Active => _enabled && Configured;

    private void Set(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    /// <summary>The queue's transport. With no client (no saved token, or a
    /// site address the client refuses) nothing is sent and the outcome
    /// says so as itself — <see cref="UploadResult.NotConfigured"/> — not
    /// as status 0, which the queue reads as a network error and retries
    /// through its 5 s and 30 s waits for every fight still queued.</summary>
    private Task<UploadResult> SendAsync(LexiconPayload payload, CancellationToken ct) =>
        _client is { } client
            ? client.UploadAsync(payload, ct)
            : Task.FromResult(UploadResult.NoClient(_noClientReason));

    public void Configure(string baseUrl, string? apiToken, bool enabled, bool raidOnly = false)
    {
        _enabled = enabled;
        _raidOnly = raidOnly;
        if (enabled && raidOnly && RaidBosses is { } bosses)
            _ = bosses.EnsureFreshAsync(DateTimeOffset.Now);

        LexiconUploadClient? client = null;
        var noClientReason = "";
        var noClientStatus = "";
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            noClientReason = Loc.Get("UploadSvc_NoTokenConfigured");
            noClientStatus = enabled ? Loc.Get("UploadSvc_NoTokenSaveBelow") : "";
        }
        else if (LexiconUploadClient.UrlProblem(baseUrl) is { } problem)
        {
            noClientReason = problem;
            noClientStatus = problem;
        }
        else
        {
            client = new LexiconUploadClient(baseUrl, apiToken, HandlerFactory?.Invoke());
        }

        // The old client is dropped, not disposed — an in-flight send may
        // still hold it, and the queue treats a disposed-client throw as a
        // plain failure anyway. Reconfigures are rare; the GC collects it.
        // One assignment: a send on the drain thread must never meet a
        // null that only exists halfway through a token change, because a
        // null now drops the fight instead of retrying it. The reason is
        // written first, and only when there is no client, so a send that
        // finds no client always finds the reason that goes with it.
        if (client is null)
            _noClientReason = noClientReason;
        _client = client;
        // AFTER the swap (UploadQueue.ConfigurationChanged says why): a new
        // generation, so whatever a send still in flight on the old client
        // comes back with cannot pause the queue or mark a server refused
        // against the new token or site. It also clears both. A "server
        // not allowed" answer and a pause belong to the site and account
        // they came from, and Configure cannot tell a changed token or URL
        // from a flipped toggle (it never keeps the token to compare), so
        // every reconfigure forgets — the cost of forgetting too often is
        // one refused request, then the skip or the pause resumes.
        _queue.ConfigurationChanged();

        if (client is null)
        {
            SetAttendanceAccess(null);
            Set(noClientStatus);
            return;
        }
        // Raid-tab entitlement check — regardless of the auto-upload toggle
        // (the tab's local features don't need uploads on).
        _lastAccessProbe = DateTimeOffset.Now;
        ProbeAttendanceAccess(client);
        if (enabled)
            Set(Loc.Get("UploadSvc_Ready"));
    }

    /// <summary>Engine EncounterEnded handler — never blocks (pump thread,
    /// inside the global sync lock).</summary>
    private DateTimeOffset _lastAttendanceSend = DateTimeOffset.MinValue;
    private static readonly TimeSpan AttendanceInterval = TimeSpan.FromMinutes(5);

    /// <summary>Best-effort {character → raid main} map from the site,
    /// refreshed alongside the attendance tick. Null until the first
    /// successful fetch — the DKP builder falls back to the bulk raid grant.
    /// Case-insensitive keys.</summary>
    public IReadOnlyDictionary<string, string>? RaidMains { get; private set; }

    /// <summary>Does the configured token's account hold the site's
    /// attendance-preview entitlement ('subscriber' role or admin)? Drives
    /// the Raid tab's visibility. null = unknown (no token / probe not yet
    /// answered / network trouble) — treated as no access, re-probed
    /// periodically from the shell tick.</summary>
    public bool? AttendanceAccess { get; private set; }

    /// <summary>Raised (possibly on a background thread) when
    /// <see cref="AttendanceAccess"/> changes.</summary>
    public event Action? AttendanceAccessChanged;

    private DateTimeOffset _lastAccessProbe = DateTimeOffset.MinValue;
    private static readonly TimeSpan AccessProbeRetry = TimeSpan.FromMinutes(2);

    private void SetAttendanceAccess(bool? value)
    {
        if (AttendanceAccess == value)
            return;
        AttendanceAccess = value;
        AttendanceAccessChanged?.Invoke();
    }

    private void ProbeAttendanceAccess(LexiconUploadClient client)
    {
        _ = Task.Run(async () =>
        {
            var result = await client.WhoAmIAsync().ConfigureAwait(false);
            if (!ReferenceEquals(_client, client))
                return; // reconfigured while the probe was in flight
            // Auth rejections are a definitive no; network trouble stays
            // unknown so the tick retries.
            SetAttendanceAccess(result.Success
                ? LexiconUploadClient.ParseAttendanceAccess(result.Body)
                : result.StatusCode == 0 ? null : false);
        });
    }

    /// <summary>Periodic attendance snapshot upload — call from the shell
    /// tick (cheap no-op most ticks). Dark until the server grows the
    /// /api/attendance/ingest endpoint: every failure (404 included) is
    /// swallowed quietly, the roster tab works fine without it. Sends only
    /// while a session has at least one raid member and uploads are on.
    /// The raid-mains map rides the same cadence (one extra GET / 5 min).</summary>
    public void TickAttendance(SourceManager manager, DateTimeOffset now)
    {
        // Raid-boss list refresh rides the same tick (cheap no-op until
        // RefreshInterval elapses; only while the raid-only rule is in use).
        if (Active && _raidOnly && RaidBosses is { } bosses)
            _ = bosses.EnsureFreshAsync(now);
        // Entitlement unknown (e.g. offline at startup) → keep re-probing so
        // the Raid tab appears once the site answers.
        if (_client is { } probeClient && AttendanceAccess is null && now - _lastAccessProbe > AccessProbeRetry)
        {
            _lastAccessProbe = now;
            ProbeAttendanceAccess(probeClient);
        }
        if (AttendanceAccess != true)
            return; // no entitlement — uploads would 403; stay silent
        if (!Active || _client is not { } client || now - _lastAttendanceSend < AttendanceInterval)
            return;
        // Only a tracker ARMED by a validated roster-macro pair may report —
        // unarmed state can't accrue members, but keep the gate explicit so
        // a future tracker change can't silently turn passive noise (fight
        // allies, stray whos) back into attendance sessions.
        if (!manager.RaidRoster.Armed)
            return;
        var snapshot = manager.RaidRoster.Snapshot();
        if (!snapshot.Any(m => m.InRaid))
            return;
        var source = manager.Sources.Count > 0 ? manager.Sources[0] : null;
        if (source is null)
            return;
        if (BuildAttendancePayload(source.Owner, source.Path, now, snapshot) is not { } payload)
            return; // no usable server folder — see BuildAttendancePayload; stays quiet like every attendance failure
        _lastAttendanceSend = now;
        _ = Task.Run(() => client.UploadAttendanceAsync(payload));
        _ = Task.Run(async () =>
        {
            // A stale map beats no map — only overwrite on success.
            if (await client.FetchRaidMainsAsync(payload.LoggerName, payload.LoggerServer).ConfigureAwait(false) is { } mains)
                RaidMains = mains;
        });
    }

    /// <summary>The snapshot payload for one tick, or null when the logger's
    /// log is not inside a logs/&lt;server&gt;/ folder
    /// (<see cref="LogPaths.ParseServerName"/> == "") or that folder's name
    /// is not a server name the site accepts
    /// (<see cref="LogPaths.IsServerNameAccepted"/>). Both requests the
    /// tick makes need the server — the attendance ingest and the raid-mains
    /// lookup each answer 400 to a missing or malformed one — so there is
    /// nothing to send, and the tick must not spend its five-minute slot on
    /// a request that can only be refused.</summary>
    internal static AttendancePayload? BuildAttendancePayload(
        string owner, string logPath, DateTimeOffset now, IReadOnlyList<Core.Raid.RaidMemberState> snapshot)
    {
        var server = LogPaths.ParseServerName(logPath);
        if (!LogPaths.IsServerNameAccepted(server))
            return null;
        return new AttendancePayload
        {
            LoggerName = owner,
            LoggerServer = server,
            SentAt = now.ToUnixTimeSeconds(),
            RaidMembers = [.. snapshot.Where(m => m.InRaid).Select(ToMember)],
            OnlineGuildies = [.. snapshot.Where(m => m is { InRaid: false, Online: true }).Select(ToMember)],
        };
    }

    private static AttendanceMember ToMember(Core.Raid.RaidMemberState m) => new()
    {
        Name = m.Name,
        FirstSeen = (m.RaidFirstSeen ?? m.OnlineFirstSeen ?? DateTimeOffset.MinValue).ToUnixTimeSeconds(),
        LastSeen = (m.RaidLastSeen ?? m.OnlineLastSeen ?? DateTimeOffset.MinValue).ToUnixTimeSeconds(),
    };

    public void OnEncounterEnded(Encounter encounter)
    {
        if (!ShouldAutoUpload(encounter))
            return;
        _queue.Enqueue(encounter, LogPaths.ParseServerName(encounter.SourceId));
    }

    /// <summary>The auto-upload gate: on + configured, the log sits in a
    /// folder the site would accept as a server name, and (when raid-only
    /// is set) a raid fight per <see cref="RaidUploadFilter"/>. A skipped
    /// fight is counted and named
    /// in the status line so an empty parses page is explainable from the
    /// Settings card. Pump thread, under the sync lock — the classifier
    /// walk is the same cost attendance pays.</summary>
    internal bool ShouldAutoUpload(Encounter encounter)
    {
        if (!Active)
            return false;
        // A log read straight from <install>/logs/ (the legacy layout) has
        // no server folder, so logger_server would be "" — and the site
        // answers 400 to that, every time: it has no default world. A log
        // in a folder whose name is not shaped like a server name ("2nd
        // account", a name past 31 characters) gets a 400 "malformed" just
        // as surely. Sending would be one doomed request per fight, so skip
        // and say what to fix. Checked before the raid-only rule: NOTHING
        // from this log can upload, and that is the reason worth showing.
        // A placeholder-titled fight is never uploaded anyway (Enqueue
        // drops it), so it is not counted as skipped for this either.
        if (encounter.Title != Encounter.PlaceholderTitle)
        {
            var server = LogPaths.ParseServerName(encounter.SourceId);
            if (ServerFolderProblem(encounter.Title, server) is { } problem)
            {
                if (server.Length == 0)
                    SkippedNoServer++;
                else
                    SkippedServerName++;
                Set(problem);
                return false;
            }
        }
        if (!_raidOnly || encounter.Title == Encounter.PlaceholderTitle)
            return true;
        var players = PlayerCounter?.Invoke(encounter) ?? 0;
        if (RaidUploadFilter.IsRaidEncounter(encounter.Title, players, RaidBosses?.Bosses))
            return true;
        SkippedNonRaid++;
        Set(Loc.Format("UploadSvc_SkippedNonRaid", encounter.Title, players));
        return false;
    }

    /// <summary>Fight-tree manual upload: every source's view of the fight,
    /// exactly what auto-upload would have sent (the site mirror-groups them
    /// and keeps the longest as primary). Works with the auto toggle off —
    /// right-clicking Upload IS the consent — and clears a bad-token pause
    /// (and the refused-server memory), since the explicit action is the
    /// retry. A source whose log is not in a server folder, or is in a
    /// folder the site would not take as a server name, is refused HERE
    /// with the reason: consent does not make the request able to succeed
    /// (the site answers 400 to both). When that leaves nothing to
    /// send, nothing is retried, so a pause is left exactly as it was.</summary>
    public void UploadFight(IReadOnlyList<Encounter> sources)
    {
        if (!Configured)
        {
            Set(Loc.Get("UploadSvc_AddTokenInSettings"));
            return;
        }
        List<(Encounter Encounter, string Server)> sendable = new(sources.Count);
        string? refusal = null;
        foreach (var encounter in sources)
        {
            var server = LogPaths.ParseServerName(encounter.SourceId);
            if (ServerFolderProblem(encounter.Title, server) is { } problem)
                refusal ??= problem;
            else
                sendable.Add((encounter, server));
        }
        if (refusal is not null)
            Set(refusal);
        if (sendable.Count == 0)
            return;
        _queue.ResetAuthPause();
        _queue.ForgetRefusedServers();
        // No provenance on manual uploads: the fight may have ended long
        // ago, and probing the log NOW says nothing about who wrote it THEN.
        foreach (var (encounter, server) in sendable)
            _queue.Enqueue(encounter, server, withProvenance: false);
    }

    /// <summary>Why nothing from a log in this folder can be uploaded, as
    /// the status line for the fight titled <paramref name="title"/>; null
    /// when the folder is a usable server name. <paramref name="server"/>
    /// is <see cref="LogPaths.ParseServerName"/> of the log: "" means the
    /// log is not in a server folder at all, anything else that fails
    /// <see cref="LogPaths.IsServerNameAccepted"/> is a folder the site
    /// would answer 400 "malformed" to. One place, so the automatic and the
    /// manual path give the same reason.</summary>
    private static string? ServerFolderProblem(string title, string server)
    {
        if (server.Length == 0)
            return Loc.Format("UploadSvc_NoServerFolder", title);
        return LogPaths.IsServerNameAccepted(server)
            ? null
            : Loc.Format("UploadQ_ServerFolderName", title, Shortened(server));
    }

    /// <summary>A folder name for the status line: it can be as long as the
    /// file system allows, and the line has to stay readable.</summary>
    private static string Shortened(string name) => name.Length <= 40 ? name : name[..40] + "…";

    /// <summary>The upload queue's status lines in the interface language:
    /// one <c>UploadQ_*</c> key per line of
    /// <see cref="UploadQueueMessages"/>, same arguments in the same order.
    /// The site's own text (a <c>detail</c>, a status word) goes in as it
    /// arrived. <c>Loc.Format</c> falls back to English and never throws, so
    /// no line here can fail on the queue's drain thread.</summary>
    internal sealed class LocalizedUploadMessages : UploadQueueMessages
    {
        public static LocalizedUploadMessages Instance { get; } = new();

        public override string Uploaded(string title, string serverStatus, int uploadedThisSession) =>
            Loc.Format("UploadQ_Uploaded", title, serverStatus, uploadedThisSession);

        public override string Retrying(string title, string reason) =>
            Loc.Format("UploadQ_Retrying", title, reason);

        public override string Failed(string title, string reason) =>
            Loc.Format("UploadQ_Failed", title, reason);

        public override string Refused(string title, string detail) =>
            Loc.Format("UploadQ_Refused", title, detail);

        public override string PausedTokenRejected(int statusCode) =>
            Loc.Format("UploadQ_PausedTokenRejected", statusCode);

        public override string PausedRefused(int statusCode) =>
            Loc.Format("UploadQ_PausedRefused", statusCode);

        public override string PausedRefusedWithDetail(int statusCode, string detail) =>
            Loc.Format("UploadQ_PausedRefusedWithDetail", statusCode, detail);

        public override string SkippedRefusedServer(string title, string server, int skippedThisSession) =>
            Loc.Format("UploadQ_SkippedRefusedServer", title, server, skippedThisSession);

        public override string RateLimitWait(string title, int seconds, int attemptsUsed, int attemptsAllowed) =>
            Loc.Format("UploadQ_RateLimitWait", title, seconds, attemptsUsed, attemptsAllowed);

        public override string BuildFailed(string title, string problem) =>
            Loc.Format("UploadQ_BuildFailed", title, problem);

        public override string NotConfigured(string title, string reason, int droppedThisSession) =>
            Loc.Format("UploadQ_NotConfigured", title, reason, droppedThisSession);
    }

    /// <summary>Settings "Test" button — verifies the token against
    /// /api/auth/whoami and reports who it belongs to.</summary>
    public async Task<string> TestTokenAsync()
    {
        if (_client is not { } client)
            return Loc.Get("UploadSvc_NoTokenSaved");
        var result = await client.WhoAmIAsync().ConfigureAwait(false);
        if (!result.Success)
        {
            if (result.StatusCode != 0)
                SetAttendanceAccess(false); // definitive rejection, not a network blip
            return result.StatusCode == 0
                ? result.Message
                : Loc.Format("UploadSvc_TokenRejected", result.StatusCode, result.Message);
        }
        _queue.ResetAuthPause();
        SetAttendanceAccess(LexiconUploadClient.ParseAttendanceAccess(result.Body));
        var name = ExtractDiscordName(result.Body);
        return name is null ? Loc.Get("UploadSvc_TokenOk") : Loc.Format("UploadSvc_TokenOkSignedIn", name);
    }

    private static string? ExtractDiscordName(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("discord_name", out var value)
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _queue.Dispose();
        _client?.Dispose();
    }
}
