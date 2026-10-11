using System.IO;
using System.Net.Http;
using System.Text.Json;
using EQ2Parser.App.Localization;
using EQ2Parser.Core.Persistence;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>
/// Pulls the curated trigger/timer library from EQ2Lexicon
/// (GET /api/act/pack) and feeds it into the trigger + timer services as
/// read-only "lexicon"-sourced definitions.
///
/// Sync shape: the cached pack (%LocalAppData%\EQ2Parser\lexicon_pack.json)
/// applies instantly at startup, then a background fetch replaces it when
/// the server's version stamp differs. The user's own enable/disable flips
/// on lexicon rows persist as overrides (lexicon_overrides.json) and
/// re-apply across syncs; everything else about a lexicon row is
/// curator-owned and replaced wholesale.
/// </summary>
public sealed class LexiconSyncService
{
    private const string SourceTag = "lexicon";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions PackJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly TriggerService _triggers;
    private readonly TimerService _timers;
    private readonly object _gate = new();
    private readonly HashSet<string> _disabledTriggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabledTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledTriggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledTimers = new(StringComparer.OrdinalIgnoreCase);
    private string _appliedVersion = "";
    private string _appliedSummary = "";
    private bool _enabled;

    public string BaseUrl { get; }

    /// <summary>Whether the curated Lexicon library is subscribed. When false,
    /// no sync runs and no lexicon-sourced rows live in the engines.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    private string? _status;

    /// <summary>Lazy default: the not-synced-yet line resolves at read
    /// time rather than at construction. The app builds this service
    /// after Loc.Initialize, but tests construct it without ever calling
    /// Loc.Initialize.</summary>
    public string Status => _status ?? Loc.Get("LexiconSvc_NotSyncedYet");

    /// <summary>Raised (on whatever thread) whenever Status changes.</summary>
    public event Action? StatusChanged;

    public LexiconSyncService(TriggerService triggers, TimerService timers, string baseUrl, bool enabled = true)
        : this(triggers, timers, baseUrl, SharedHttp, enabled)
    {
    }

    /// <summary>Test seam: inject the HTTP transport (App.Tests drives the
    /// sync flow against a canned pack without a network).</summary>
    internal LexiconSyncService(TriggerService triggers, TimerService timers, string baseUrl, HttpClient http, bool enabled = true)
    {
        _triggers = triggers;
        _timers = timers;
        _http = http;
        _enabled = enabled;
        BaseUrl = baseUrl.TrimEnd('/');
        LoadOverrides();
        triggers.LexiconEnabledChanged += (key, enabled) => SetOverride(_disabledTriggers, _enabledTriggers, key, enabled);
        timers.LexiconEnabledChanged += (key, enabled) => SetOverride(_disabledTimers, _enabledTimers, key, enabled);
    }

    // ---- pack DTOs (snake_case JSON from the FastAPI models) ----

    // Every string is nullable: System.Text.Json binds an explicit null
    // into a non-nullable string without complaint, and the NRE then fired
    // LATER on the log pump thread (killing that tail loop). Nulls are
    // coalesced or skipped at the mapping instead.
    private sealed record PackTrigger(
        string? Regex,
        string? SoundData,
        int SoundType,
        bool CategoryRestrict,
        string? Category,
        bool Timer,
        string? TimerName,
        bool Active,
        double CooldownSeconds);

    private sealed record PackTimer(
        string? Name,
        bool Checked,
        int TimerDurationS,
        int WarningValue,
        int RemoveValue,
        bool OnlyMasterTicks,
        bool Restrict,
        bool Absolute,
        string? StartWav,
        string? WarningWav,
        bool RadialDisplay,
        bool Modable,
        string? Tooltip,
        int FillColor,
        bool Panel1,
        bool Panel2,
        string? Category,
        bool RestrictCategory,
        string? DamageType,
        string? ControlEffect);

    private sealed record PackEncounter(string Mob, int Position, List<PackTrigger> Triggers, List<PackTimer> SpellTimers);

    private sealed record PackZone(string Zone, string Expansion, List<PackEncounter> Encounters);

    private sealed record PackRoot(string Version, List<PackZone> Zones);

    // ---- sync ----

    /// <summary>Startup: apply the cached pack immediately (offline-safe),
    /// then fetch in the background. Never throws.</summary>
    public async Task StartupAsync()
    {
        if (!Enabled)
        {
            SetStatus(Loc.Get("LexiconSvc_Disabled"));
            return;
        }
        try
        {
            // A corrupt cache is quarantined and never blocks startup — the
            // fetch below replaces it.
            Apply(PersistedJsonFile.Load<PackRoot?>(PackPath, static () => null, PackJson), cached: true);
        }
        catch (Exception)
        {
            // Applying a stale pack shape must not block startup either.
        }
        await SyncAsync();
    }

    /// <summary>Turn the Lexicon library on or off at runtime (the Settings
    /// toggle). Off: every lexicon-sourced trigger/timer is removed from the
    /// engines (the user's own are untouched) and syncing stops. On: the
    /// cached pack re-applies immediately, then a background fetch refreshes
    /// it. Never throws. A no-op if the state is unchanged.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled)
                return;
            _enabled = enabled;
        }
        if (enabled)
        {
            await StartupAsync().ConfigureAwait(false);
        }
        else
        {
            ClearLexicon();
            SetStatus(Loc.Get("LexiconSvc_Disabled"));
        }
    }

    /// <summary>Drop every lexicon-sourced row from both engines (apply an
    /// empty set — ApplyLexicon replaces the lexicon set wholesale) and forget
    /// the applied version so re-enabling re-applies from cache.</summary>
    private void ClearLexicon()
    {
        lock (_gate)
        {
            _timers.ApplyLexicon([],
                _disabledTimers.ToHashSet(StringComparer.OrdinalIgnoreCase),
                _enabledTimers.ToHashSet(StringComparer.OrdinalIgnoreCase));
            _triggers.ApplyLexicon([],
                _disabledTriggers.ToHashSet(StringComparer.OrdinalIgnoreCase),
                _enabledTriggers.ToHashSet(StringComparer.OrdinalIgnoreCase));
            _appliedVersion = "";
            _appliedSummary = "";
        }
    }

    /// <summary>Fetch the pack and apply it if the version moved. Safe to
    /// call any time (the Settings "Sync now" button). Never throws.</summary>
    public async Task SyncAsync()
    {
        if (!Enabled)
        {
            SetStatus(Loc.Get("LexiconSvc_Disabled"));
            return;
        }
        try
        {
            SetStatus(Loc.Get("LexiconSvc_Syncing"));
            var json = await _http.GetStringAsync($"{BaseUrl}/api/act/pack").ConfigureAwait(false);
            var pack = JsonSerializer.Deserialize<PackRoot>(json, PackJson);
            if (pack is null)
            {
                SetStatus(Loc.Get("LexiconSvc_SyncFailedEmpty"));
                return;
            }
            if (_appliedVersion.Length > 0 && pack.Version == _appliedVersion)
            {
                // Already applied (from cache or a prior sync) — a full
                // re-apply here was pure churn through every engine.
                SetStatus(Loc.Format("LexiconSvc_StatusUpToDate", _appliedSummary, pack.Version));
                return;
            }
            // Cache the server's bytes verbatim, atomically — a crash
            // mid-write must never leave a torn pack for the next startup.
            PersistedJsonFile.SaveText(PackPath, json);
            Apply(pack, cached: false);
        }
        catch (Exception ex)
        {
            SetStatus(Loc.Format("LexiconSvc_SyncFailed", ex.Message));
        }
    }

    private void Apply(PackRoot? pack, bool cached)
    {
        if (pack is null)
            return;
        List<Trigger> triggers = [];
        List<TimerDefinition> timers = [];
        foreach (var zone in pack.Zones)
        {
            foreach (var encounter in zone.Encounters)
            {
                foreach (var t in encounter.Triggers)
                {
                    if (string.IsNullOrEmpty(t.Regex))
                        continue; // a null/empty regex would match EVERY line
                    try
                    {
                        triggers.Add(new Trigger(t.Regex, t.Category ?? encounter.Mob, zone.Zone)
                        {
                            Enabled = t.Active,
                            SoundType = Enum.IsDefined((TriggerSound)t.SoundType) ? (TriggerSound)t.SoundType : TriggerSound.None,
                            SoundData = t.SoundData ?? "",
                            RestrictToCategoryZone = t.CategoryRestrict,
                            StartsTimer = t.Timer && !string.IsNullOrEmpty(t.TimerName),
                            TimerName = t.TimerName ?? "",
                            AudioCooldown = TimeSpan.FromSeconds(Math.Clamp(t.CooldownSeconds, 0, 3600)),
                            Source = SourceTag,
                        });
                    }
                    catch (ArgumentException)
                    {
                        // A curated regex that doesn't compile is skipped, not fatal.
                    }
                }
                foreach (var t in encounter.SpellTimers)
                {
                    if (string.IsNullOrEmpty(t.Name))
                        continue;
                    // The site's editor expresses EVERY field since the
                    // 2026-08 parity release (plus a one-time backfill of
                    // the rows its older editor stripped) — curated values
                    // flow through verbatim.
                    timers.Add(new TimerDefinition
                    {
                        Name = t.Name,
                        Category = t.Category ?? encounter.Mob,
                        Zone = zone.Zone,
                        Enabled = t.Checked,
                        DurationSeconds = t.TimerDurationS,
                        WarningSeconds = t.WarningValue,
                        RemoveSeconds = t.RemoveValue,
                        OnlyMasterTicks = t.OnlyMasterTicks,
                        RestrictToMe = t.Restrict,
                        AbsoluteTiming = t.Absolute,
                        StartSoundData = t.StartWav ?? "",
                        WarningSoundData = t.WarningWav ?? "",
                        RadialDisplay = t.RadialDisplay,
                        Modable = t.Modable,
                        Tooltip = t.Tooltip ?? "",
                        FillColorArgb = t.FillColor,
                        Panel1 = t.Panel1,
                        Panel2 = t.Panel2,
                        RestrictToCategory = t.RestrictCategory,
                        DamageType = t.DamageType ?? "",
                        ControlEffect = t.ControlEffect ?? "",
                        Source = SourceTag,
                    });
                }
            }
        }

        lock (_gate)
        {
            _timers.ApplyLexicon(timers,
                _disabledTimers.ToHashSet(StringComparer.OrdinalIgnoreCase),
                _enabledTimers.ToHashSet(StringComparer.OrdinalIgnoreCase));
            _triggers.ApplyLexicon(triggers,
                _disabledTriggers.ToHashSet(StringComparer.OrdinalIgnoreCase),
                _enabledTriggers.ToHashSet(StringComparer.OrdinalIgnoreCase));
            _appliedVersion = pack.Version;
            _appliedSummary = Loc.Format("LexiconSvc_AppliedSummary", triggers.Count, timers.Count, pack.Zones.Count);
        }
        SetStatus(cached
            ? Loc.Format("LexiconSvc_StatusCached", _appliedSummary)
            : Loc.Format("LexiconSvc_StatusSynced", _appliedSummary, pack.Version));
    }

    private void SetStatus(string status)
    {
        _status = status;
        StatusChanged?.Invoke();
    }

    // ---- enable/disable overrides ----

    /// <summary>Record the user's explicit choice in BOTH directions — the
    /// class contract says flips re-apply across syncs, but only disables
    /// used to be stored, so enabling a curator-disabled row reverted on
    /// the next sync or restart.</summary>
    private void SetOverride(HashSet<string> disabled, HashSet<string> enabled, string key, bool nowEnabled)
    {
        lock (_gate)
        {
            if (nowEnabled)
            {
                disabled.Remove(key);
                enabled.Add(key);
            }
            else
            {
                enabled.Remove(key);
                disabled.Add(key);
            }
            SaveOverrides();
        }
    }

    /// <summary>The enabled lists are nullable so pre-existing override
    /// files (disables only) still deserialize.</summary>
    private sealed record Overrides(
        List<string> DisabledTriggers, List<string> DisabledTimers,
        List<string>? EnabledTriggers = null, List<string>? EnabledTimers = null);

    private static string PackPath => Path.Combine(AppSettings.Directory, "lexicon_pack.json");

    private static string OverridesPath => Path.Combine(AppSettings.Directory, "lexicon_overrides.json");

    private void LoadOverrides()
    {
        // Corrupt overrides quarantine (evidence survives for recovery) and
        // degrade to everything-enabled.
        var overrides = PersistedJsonFile.Load<Overrides?>(OverridesPath, static () => null);
        foreach (var key in overrides?.DisabledTriggers ?? [])
            _disabledTriggers.Add(key);
        foreach (var key in overrides?.DisabledTimers ?? [])
            _disabledTimers.Add(key);
        foreach (var key in overrides?.EnabledTriggers ?? [])
            _enabledTriggers.Add(key);
        foreach (var key in overrides?.EnabledTimers ?? [])
            _enabledTimers.Add(key);
    }

    private void SaveOverrides()
    {
        try
        {
            PersistedJsonFile.Save(OverridesPath, new Overrides(
                [.. _disabledTriggers], [.. _disabledTimers],
                [.. _enabledTriggers], [.. _enabledTimers]));
        }
        catch (Exception)
        {
            // Best-effort — a failed save loses overrides on next sync only.
        }
    }
}
