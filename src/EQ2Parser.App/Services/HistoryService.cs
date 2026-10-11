using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.History;

namespace EQ2Parser.App.Services;

/// <summary>
/// Cross-session parse history: every finished encounter queues to a
/// background writer (the log pump never blocks on SQLite). Retention is
/// split — trash hard-deletes after its window, BOSS fights stay in the
/// archive until explicitly purged. At startup the recent window of each
/// restores into the correlator; anything older is searchable in the
/// archive and can be pulled back into the parser on demand. Deleting a
/// fight from the tree only unloads it from the session — the archive
/// keeps it (purging lives in the archive window).
/// </summary>
public sealed class HistoryService : IDisposable
{
    /// <summary>Startup-restore cap — several raid nights without a startup
    /// stall; everything else stays reachable through the archive.</summary>
    private const int MaxRestoredFights = 150;

    private readonly HistoryStore _store;
    private readonly object _gate = new();
    private readonly Channel<Encounter> _saves = Channel.CreateUnbounded<Encounter>();
    private readonly Task _writer;
    private readonly ConditionalWeakTable<Encounter, StrongBox<long>> _storedIds = [];
    private readonly HashSet<long> _inParser = [];

    /// <summary>Encounters deleted from the tree BEFORE their background
    /// save landed — the save must then archive them as NOT loaded, or the
    /// phantom "loaded" id made the archive refuse to pull them back.</summary>
    private readonly HashSet<Encounter> _unloadedBeforeSave = [];

    /// <summary>Non-primary mirrors doomed by the archive collapse before
    /// their background save landed — the writer drops them instead of
    /// saving a copy the collapse would immediately delete.</summary>
    private readonly HashSet<Encounter> _skipSave = [];

    public HistoryService()
    {
        Directory.CreateDirectory(AppSettings.Directory);
        _store = new HistoryStore(Path.Combine(AppSettings.Directory, "history.db"));
        _writer = Task.Run(async () =>
        {
            await foreach (var encounter in _saves.Reader.ReadAllAsync())
            {
                try
                {
                    lock (_gate)
                    {
                        // Doomed by the archive collapse (a non-primary
                        // mirror of an already-archived fight) — drop it.
                        if (_skipSave.Remove(encounter))
                        {
                            _unloadedBeforeSave.Remove(encounter);
                            continue;
                        }
                        var unloaded = _unloadedBeforeSave.Remove(encounter);
                        // A re-parse of an already-archived fight maps to
                        // its existing row instead of saving a duplicate —
                        // UNLESS the re-parse captured MORE swings (newer
                        // grammar shapes, e.g. status effects): richer data
                        // replaces the stale rows in place.
                        var existing = _store.FindEncounter(
                            encounter.SourceId, encounter.OwnerName,
                            encounter.StartTime, encounter.EndTime, encounter.Title);
                        if (existing is { } staleId
                            && CountUniqueSwings(encounter) > _store.CountSwings(staleId))
                        {
                            _store.DeleteEncounter(staleId);
                            _inParser.Remove(staleId);
                            existing = null;
                        }
                        var id = existing ?? _store.SaveEncounter(encounter);
                        if (!unloaded)
                            _inParser.Add(id);
                        // Inside the gate so CollapseToPrimary can never see
                        // a saved row without its id mapping (it would doom
                        // via _skipSave and orphan the row).
                        _storedIds.Add(encounter, new StrongBox<long>(id));
                    }
                }
                catch (Exception)
                {
                    // A failed save loses one fight, never the app.
                }
            }
        });
    }

    /// <summary>Swings the store would persist (each combatant's outgoing
    /// reference bucket, deduplicated by the same rule SaveEncounter uses).</summary>
    private static int CountUniqueSwings(Encounter encounter) =>
        encounter.Combatants.Values
            .Where(c => c.OutgoingBuckets.ContainsKey(Core.Combat.BucketConfig.AllOutgoingRef))
            .Sum(c => c.OutgoingBuckets[Core.Combat.BucketConfig.AllOutgoingRef].All.Swings.Count);

    /// <summary>Engine EncounterEnded handler — snapshot is final by now,
    /// so the actual write happens off-thread.</summary>
    public void QueueSave(Encounter encounter)
    {
        if (encounter.Title == Encounter.PlaceholderTitle)
            return; // engine scraps these; never persist them
        _saves.Writer.TryWrite(encounter);
    }

    /// <summary>Startup: hard-delete old trash, then replay the recent
    /// window (bosses and trash on their own clocks, oldest first) through
    /// the correlator. Returns fights loaded.</summary>
    public int RestoreInto(EncounterCorrelator correlator, int bossDays, int trashDays)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            // Heal archives from before the duplicate-save guard existed.
            _store.RemoveDuplicateEncounters();
            _store.PruneTrashBefore(now.AddDays(-Math.Max(1, trashDays)));
            var recent = _store
                .SearchEncounters(since: now.AddDays(-Math.Max(1, bossDays)), bossOnly: true, limit: MaxRestoredFights)
                .Concat(_store.SearchEncounters(since: now.AddDays(-Math.Max(1, trashDays)), bossOnly: false, limit: MaxRestoredFights))
                .OrderByDescending(s => s.Start)
                .Take(MaxRestoredFights)
                .Reverse()
                .ToList();
            foreach (var summary in recent)
                correlator.RegisterOwner(summary.Owner);
            foreach (var summary in recent)
                LoadLocked(summary, correlator);
            return recent.Count;
        }
    }

    // ---- the archive (search / pull back / purge) ----

    public IReadOnlyList<EncounterSummary> Search(string? text, DateTimeOffset? since, int limit = 500)
    {
        lock (_gate)
        {
            return _store.SearchEncounters(titleContains: text, since: since, limit: limit);
        }
    }

    /// <summary>Every zone with archived boss fights — the curation
    /// workbench's zone picker.</summary>
    public IReadOnlyList<string> ArchivedZones()
    {
        lock (_gate)
        {
            return [.. _store.SearchEncounters(bossOnly: true, limit: 2000)
                .Select(s => s.Zone)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(z => z, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>Stream a zone's archived boss fights with their full swing
    /// lists and enemy name sets — the ability miner's feed. Each fight
    /// loads under the gate; iteration between fights doesn't hold it.</summary>
    public IEnumerable<(EncounterSummary Summary, IReadOnlyList<Core.Combat.Swing> Swings, HashSet<string> Enemies)>
        EnumerateArchivedFights(string zone)
    {
        IReadOnlyList<EncounterSummary> summaries;
        lock (_gate)
        {
            summaries = _store.SearchEncounters(zone: zone, bossOnly: true, limit: 500);
        }
        foreach (var summary in summaries)
        {
            IReadOnlyList<Core.Combat.Swing> swings;
            HashSet<string> enemies;
            lock (_gate)
            {
                swings = _store.LoadSwings(summary.Id);
                enemies = new HashSet<string>(
                    _store.LoadCombatants(summary.Id).Where(c => !c.IsAlly).Select(c => c.Name),
                    StringComparer.OrdinalIgnoreCase);
            }
            yield return (summary, swings, enemies);
        }
    }

    public bool IsLoaded(long encounterId)
    {
        lock (_gate)
        {
            return _inParser.Contains(encounterId);
        }
    }

    /// <summary>Pull one archived fight back into the parser. Caller holds
    /// the manager sync lock (the correlator is not thread-safe).</summary>
    public bool LoadFight(EncounterSummary summary, EncounterCorrelator correlator)
    {
        lock (_gate)
        {
            if (_inParser.Contains(summary.Id))
                return false;
            correlator.RegisterOwner(summary.Owner);
            LoadLocked(summary, correlator);
            return true;
        }
    }

    private void LoadLocked(EncounterSummary summary, EncounterCorrelator correlator)
    {
        var encounter = _store.RestoreEncounter(summary);
        _storedIds.Add(encounter, new StrongBox<long>(summary.Id));
        _inParser.Add(summary.Id);
        correlator.Accept(encounter);
    }

    /// <summary>Correlator Merged handler: the archive keeps ONE copy of
    /// each fight — the primary (longest), matching the site's retention
    /// rule. A non-primary mirror already saved is deleted; one still in
    /// the save queue is dropped before it lands. The in-RAM session view
    /// keeps every mirror (that's what the perspective dropdown flicks
    /// between) — this only shapes what survives into the archive. Also
    /// self-heals pre-collapse archives: startup restore replays mirrors
    /// through the correlator, and each re-merge prunes the duplicates.</summary>
    public void CollapseToPrimary(CorrelatedEncounter fight)
    {
        lock (_gate)
        {
            foreach (var mirror in fight.NonPrimarySources)
            {
                if (_storedIds.TryGetValue(mirror, out var box))
                {
                    _store.DeleteEncounter(box.Value);
                    _inParser.Remove(box.Value);
                    _storedIds.Remove(mirror);
                }
                else
                {
                    _skipSave.Add(mirror);
                }
            }
        }
    }

    /// <summary>Tree deletion: the fight leaves the session but stays in
    /// the archive, ready to pull back.</summary>
    public void MarkUnloaded(CorrelatedEncounter fight)
    {
        lock (_gate)
        {
            foreach (var encounter in fight.Sources)
            {
                if (_storedIds.TryGetValue(encounter, out var box))
                    _inParser.Remove(box.Value);
                else
                    _unloadedBeforeSave.Add(encounter); // save hasn't landed yet
            }
        }
    }

    /// <summary>Undo of a tree deletion: the fight is back in the session,
    /// so the archive must show it as loaded again.</summary>
    public void MarkLoaded(CorrelatedEncounter fight)
    {
        lock (_gate)
        {
            foreach (var encounter in fight.Sources)
            {
                if (_storedIds.TryGetValue(encounter, out var box))
                    _inParser.Add(box.Value);
                else
                    _unloadedBeforeSave.Remove(encounter); // undo of a pre-save delete
            }
        }
    }

    /// <summary>The explicit purge: rows gone for good. (A copy already
    /// loaded in the session stays until removed from the tree.)</summary>
    public void PurgeFromArchive(long encounterId)
    {
        lock (_gate)
        {
            _store.DeleteEncounter(encounterId);
            _inParser.Remove(encounterId);
        }
    }

    public void Dispose()
    {
        _saves.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Shutdown path — nothing to report.
        }
        lock (_gate)
        {
            BackupOnClose();
            _store.Dispose();
        }
    }

    /// <summary>Clean-shutdown snapshot: backups\history.db.bak (newest
    /// close) with the previous close rotated to .bak2 — insurance against
    /// both the one-keystroke Remove-Item and creeping corruption. In a
    /// SUBFOLDER deliberately: the classic wipe command is the glob
    /// "history.db*", which must not take the backups with it.
    /// Best-effort; a failed backup never blocks shutdown.
    ///
    /// An EMPTY archive is never snapshotted, and so never rotates a
    /// backup out. Rotating on every clean exit meant the insurance was
    /// spent by the accident it was bought for: after a wipe (or an archive
    /// recreated from nothing) the first exit put the empty archive in .bak
    /// and the second pushed it into .bak2 — and "close it and open it
    /// again" is exactly what someone does on finding their history gone.
    /// Skipping costs nothing, because a snapshot of no fights restores to
    /// what deleting history.db gives you anyway.
    ///
    /// The guard is deliberately no wider than that. Once the archive holds
    /// a fight again the rotation resumes (there is new data to protect),
    /// so a lost archive survives one more exit in .bak2 and is gone on the
    /// next. Holding a backup back from a SMALLER archive would close that
    /// gap, but trash retention and the archive's own purge shrink it
    /// legitimately, and a backup that refuses to follow them protects
    /// stale fights while the live ones go unprotected.</summary>
    private void BackupOnClose()
    {
        try
        {
            if (!_store.HasEncounters())
                return;
            var backupDir = Path.Combine(AppSettings.Directory, "backups");
            Directory.CreateDirectory(backupDir);
            var bak = Path.Combine(backupDir, "history.db.bak");
            var bak2 = Path.Combine(backupDir, "history.db.bak2");
            if (File.Exists(bak))
            {
                File.Delete(bak2);
                File.Move(bak, bak2);
            }
            _store.BackupTo(bak);
        }
        catch (Exception)
        {
            // The live db is untouched either way.
        }
    }
}
