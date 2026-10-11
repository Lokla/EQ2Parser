using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.History;
using Microsoft.Data.Sqlite;

namespace EQ2Parser.App.Services;

/// <summary>What became of history.db when this session opened it.</summary>
public enum ArchiveState
{
    /// <summary>Opened (or created) normally.</summary>
    Healthy,

    /// <summary>The file was damaged. It was renamed aside, nothing
    /// deleted, and a fresh archive is in use: fights are being saved, but
    /// the earlier ones are in the renamed file
    /// (<see cref="HistoryService.QuarantinedPath"/>).</summary>
    Quarantined,

    /// <summary>The archive could not be opened and was left exactly as it
    /// is. The session runs without one: fights parse and show, nothing is
    /// saved, the archive window is empty.</summary>
    Unavailable,
}

/// <summary>
/// Cross-session parse history: every finished encounter queues to a
/// background writer (the log pump never blocks on SQLite). Retention is
/// split — trash hard-deletes after its window, BOSS fights stay in the
/// archive until explicitly purged. At startup the recent window of each
/// restores into the correlator; anything older is searchable in the
/// archive and can be pulled back into the parser on demand. Deleting a
/// fight from the tree only unloads it from the session — the archive
/// keeps it (purging lives in the archive window).
///
/// The archive is never what stops the app. The parser's job is the fight
/// on screen; the archive is where it goes afterwards. So nothing here
/// throws at startup: a history.db that cannot be opened leaves the session
/// in a degraded <see cref="State"/> (see <see cref="Open"/> for which
/// failures move the file aside and which leave it alone), a fight that
/// cannot be restored costs that fight (<see cref="RestoreInto"/>), and a
/// save that fails costs that save — each with a crash report, a journal
/// line and a property a UI surface can show (<see cref="Problem"/>).
/// </summary>
public sealed class HistoryService : IDisposable
{
    /// <summary>Startup-restore cap — several raid nights without a startup
    /// stall; everything else stays reachable through the archive.</summary>
    private const int MaxRestoredFights = 150;

    /// <summary>Crash reports one session may write for failed saves. The
    /// first carries the stack trace that matters. A fault that is there to
    /// stay (a full disk, a file gone read-only) fails once per FIGHT, and
    /// an import ends thousands of fights: uncapped, it would rotate every
    /// older, unrelated report out of the crashes folder (it keeps 30).
    /// Every failure past the cap is still counted, in
    /// <see cref="SaveFailures"/> and in the journal.</summary>
    private const int MaxSaveReports = 3;

    /// <summary>The crash-report kinds this class writes.</summary>
    internal const string QuarantinedKind = "archive-quarantined";
    internal const string UnavailableKind = "archive-unavailable";
    internal const string SaveKind = "archive-save";
    internal const string RestoreKind = "archive-restore";
    internal const string BackupKind = "archive-backup";

    private const string FileName = "history.db";

    /// <summary>Null when the session runs without an archive
    /// (<see cref="ArchiveState.Unavailable"/>). Every member checks.</summary>
    private readonly HistoryStore? _store;
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

    /// <summary>Set by Dispose, under the gate. The writer checks it there
    /// before each save: fights still queued when the exit stopped waiting
    /// must be dropped, not thrown at a store that has just been closed —
    /// that would write "archive save FAILED" evidence for a session whose
    /// only fault was being closed.</summary>
    private bool _disposed;

    private long _saveFailures;
    private volatile Exception? _lastSaveError;
    private int _saveReports;
    private volatile Exception? _restoreError;
    private int _restoreFailures;

    /// <summary>What became of history.db at startup. Fixed for the
    /// session.</summary>
    public ArchiveState State { get; }

    /// <summary>Why <see cref="State"/> is not Healthy, as one English
    /// sentence a person can act on; null when it is. English by design,
    /// like the journal and the crash reports — see <see cref="Problem"/>
    /// for what a UI surface should show.</summary>
    public string? DegradedReason { get; }

    /// <summary>Full path of the damaged archive that was renamed aside
    /// (its -wal and -shm siblings follow it under the same name); null
    /// unless one was. Set for <see cref="ArchiveState.Quarantined"/>, and
    /// for the rare Unavailable session whose fresh archive could not be
    /// created after the rename.</summary>
    public string? QuarantinedPath { get; }

    /// <summary>False for a session running without an archive.</summary>
    public bool IsAvailable => _store is not null;

    /// <summary>Fights this session that could not be written to the
    /// archive. Each was parsed and shown, and uploaded if uploads are on;
    /// it is missing from history.db only.</summary>
    public long SaveFailures => Interlocked.Read(ref _saveFailures);

    /// <summary>Why the most recent save failed (null = none has).</summary>
    public Exception? LastSaveError => _lastSaveError;

    /// <summary>Recent fights the startup restore could not load.</summary>
    public int RestoreFailures => Volatile.Read(ref _restoreFailures);

    /// <summary>Why the startup restore failed, in whole or for some
    /// fights (null = it did not).</summary>
    public Exception? RestoreError => _restoreError;

    /// <summary>Everything wrong with the archive right now, as English
    /// text for a status line or a banner; null when nothing is. It covers
    /// the startup verdict (<see cref="DegradedReason"/>), fights the
    /// restore could not load and saves that failed, so a surface bound to
    /// this one property misses none of them. Cheap enough for the UI tick.</summary>
    public string? Problem
    {
        get
        {
            var saves = SaveFailures;
            var restores = RestoreFailures;
            if (DegradedReason is null && saves == 0 && _restoreError is null)
                return null;
            List<string> parts = [];
            if (DegradedReason is { } reason)
                parts.Add(reason);
            if (_restoreError is { } restoreError)
            {
                parts.Add(restores > 0
                    ? $"{restores} recent fight{(restores == 1 ? "" : "s")} could not be loaded from the archive ({Kind(restoreError)})."
                    : $"The recent fights could not be loaded from the archive ({Kind(restoreError)}).");
            }
            if (saves > 0)
                parts.Add($"{saves} fight{(saves == 1 ? "" : "s")} from this session could not be saved to the archive ({Kind(_lastSaveError)}).");
            return string.Join(" ", parts);
        }
    }

    /// <summary>Test seam: how the exit snapshot is written. The app never
    /// replaces it; a test does, to fail a snapshot part-way.</summary>
    internal Action<HistoryStore, string> SnapshotTo { get; set; } = static (store, path) => store.BackupTo(path);

    public HistoryService()
    {
        (_store, State, DegradedReason, QuarantinedPath) = Open(Path.Combine(AppSettings.Directory, FileName));
        _writer = _store is { } store ? Task.Run(() => WriteLoopAsync(store)) : Task.CompletedTask;
    }

    /// <summary>Open the archive, and decide what a failure means for the
    /// FILE. Never throws: this runs while SourceManager is constructed,
    /// and a throw there is a failed startup (exit code 71) — on every
    /// launch, for as long as the file stays as it is, with the fix
    /// ("delete history.db by hand") nowhere on screen.
    ///
    /// Two outcomes, and the split is deliberately lopsided:
    ///
    ///  * SQLite says the file itself is bad (not a database, corrupt,
    ///    malformed schema — <see cref="HistoryStore.IsDamagedFile"/> has
    ///    the result codes and the reasoning). It will fail the same way on
    ///    every launch, so it is renamed aside WITH its -wal and -shm
    ///    siblings and a fresh archive is started. Nothing is deleted: the
    ///    renamed file is everything the old archive held, and a later
    ///    build or a manual repair (sqlite3's .recover) can still read it.
    ///    The evidence says so, and names the file.
    ///
    ///  * Anything else — locked by another process (a second copy of the
    ///    app on the same data folder, a backup or sync tool), access
    ///    denied, disk full, the path not there, an SQL error from this
    ///    build's own migration. The file may be a perfectly good archive
    ///    that is merely out of reach right now, so it is NOT touched and
    ///    the session runs without an archive: fights still parse, show and
    ///    upload; nothing is saved; the next launch tries again. Renaming a
    ///    healthy archive would look, to its owner, exactly like losing it.
    ///
    /// The same care decides the unhappy ends of the first case: a damaged
    /// file that will not move stays where it is (no archive this session),
    /// and one that moved but whose replacement cannot be created stays
    /// moved (again no archive) — never a fresh database created beside
    /// files that belong to another one.
    ///
    /// Each outcome writes one crash report (its own kind; "always", since
    /// there is at most one per launch) and one journal line BEFORE the
    /// constructor returns, so whoever reads <see cref="State"/> finds the
    /// evidence already there. The journal names exception types and the
    /// quarantined file, never a path.</summary>
    private static (HistoryStore? Store, ArchiveState State, string? Reason, string? Quarantined) Open(string path)
    {
        Exception failure;
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            return (new HistoryStore(path), ArchiveState.Healthy, null, null);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (!HistoryStore.IsDamagedFile(failure))
        {
            return Unavailable(
                failure,
                $"The fight archive ({FileName}) could not be opened ({failure.Message}), so fights from this session are not being saved. The file has been left exactly as it is, and the next start will try it again.",
                "could not be opened; the file was left untouched",
                quarantined: null);
        }

        string quarantined;
        try
        {
            quarantined = HistoryStore.Quarantine(path, DateTime.Now);
        }
        catch (Exception moveError)
        {
            return Unavailable(
                failure,
                $"The fight archive ({FileName}) is damaged ({failure.Message}) and could not be moved aside ({moveError.Message}), so fights from this session are not being saved. The file has been left where it is.",
                $"is damaged and could not be moved aside ({Kind(moveError)}); the file was left where it is",
                quarantined: null);
        }

        var quarantinedName = Path.GetFileName(quarantined);
        try
        {
            var fresh = new HistoryStore(path);
            CrashLog.Report(
                QuarantinedKind, failure,
                $"The fight archive ({FileName}) could not be read: SQLite reported the file itself as damaged.{Environment.NewLine}"
                + $"It was renamed to {quarantinedName} in the data folder (a -wal or -shm file beside it was{Environment.NewLine}"
                + $"renamed with it) and a new, empty archive was started. Nothing was deleted: the fights{Environment.NewLine}"
                + $"saved before now are in that renamed file, and a later build or a manual repair may{Environment.NewLine}"
                + "still recover them. Keep it.",
                always: true);
            SessionJournal.Mark(
                $"archive QUARANTINED — {FileName} was damaged ({Kind(failure)}); renamed to {quarantinedName}, a fresh archive started, nothing deleted");
            return (
                fresh,
                ArchiveState.Quarantined,
                $"The fight archive ({FileName}) was damaged and could not be read. It has been renamed to {quarantinedName} in the data folder and a new, empty archive started. Nothing was deleted: your earlier fights are in the renamed file, which a later version or a manual repair may still recover.",
                quarantined);
        }
        catch (Exception freshError)
        {
            return Unavailable(
                freshError,
                $"The fight archive ({FileName}) was damaged and has been renamed to {quarantinedName} in the data folder, but a new archive could not be created ({freshError.Message}), so fights from this session are not being saved. Nothing was deleted.",
                $"was damaged ({Kind(failure)}) and renamed to {quarantinedName}, but a fresh archive could not be created; nothing deleted",
                quarantined);
        }

        static (HistoryStore?, ArchiveState, string?, string?) Unavailable(
            Exception error, string reason, string journal, string? quarantined)
        {
            CrashLog.Report(UnavailableKind, error, reason, always: true);
            SessionJournal.Mark($"archive UNAVAILABLE — {FileName} {journal} ({Kind(error)}); this session saves no fights");
            return (null, ArchiveState.Unavailable, reason, quarantined);
        }
    }

    /// <summary>An exception as the journal may name it: its type, plus
    /// SQLite's result code when it has one (13 "disk full" and 5 "busy"
    /// are different stories under one type). Never the message — an IO
    /// exception's quotes a path.</summary>
    internal static string Kind(Exception? error) => error switch
    {
        null => "unknown",
        SqliteException sqlite => $"SqliteException {sqlite.SqliteErrorCode}",
        _ => error.GetType().Name,
    };

    /// <summary>The background writer: one queued fight at a time, each
    /// save in its own guard. A failed save loses one fight, never the app
    /// — and never silently (see <see cref="OnSaveFailed"/>).</summary>
    private async Task WriteLoopAsync(HistoryStore store)
    {
        await foreach (var encounter in _saves.Reader.ReadAllAsync())
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;
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
                    var existing = store.FindEncounter(
                        encounter.SourceId, encounter.OwnerName,
                        encounter.StartTime, encounter.EndTime, encounter.Title);
                    if (existing is { } staleId
                        && CountUniqueSwings(encounter) > store.CountSwings(staleId))
                    {
                        store.DeleteEncounter(staleId);
                        _inParser.Remove(staleId);
                        existing = null;
                    }
                    var id = existing ?? store.SaveEncounter(encounter);
                    if (!unloaded)
                        _inParser.Add(id);
                    // Inside the gate so CollapseToPrimary can never see
                    // a saved row without its id mapping (it would doom
                    // via _skipSave and orphan the row).
                    _storedIds.Add(encounter, new StrongBox<long>(id));
                }
            }
            catch (Exception ex)
            {
                OnSaveFailed(ex, encounter);
            }
        }
    }

    /// <summary>A save threw. The fight is lost to the archive and the
    /// writer carries on — but this used to be an empty catch, so an
    /// archive that had stopped taking writes lost every later fight of
    /// the night without a trace anywhere.
    ///
    /// Sized for the failure that does not go away. A full disk fails
    /// every save from then on, one per fight:
    ///  * crash reports: the first few that land, <see cref="MaxSaveReports"/>
    ///    a session (they go through the ordinary five-second rate limit,
    ///    so only reports actually written count toward the cap);
    ///  * journal: the 1st failure, then the 10th, 100th, 1,000th… each
    ///    carrying the running total, and the final total once more at
    ///    exit (<see cref="Dispose"/>) — a whole night of failures is a
    ///    handful of lines;
    ///  * <see cref="SaveFailures"/> and <see cref="LastSaveError"/>: every
    ///    one.
    /// The journal names the exception type and SQLite's result code only.
    ///
    /// Evidence first, the public count last: the count is what other
    /// threads (a status line, the tests) watch, so whoever sees it move
    /// finds the report and the journal line already written. Writer
    /// thread only, outside the gate — the file writes never hold up a
    /// reader of the archive.</summary>
    private void OnSaveFailed(Exception ex, Encounter encounter)
    {
        var failures = Interlocked.Read(ref _saveFailures) + 1;
        if (_saveReports < MaxSaveReports
            && CrashLog.Report(
                SaveKind, ex,
                $"A finished fight could not be saved to the archive ({FileName}).{Environment.NewLine}"
                + $"It was parsed and shown as usual; it is missing from the archive only.{Environment.NewLine}"
                + $"Fight: {encounter.Title} ({encounter.Zone}){Environment.NewLine}"
                + $"Saves failed this session: {failures}") is not null)
        {
            _saveReports++;
        }
        if (IsPowerOfTen(failures))
            SessionJournal.Mark($"archive save FAILED: {Kind(ex)} ({failures} this session) — the fight was parsed but is not in {FileName}");
        _lastSaveError = ex;
        Interlocked.Exchange(ref _saveFailures, failures);
    }

    private static bool IsPowerOfTen(long n)
    {
        while (n >= 10 && n % 10 == 0)
            n /= 10;
        return n == 1;
    }

    /// <summary>Swings the store would persist (each combatant's outgoing
    /// reference bucket, deduplicated by the same rule SaveEncounter uses).</summary>
    private static int CountUniqueSwings(Encounter encounter) =>
        encounter.Combatants.Values
            .Where(c => c.OutgoingBuckets.ContainsKey(Core.Combat.BucketConfig.AllOutgoingRef))
            .Sum(c => c.OutgoingBuckets[Core.Combat.BucketConfig.AllOutgoingRef].All.Swings.Count);

    /// <summary>Engine EncounterEnded handler — snapshot is final by now,
    /// so the actual write happens off-thread. Without an archive there is
    /// nowhere to write: the fight is simply not queued (nothing would ever
    /// drain the queue, and it would hold every fight of the night).</summary>
    public void QueueSave(Encounter encounter)
    {
        if (_store is null)
            return;
        if (encounter.Title == Encounter.PlaceholderTitle)
            return; // engine scraps these; never persist them
        _saves.Writer.TryWrite(encounter);
    }

    /// <summary>Startup: hard-delete old trash, then replay the recent
    /// window (bosses and trash on their own clocks, oldest first) through
    /// the correlator. Returns fights loaded.
    ///
    /// Never throws. This runs inside App startup, where a throw ends the
    /// launch — and an archive with one unreadable page (damage SQLite
    /// only finds when it reads that page, long after the file opened
    /// cleanly) would then end EVERY launch. So the housekeeping and the
    /// listing are one guard, and each fight is its own: a fight that will
    /// not load costs that fight, the rest still restore, and the archive
    /// stays in place — most of it is readable, which is worth far more
    /// than the fresh empty one a quarantine would leave. One crash report
    /// and one journal line cover the whole restore.</summary>
    public int RestoreInto(EncounterCorrelator correlator, int bossDays, int trashDays)
    {
        if (_store is not { } store)
            return 0;
        lock (_gate)
        {
            List<EncounterSummary> recent;
            try
            {
                var now = DateTimeOffset.Now;
                // Heal archives from before the duplicate-save guard existed.
                store.RemoveDuplicateEncounters();
                store.PruneTrashBefore(now.AddDays(-Math.Max(1, trashDays)));
                recent = store
                    .SearchEncounters(since: now.AddDays(-Math.Max(1, bossDays)), bossOnly: true, limit: MaxRestoredFights)
                    .Concat(store.SearchEncounters(since: now.AddDays(-Math.Max(1, trashDays)), bossOnly: false, limit: MaxRestoredFights))
                    .OrderByDescending(s => s.Start)
                    .Take(MaxRestoredFights)
                    .Reverse()
                    .ToList();
            }
            catch (Exception ex)
            {
                CrashLog.Report(
                    RestoreKind, ex,
                    $"The recent fights could not be listed from the archive ({FileName}) at startup.{Environment.NewLine}"
                    + "None were restored into the session. The archive file was left as it is.",
                    always: true);
                SessionJournal.Mark($"archive restore FAILED: the recent fights could not be listed ({Kind(ex)}); none restored, {FileName} left as it is");
                _restoreError = ex;
                return 0;
            }

            foreach (var summary in recent)
                correlator.RegisterOwner(summary.Owner);
            var restored = 0;
            var failed = 0;
            Exception? firstFailure = null;
            foreach (var summary in recent)
            {
                try
                {
                    LoadLocked(store, summary, correlator);
                    restored++;
                }
                catch (Exception ex)
                {
                    failed++;
                    firstFailure ??= ex;
                }
            }
            if (firstFailure is not null)
            {
                CrashLog.Report(
                    RestoreKind, firstFailure,
                    $"{failed} of {recent.Count} recent fights could not be loaded from the archive ({FileName}) at startup.{Environment.NewLine}"
                    + $"The other {restored} were restored. The archive file was left as it is.{Environment.NewLine}"
                    + "The exception below is the first of them.",
                    always: true);
                SessionJournal.Mark($"archive restore: {failed} of {recent.Count} recent fights could not be loaded ({Kind(firstFailure)}); {FileName} left as it is");
                Volatile.Write(ref _restoreFailures, failed);
                _restoreError = firstFailure;
            }
            return restored;
        }
    }

    // ---- the archive (search / pull back / purge) ----
    //
    // Without an archive these answer as an empty one does: nothing found,
    // nothing loaded, nothing to purge. The reason is in DegradedReason.

    public IReadOnlyList<EncounterSummary> Search(string? text, DateTimeOffset? since, int limit = 500)
    {
        if (_store is not { } store)
            return [];
        lock (_gate)
        {
            return store.SearchEncounters(titleContains: text, since: since, limit: limit);
        }
    }

    /// <summary>Every zone with archived boss fights — the curation
    /// workbench's zone picker.</summary>
    public IReadOnlyList<string> ArchivedZones()
    {
        if (_store is not { } store)
            return [];
        lock (_gate)
        {
            return [.. store.SearchEncounters(bossOnly: true, limit: 2000)
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
        if (_store is not { } store)
            yield break;
        IReadOnlyList<EncounterSummary> summaries;
        lock (_gate)
        {
            summaries = store.SearchEncounters(zone: zone, bossOnly: true, limit: 500);
        }
        foreach (var summary in summaries)
        {
            IReadOnlyList<Core.Combat.Swing> swings;
            HashSet<string> enemies;
            lock (_gate)
            {
                swings = store.LoadSwings(summary.Id);
                enemies = new HashSet<string>(
                    store.LoadCombatants(summary.Id).Where(c => !c.IsAlly).Select(c => c.Name),
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
        if (_store is not { } store)
            return false;
        lock (_gate)
        {
            if (_inParser.Contains(summary.Id))
                return false;
            correlator.RegisterOwner(summary.Owner);
            LoadLocked(store, summary, correlator);
            return true;
        }
    }

    private void LoadLocked(HistoryStore store, EncounterSummary summary, EncounterCorrelator correlator)
    {
        var encounter = store.RestoreEncounter(summary);
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
        // No archive, nothing to shape — and no writer to drain the
        // bookkeeping sets below, which would otherwise hold on to every
        // mirror of the night.
        if (_store is not { } store)
            return;
        lock (_gate)
        {
            foreach (var mirror in fight.NonPrimarySources)
            {
                if (_storedIds.TryGetValue(mirror, out var box))
                {
                    store.DeleteEncounter(box.Value);
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
        if (_store is null)
            return;
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
        if (_store is null)
            return;
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
        if (_store is not { } store)
            return;
        lock (_gate)
        {
            store.DeleteEncounter(encounterId);
            _inParser.Remove(encounterId);
        }
    }

    public void Dispose()
    {
        _saves.Writer.TryComplete();
        var drained = true;
        try
        {
            drained = _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Shutdown path — nothing to report.
        }
        lock (_gate)
        {
            if (_disposed)
                return;
            // From here the writer drops whatever is still queued (it
            // checks under this gate) instead of failing each fight
            // against a closed store.
            _disposed = true;
            if (_store is { } store)
            {
                BackupOnClose(store);
                store.Dispose();
            }
        }
        // The session's totals, where a postmortem or a tester's journal
        // will have them: the per-failure lines above are deliberately
        // sparse, and fights the exit did not wait for were never counted
        // as failures at all.
        if (!drained && _saves.Reader.CanCount && _saves.Reader.Count is > 0 and var waiting)
            SessionJournal.Mark($"archive: exit did not wait for {waiting} queued fight save{(waiting == 1 ? "" : "s")} — not in {FileName}");
        if (SaveFailures is > 1 and var failures)
            SessionJournal.Mark($"archive: {failures} fight saves failed this session (last: {Kind(_lastSaveError)})");
    }

    /// <summary>Clean-shutdown snapshot: backups\history.db.bak (newest
    /// close) with the previous close rotated to .bak2 — insurance against
    /// both the one-keystroke Remove-Item and creeping corruption. In a
    /// SUBFOLDER deliberately: the classic wipe command is the glob
    /// "history.db*", which must not take the backups with it.
    /// Best-effort; a failed backup never blocks shutdown.
    ///
    /// Snapshot FIRST, to a temporary name, and rotate only once it is
    /// complete. The old order — move .bak to .bak2, then write the new
    /// .bak — spent the last good backup on an attempt that might not
    /// finish: a snapshot that failed part-way (disk full is the likely
    /// one, and an archive is the biggest file in the folder) left .bak
    /// missing or partial, and the NEXT exit rotated that over the one
    /// good copy still in .bak2. Now a failed snapshot costs nothing: the
    /// partial file is removed and both backups are exactly as they were.
    /// A temporary file left by an exit that died mid-snapshot is of
    /// unknown completeness, so it is deleted before the next attempt and
    /// never promoted.
    ///
    /// The rotation itself is two renames inside one folder. If the second
    /// fails, .bak2 holds the previous good backup and the next clean exit
    /// writes .bak again.
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
    private void BackupOnClose(HistoryStore store)
    {
        string? snapshot = null;
        try
        {
            if (!store.HasEncounters())
                return;
            var backupDir = Path.Combine(AppSettings.Directory, "backups");
            Directory.CreateDirectory(backupDir);
            var bak = Path.Combine(backupDir, "history.db.bak");
            var bak2 = Path.Combine(backupDir, "history.db.bak2");
            snapshot = Path.Combine(backupDir, "history.db.bak.tmp");
            DeleteSnapshot(snapshot);
            SnapshotTo(store, snapshot);
            // Complete — only now may it displace anything.
            if (File.Exists(bak))
                File.Move(bak, bak2, overwrite: true);
            File.Move(snapshot, bak);
        }
        catch (Exception ex)
        {
            // The live db is untouched either way, and so are the backups
            // unless the very last rename is what failed.
            CrashLog.Report(BackupKind, ex, "The exit snapshot of the archive (backups\\history.db.bak) could not be written. The archive itself is unaffected.");
            SessionJournal.Mark($"archive: exit backup FAILED ({Kind(ex)}) — the archive is unaffected, earlier backups were kept");
            if (snapshot is not null)
            {
                try
                {
                    DeleteSnapshot(snapshot);
                }
                catch (Exception)
                {
                    // Left for the next exit, which deletes it before it
                    // writes its own.
                }
            }
        }
    }

    /// <summary>Remove a temporary snapshot and anything SQLite may have
    /// left beside it when the write was cut short.</summary>
    private static void DeleteSnapshot(string snapshot)
    {
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            File.Delete(snapshot + suffix);
    }
}
