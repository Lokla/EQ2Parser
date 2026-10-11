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
    private LexiconUploadClient? _client;
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

    /// <summary>Fights the queue did not send this session because the site
    /// had already refused their server as not on its allow list — see
    /// <see cref="UploadQueue.SkippedRefusedServer"/>.</summary>
    public int SkippedRefusedServer => _queue.SkippedRefusedServer;

    /// <summary>Test seam: the handler behind each client
    /// <see cref="Configure"/> builds (one per call — a client disposes its
    /// handler). Null in the app = the real network stack.</summary>
    internal Func<HttpMessageHandler>? HandlerFactory { get; init; }

    public UploadService()
    {
        _queue = new UploadQueue(SendAsync, ProbeProvenance);
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

    private Task<UploadResult> SendAsync(LexiconPayload payload, CancellationToken ct) =>
        _client is { } client
            ? client.UploadAsync(payload, ct)
            : Task.FromResult(new UploadResult(false, 0, Loc.Get("UploadSvc_NoTokenConfigured")));

    public void Configure(string baseUrl, string? apiToken, bool enabled, bool raidOnly = false)
    {
        _enabled = enabled;
        _raidOnly = raidOnly;
        if (enabled && raidOnly && RaidBosses is { } bosses)
            _ = bosses.EnsureFreshAsync(DateTimeOffset.Now);
        // The old client is dropped, not disposed — an in-flight send may
        // still hold it, and the queue treats a disposed-client throw as a
        // plain failure anyway. Reconfigures are rare; the GC collects it.
        _client = null;
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            SetAttendanceAccess(null);
            Set(enabled ? Loc.Get("UploadSvc_NoTokenSaveBelow") : "");
            return;
        }
        if (LexiconUploadClient.UrlProblem(baseUrl) is { } problem)
        {
            SetAttendanceAccess(null);
            Set(problem);
            return;
        }
        _client = new LexiconUploadClient(baseUrl, apiToken, HandlerFactory?.Invoke());
        _queue.ResetAuthPause();
        // A "server not allowed" answer belongs to the site and account it
        // came from. Configure cannot tell a changed token or URL from a
        // flipped toggle (it never keeps the token to compare), so every
        // reconfigure forgets — the cost of forgetting too often is one
        // refused request per such server, then the skip resumes.
        _queue.ForgetRefusedServers();
        // Raid-tab entitlement check — regardless of the auto-upload toggle
        // (the tab's local features don't need uploads on).
        _lastAccessProbe = DateTimeOffset.Now;
        ProbeAttendanceAccess(_client);
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
            return; // no server folder — see BuildAttendancePayload; stays quiet like every attendance failure
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
    /// (<see cref="LogPaths.ParseServerName"/> == ""). Both requests the
    /// tick makes need the server — the attendance ingest and the raid-mains
    /// lookup each answer 400 without one — so there is nothing to send, and
    /// the tick must not spend its five-minute slot on a request that can
    /// only be refused.</summary>
    internal static AttendancePayload? BuildAttendancePayload(
        string owner, string logPath, DateTimeOffset now, IReadOnlyList<Core.Raid.RaidMemberState> snapshot)
    {
        var server = LogPaths.ParseServerName(logPath);
        if (server.Length == 0)
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
    /// server folder, and (when raid-only is set) a raid fight per
    /// <see cref="RaidUploadFilter"/>. A skipped fight is counted and named
    /// in the status line so an empty parses page is explainable from the
    /// Settings card. Pump thread, under the sync lock — the classifier
    /// walk is the same cost attendance pays.</summary>
    internal bool ShouldAutoUpload(Encounter encounter)
    {
        if (!Active)
            return false;
        // A log read straight from <install>/logs/ (the legacy layout) has
        // no server folder, so logger_server would be "" — and the site
        // answers 400 to that, every time: it has no default world. Sending
        // would be one doomed request per fight, so skip and say what to
        // fix. Checked before the raid-only rule: with no server NOTHING
        // from this log can upload, and that is the reason worth showing.
        // A placeholder-titled fight is never uploaded anyway (Enqueue
        // drops it), so it is not counted as skipped for this either.
        if (encounter.Title != Encounter.PlaceholderTitle && LogPaths.ParseServerName(encounter.SourceId).Length == 0)
        {
            SkippedNoServer++;
            Set(Loc.Format("UploadSvc_NoServerFolder", encounter.Title));
            return false;
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
    /// retry. A source whose log is not in a server folder is refused HERE
    /// with the reason: consent does not make the request able to succeed
    /// (the site answers 400 without a server). When that leaves nothing to
    /// send, nothing is retried, so a pause is left exactly as it was.</summary>
    public void UploadFight(IReadOnlyList<Encounter> sources)
    {
        if (!Configured)
        {
            Set(Loc.Get("UploadSvc_AddTokenInSettings"));
            return;
        }
        List<(Encounter Encounter, string Server)> sendable = new(sources.Count);
        string? noServerTitle = null;
        foreach (var encounter in sources)
        {
            var server = LogPaths.ParseServerName(encounter.SourceId);
            if (server.Length == 0)
                noServerTitle ??= encounter.Title;
            else
                sendable.Add((encounter, server));
        }
        if (noServerTitle is not null)
            Set(Loc.Format("UploadSvc_NoServerFolder", noServerTitle));
        if (sendable.Count == 0)
            return;
        _queue.ResetAuthPause();
        _queue.ForgetRefusedServers();
        // No provenance on manual uploads: the fight may have ended long
        // ago, and probing the log NOW says nothing about who wrote it THEN.
        foreach (var (encounter, server) in sendable)
            _queue.Enqueue(encounter, server, withProvenance: false);
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
