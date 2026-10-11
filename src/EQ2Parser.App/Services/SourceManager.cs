using EQ2Parser.App.Localization;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Services;

/// <summary>
/// The app's parsing backbone: the set of tailed log sources, the shared
/// encounter correlator, and the shared class/pet classifier. One global
/// sync object serializes every engine mutation and correlator callback;
/// UI snapshots take the same lock briefly.
/// </summary>
public sealed class SourceManager : IDisposable
{
    private readonly List<LogSource> _sources = [];

    /// <summary>Serializes Add end-to-end — the folder-watch thread and a
    /// UI add both used to check-then-act on the sources list, so the same
    /// path could attach twice.</summary>
    private readonly object _addGate = new();

    /// <summary>Paths the user removed THIS session — the folder watcher
    /// respects these instead of re-adding a growing log ~2s after its
    /// removal, and PersistSources drops their saved entries.</summary>
    private readonly HashSet<string> _removedPaths = new(StringComparer.OrdinalIgnoreCase);

    public object Sync { get; } = new();
    public EncounterCorrelator Correlator { get; } = new();
    public Core.Raid.RaidRosterTracker RaidRoster { get; } = new();

    /// <summary>Counts throttled award lines + the marker per DKP-macro
    /// press (see DkpCommandFile) — the Raid tab auto-advances its queue.</summary>
    public Core.Raid.DkpAwardProgress DkpProgress { get; } = new();

    private readonly Core.Raid.RaidLineDedup _raidDedup = new();
    private readonly Dictionary<LogSource, Action<string, DateTimeOffset>> _raidHandlers = [];

    /// <summary>Per-source raid hook: cross-source dedup in front of the
    /// three raid consumers, so a dual-boxer's second log can't deliver
    /// the same game event twice.</summary>
    private Action<string, DateTimeOffset> MakeRaidLineHandler(LogSource source) =>
        (message, time) =>
        {
            if (!_raidDedup.ShouldProcess(source, message, time))
                return;
            RaidRoster.OnLine(message, time);
            DkpProgress.OnLine(message, time);
            Loot.OnLine(message, time);
        };

    /// <summary>Raid loot accumulator (chest contents + who looted what) —
    /// feeds the Raid tab's loot list and the DKP purchase deductions.</summary>
    public Core.Raid.LootTracker Loot { get; } = new();

    /// <summary>Curated source corrections + an optional local hot-fix file
    /// (%LocalAppData%\EQ2Parser\source_overrides.json, rules win over the
    /// embedded set) so a mislabel is fixable without waiting on a release.</summary>
    public CombatantClassifier Classifier { get; }

    /// <summary>Why the local hot-fix file was ignored this session (it
    /// did not parse), or null when it loaded or is absent. A bad file
    /// never stops startup — MergeFile swallows it — but it used to do so
    /// without a word, leaving the user's edit mysteriously inert; the
    /// journal gets the reason at construction.</summary>
    public string? SourceOverridesError { get; }
    public AlertAudioService Audio { get; }
    public TriggerService Triggers { get; }
    public TimerService SpellTimers { get; }
    public LexiconSyncService Lexicon { get; }
    public RaidBossSyncService RaidBosses { get; }
    public StatusCalloutMonitor Callouts { get; } = new();
    public UndoService Undo { get; } = new();

    /// <summary>A mass-detriment callout was announced (already spoken) —
    /// the Notifications overlay toasts it too.</summary>
    public event Action<string>? CalloutAnnounced;

    /// <summary>Post a line to the Notifications overlay (same channel the
    /// callouts use) — trigger-share outcomes ride it too.</summary>
    public void AnnounceNotification(string text) => CalloutAnnounced?.Invoke(text);

    /// <summary>Chat trigger-share handler (dedupe + offer window).</summary>
    public SharedTriggerPrompter SharedTriggers { get; }
    public HistoryService History { get; } = new();
    public UpdateService Updates { get; } = new();
    public UploadService Uploads { get; } = new();
    public AppSettings Settings { get; set; }

    /// <summary>Settings come IN rather than being loaded here: the UI
    /// language lives in them, and the services built below resolve
    /// status strings as they are constructed (Uploads.Configure sets
    /// one). Taking them as a parameter makes the only possible order
    /// "load settings → Loc.Initialize → build the manager" — when this
    /// constructor loaded them itself, the dictionaries were still empty
    /// and the Settings page showed a raw key until the status next
    /// changed.</summary>
    public SourceManager(AppSettings settings)
    {
        Settings = settings;
        var overrides = SourceOverrides.LoadEmbedded()
            .MergeFile(System.IO.Path.Combine(AppSettings.Directory, "source_overrides.json"));
        Classifier = new CombatantClassifier(new ClassIdentifier(SpellClassMap.LoadEmbedded(), overrides));
        SourceOverridesError = overrides.LoadError;
        if (SourceOverridesError is { } overridesError)
            SessionJournal.Mark($"user override file IGNORED — {overridesError}");
        Audio = new AlertAudioService
        {
            Volume = Settings.AlertVolume,
            SpeakingRate = Settings.TtsRate,
            VoiceDepth = Settings.TtsDepth,
            VoiceId = Settings.TtsVoiceId,
        };
        Triggers = new TriggerService(Audio, Sync);
        SpellTimers = new TimerService(Audio, Sync);
        SharedTriggers = new SharedTriggerPrompter(this);
        Lexicon = new LexiconSyncService(Triggers, SpellTimers, Settings.LexiconBaseUrl, Settings.LexiconTriggersEnabled);
        RaidBosses = new RaidBossSyncService(Settings.LexiconBaseUrl);
        // Raid-only upload rule: classified player count (pets out) + the
        // synced boss list. Wired before Configure so a startup with the
        // rule on can kick the first sync.
        Uploads.PlayerCounter = encounter => Classifier.PlayerAllyNames(encounter).Count();
        Uploads.RaidBosses = RaidBosses;
        Callouts.MinVictims = Settings.CalloutMinPlayers;
        Callouts.Cooldown = TimeSpan.FromSeconds(Settings.CalloutCooldownSeconds);
        Callouts.Callout += (effect, count) =>
        {
            if (!Settings.CalloutsEnabled)
                return;
            var word = Vocabulary.CalloutWord(effect);
            var text = count == 1 ? $"1 player {word}" : $"{count} players {word}";
            Audio.Speak(text);
            CalloutAnnounced?.Invoke(text);
        };
        Uploads.Configure(
            Settings.LexiconBaseUrl,
            TokenProtector.Unprotect(Settings.LexiconApiTokenProtected),
            Settings.UploadEnabled,
            Settings.UploadRaidOnly);
        // Archive collapse: whenever a mirror joins a fight, the archive
        // keeps only the primary copy (fires under Sync — correlator events
        // are raised inside Accept on the pump/restore path).
        Correlator.Merged += OnFightMerged;
        // History views resync by polling Correlator.Version on the UI tick
        // — no event needed (the old HistoryChanged had zero subscribers).
    }

    // ── Fight consumers, each isolated ──────────────────────────────────────

    private int _consumerFaults;

    /// <summary>Everything that wants a finished fight, in the order the
    /// old multicast ran it (correlator first, then archive, upload, raid
    /// roster) — but each in its own guard. As chained delegates, the first
    /// consumer to throw starved every one after it: a correlator or
    /// archive fault cost the upload and the attendance feed as well, and
    /// the throw then surfaced on the log pump as a failed LINE. One
    /// handler for every source, so Remove can unsubscribe the same
    /// delegate Add subscribed. Fires on the pump thread under Sync.</summary>
    private void OnEncounterEnded(Core.Combat.Encounter encounter)
    {
        Isolated("correlator", encounter, Correlator.Accept);
        Isolated("archive save", encounter, History.QueueSave);
        Isolated("upload", encounter, Uploads.OnEncounterEnded);
        Isolated("raid roster", encounter, OnEncounterEndedFeedRaid);
    }

    /// <summary>The archive collapse does SQLite work inside the
    /// correlator's Merged event. Unguarded, a database error there
    /// escaped through Accept — on the pump that skipped the line, and on
    /// the startup restore path it would have failed the whole launch.</summary>
    private void OnFightMerged(Core.Correlation.CorrelatedEncounter fight) =>
        Isolated("archive collapse", fight, History.CollapseToPrimary);

    /// <summary>Run one consumer; a throw becomes a crash report and a
    /// journal line instead of propagating. The journal names the consumer
    /// and the exception type only. Throttled past the first ten: a
    /// consumer that is broken for good fails once per fight, and an
    /// import ends thousands of fights.</summary>
    private void Isolated<T>(string consumer, T argument, Action<T> action)
    {
        try
        {
            action(argument);
        }
        catch (Exception ex)
        {
            var faults = Interlocked.Increment(ref _consumerFaults);
            CrashLog.Report("fight-consumer", ex, $"Consumer: {consumer}");
            if (faults <= 10 || faults % 100 == 0)
                SessionJournal.Mark($"fight consumer FAILED: {consumer} ({ex.GetType().Name}, {faults} this session)");
        }
    }

    /// <summary>A trigger's regex ran past its match timeout and the engine
    /// switched it off for the session (per source — each log has its own
    /// engine, so this can fire once per log). Nobody was told: the alert
    /// just stopped. Journal it and toast it. The journal names the
    /// trigger by where it is filed; the pattern itself is added only for
    /// Lexicon-pack triggers (public, curated content — and the one thing
    /// a curator needs to fix it), never for one the user typed. Fires on
    /// the pump thread under Sync.</summary>
    private void OnTriggerTimedOut(Trigger trigger)
    {
        var filedUnder = trigger.Zone.Length > 0 ? $"{trigger.Zone} / {trigger.Category}" : trigger.Category;
        var pattern = trigger.Source.Length > 0
            ? $", pattern: {(trigger.RegexText.Length <= 200 ? trigger.RegexText : trigger.RegexText[..200] + "…")}"
            : "";
        SessionJournal.Mark(
            $"trigger DISABLED for this session — regex match timed out ({(trigger.Source.Length > 0 ? trigger.Source : "user")} trigger, filed under: {filedUnder}{pattern})");
        AnnounceNotification(Loc.Format("Sources_TriggerTooSlow", filedUnder));
    }

    public IReadOnlyList<LogSource> Sources
    {
        get
        {
            lock (Sync)
            {
                return [.. _sources];
            }
        }
    }

    private long _importBusyCheckedMs;
    private bool _importBusy;

    /// <summary>True while any source is chewing a big backlog (wipe +
    /// re-import, first parse-from-start). The meters pause and the tree
    /// throttles while this holds — replaying history through the live UI
    /// was slowing imports to a crawl. Cached ~500ms: every read stats the
    /// files, and the UI + overlay ticks read this 30+ times a second.</summary>
    public bool ImportBusy
    {
        get
        {
            var now = Environment.TickCount64;
            if (now - _importBusyCheckedMs < 500)
                return _importBusy;
            _importBusyCheckedMs = now;
            var busy = false;
            foreach (var source in Sources)
            {
                if (source.PendingBytes > 1_000_000)
                {
                    busy = true;
                    break;
                }
            }
            return _importBusy = busy;
        }
    }

    public LogSource Add(string path, bool parseFromStart, long? startOffset = null, bool autoDiscovered = false)
    {
        lock (_addGate)
        {
            lock (Sync)
            {
                // Path-unique, authoritative here (not at call sites).
                var existing = _sources.FirstOrDefault(s =>
                    string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    return existing;
                _removedPaths.Remove(path); // an explicit re-add clears the tombstone
            }
            var source = new LogSource(
                path, parseFromStart, Sync,
                new EngineOptions { IdleEndSeconds = Settings.IdleEndSeconds },
                TimeSpan.FromMilliseconds(Settings.PollMilliseconds),
                Triggers.CreateEngine(LogSource.DeriveOwner(path)),
                SpellTimers.Service,
                startOffset)
            {
                AutoDiscovered = autoDiscovered,
            };
            lock (Sync)
            {
                // The owner is registered for the correlator's merge rule;
                // its Accept runs from OnEncounterEnded with the other
                // consumers (each isolated) rather than as a subscriber of
                // its own.
                Correlator.RegisterOwner(source.Owner);
                source.Engine.EncounterEnded += OnEncounterEnded;
                if (source.TriggerEngine is { } triggerEngine)
                    triggerEngine.MatchTimedOut += OnTriggerTimedOut;
                source.Processor.StatusApplied += Callouts.OnStatusApplied;
                source.Processor.TriggerShared += SharedTriggers.OnShared;
                // One handler per source, routed through the cross-source
                // dedup: a dual-boxer's second log witnesses the same chest
                // blocks / loots lines / officer echoes, and feeding both
                // would double every raid event (and double-confirm DKP).
                var raidHandler = MakeRaidLineHandler(source);
                _raidHandlers[source] = raidHandler;
                source.Processor.RaidLine += raidHandler;
                _sources.Add(source);
            }
            // Pump only once fully wired — starting in the LogSource
            // constructor let a fast log's first lines process unattached.
            source.Start();
            SessionJournal.Mark($"source added: {System.IO.Path.GetFileName(path)}");
            return source;
        }
    }

    /// <summary>Add's exact inverse: unwire everything Add wired.</summary>
    public void Remove(LogSource source)
    {
        lock (Sync)
        {
            _sources.Remove(source);
            // The owner stays registered with the correlator, as it did
            // after Detach: history from that owner still needs the owner
            // rule for future merges.
            source.Engine.EncounterEnded -= OnEncounterEnded;
            if (source.TriggerEngine is { } triggerEngine)
                triggerEngine.MatchTimedOut -= OnTriggerTimedOut;
            source.Processor.StatusApplied -= Callouts.OnStatusApplied;
            source.Processor.TriggerShared -= SharedTriggers.OnShared;
            if (_raidHandlers.Remove(source, out var raidHandler))
                source.Processor.RaidLine -= raidHandler;
            _removedPaths.Add(source.Path);
        }
        if (source.TriggerEngine is { } engine)
            Triggers.RemoveEngine(engine);
        source.Dispose();
    }

    /// <summary>Feed a finished fight's player allies into the raid roster —
    /// catches raid members present before our own join (the raid deltas
    /// never enumerate them). CLASSIFIED first: raw allies include pets,
    /// and auto-named mage/necro pets are single words the tracker's
    /// name-shape filter can't tell from players — they were landing in
    /// attendance as raiders. Fires on the pump thread under Sync.</summary>
    private void OnEncounterEndedFeedRaid(Core.Combat.Encounter encounter)
    {
        RaidRoster.OnFightAllies(
            Classifier.PlayerAllyNames(encounter),
            encounter.EndTime);
    }

    /// <summary>Load past sessions' fights into the correlator — called at
    /// startup BEFORE the log tails start, so live fights land on top.</summary>
    public void RestoreHistory()
    {
        lock (Sync)
        {
            History.RestoreInto(Correlator, Settings.HistoryBossDays, Settings.HistoryTrashDays);
        }
    }

    public void RestoreFromSettings()
    {
        foreach (var saved in Settings.Sources)
        {
            // "Parse existing" is a one-time backfill at add time — the
            // archive holds that history. Restarts resume from the last
            // consumed byte, catching up whatever was written while the
            // app was closed (alerts stay silent for old lines; the dedup
            // guard keeps the archive clean). No saved position (older
            // settings) = tail from the end. Auto-discovered sources are
            // the folder watcher's to re-add — only when active again.
            if (!saved.AutoDiscovered && System.IO.File.Exists(saved.Path))
                Add(saved.Path, parseFromStart: false, saved.LastPosition);
        }
    }

    // ── Folder watch: track any log that becomes active ─────────────────────

    private readonly CancellationTokenSource _watchCts = new();
    private Task? _watchTask;

    public IReadOnlyList<string> WatchedFolders => Settings.WatchedFolders;

    /// <summary>Begin scanning watched folders — called AFTER
    /// RestoreFromSettings so manual sources win any race for a path.</summary>
    public void StartFolderWatch() =>
        _watchTask ??= Task.Run(() => WatchLoopAsync(_watchCts.Token));

    public void AddWatchedFolder(string folder)
    {
        if (Settings.WatchedFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            return;
        Settings = Settings with { WatchedFolders = [.. Settings.WatchedFolders, folder] };
        Settings.Save();
        // Watching (or re-watching) a folder is fresh consent for its logs —
        // clear any session tombstones underneath it.
        var prefix = folder.EndsWith(System.IO.Path.DirectorySeparatorChar) ? folder : folder + System.IO.Path.DirectorySeparatorChar;
        lock (Sync)
        {
            _removedPaths.RemoveWhere(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Stop watching a folder and detach the sources it added.</summary>
    public void RemoveWatchedFolder(string folder)
    {
        Settings = Settings with
        {
            WatchedFolders = [.. Settings.WatchedFolders.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))],
        };
        Settings.Save();
        // Trailing separator so "C:\logs" never matches a sibling "C:\logs2".
        var prefix = folder.EndsWith(System.IO.Path.DirectorySeparatorChar) ? folder : folder + System.IO.Path.DirectorySeparatorChar;
        foreach (var source in Sources)
        {
            if (source.AutoDiscovered
                && source.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                Remove(source);
        }
    }

    /// <summary>Every ~2 s: any eq2log_*.txt under a watched folder (all
    /// server subfolders) that GROWS becomes a live source. A log with a
    /// saved position resumes there (catch-up); a never-seen log baselines
    /// at its current length and attaches only when it grows past it — so
    /// pointing at the logs root never chews a giant dormant file.</summary>
    private async Task WatchLoopAsync(CancellationToken ct)
    {
        Dictionary<string, long> baselines = new(StringComparer.OrdinalIgnoreCase);
        while (!ct.IsCancellationRequested)
        {
            foreach (var folder in Settings.WatchedFolders)
            {
                IEnumerable<string> files;
                try
                {
                    if (!System.IO.Directory.Exists(folder))
                        continue;
                    files = System.IO.Directory.EnumerateFiles(folder, "eq2log_*.txt", System.IO.SearchOption.AllDirectories);
                }
                catch (Exception)
                {
                    continue; // unreadable folder this pass — retry next
                }
                foreach (var file in files)
                {
                    try
                    {
                        lock (Sync)
                        {
                            // Explicitly removed this session — respect that
                            // instead of re-adding the log within ~2s.
                            if (_removedPaths.Contains(file))
                                continue;
                        }
                        if (Sources.Any(s => string.Equals(s.Path, file, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var length = new System.IO.FileInfo(file).Length;
                        var saved = Settings.Sources.FirstOrDefault(s =>
                            string.Equals(s.Path, file, StringComparison.OrdinalIgnoreCase));
                        if (saved?.LastPosition is { } position)
                        {
                            // Known log: catch up when it grew. Pass the RAW
                            // saved position — the tail reader treats a stale
                            // offset past the file's length as rotation and
                            // restarts from 0; pre-clamping to the new length
                            // silently skipped the rotated file's content.
                            if (length != position)
                                Add(file, parseFromStart: false, position, autoDiscovered: true);
                        }
                        else if (!baselines.TryGetValue(file, out var baseline))
                        {
                            baselines[file] = length;
                        }
                        else if (length > baseline)
                        {
                            baselines.Remove(file);
                            Add(file, parseFromStart: false, baseline, autoDiscovered: true);
                        }
                        else if (length < baseline)
                        {
                            baselines[file] = length; // rotated while dormant
                        }
                    }
                    catch (Exception)
                    {
                        // One unreadable file never stops the sweep.
                    }
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void PersistSources()
    {
        // Live sources with fresh positions, plus every dormant entry from
        // earlier sessions — auto-discovered logs that never woke up AND
        // manual sources whose file is missing right now (unmounted or
        // network drive: dropping them lost the resume position forever).
        // Only explicitly-removed paths are actually forgotten.
        List<SourceSetting> persisted =
            [.. Sources.Select(s => new SourceSetting(s.Path, s.ParseFromStart, s.LastPosition, s.AutoDiscovered))];
        var livePaths = new HashSet<string>(persisted.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        lock (Sync)
        {
            persisted.AddRange(Settings.Sources.Where(s =>
                !livePaths.Contains(s.Path) && !_removedPaths.Contains(s.Path)));
        }
        Settings = Settings with { Sources = persisted };
        Settings.Save();
    }

    /// <summary>Stop the folder watcher and join it. Idempotent — called
    /// before <see cref="PersistSources"/> on exit (a discovery racing the
    /// persist would lose its resume position) and again by Dispose.</summary>
    public void StopFolderWatch()
    {
        _watchCts.Cancel();
        try
        {
            _watchTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Cancellation surfacing on shutdown — nothing to report.
        }
    }

    public void Dispose()
    {
        // Stop the folder watcher FIRST and join it — it could otherwise
        // Add a source concurrently with (or after) this teardown.
        StopFolderWatch();
        foreach (var source in Sources)
            source.Dispose();
        History.Dispose();
        Uploads.Dispose();
        Audio.Dispose();
        _watchCts.Dispose();
    }

}
