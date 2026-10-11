using EQ2Parser.Core.Combat;
using Microsoft.Data.Sqlite;

namespace EQ2Parser.Core.History;

public sealed record EncounterSummary(
    long Id,
    string SourceId,
    string Owner,
    string Zone,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    double DurationSeconds,
    SuccessLevel Success,
    long Damage,
    string? CorrelationId,
    bool IsBoss,
    DateTimeOffset? ZoneInstanceExpiry = null);

public sealed record CombatantSummary(
    string Name,
    long Damage,
    long Healed,
    long DamageTaken,
    long HealsTaken,
    int Deaths,
    bool IsAlly);

/// <summary>
/// Cross-session encounter history (the rich-data rule made durable): every
/// completed encounter is stored WITH its full swing list, so future app
/// features — replay, death recaps, per-ability drill-downs, re-uploads —
/// derive from stored data without re-parsing logs. WAL mode; one writer,
/// many readers. UI-free by design.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private const int SchemaVersion = 4;
    private readonly SqliteConnection _conn;

    /// <summary>Opens (creating if need be) and brings the file up to this
    /// build's shape. Throws when it cannot — and then holds nothing: the
    /// connection is closed before the exception leaves, so the caller is
    /// free to move the file aside (see <see cref="IsDamagedFile"/> and
    /// <see cref="Quarantine"/>). It used to stay open, unreachable, for
    /// the life of the process, keeping the file locked against exactly
    /// that.</summary>
    public HistoryStore(string path)
    {
        _conn = new SqliteConnection(ConnectionString(path));
        try
        {
            _conn.Open();
            Execute("PRAGMA journal_mode=WAL");
            Execute("PRAGMA foreign_keys=ON");
            // Migrations run BEFORE the idempotent schema pass — a new index may
            // reference a column the migration adds.
            var storedVersion = StoredVersion();
            Migrate();
            CreateSchema();
            StampVersion(storedVersion);
        }
        catch
        {
            _conn.Dispose();
            throw;
        }
    }

    /// <summary>Unpooled, for both the store and its snapshots. The
    /// provider's pool keeps a "closed" connection's file handle open for
    /// reuse, which buys nothing here (one connection for the life of the
    /// process) and costs the two things this class promises: a store that
    /// failed to open has let go of the file, and a finished snapshot can
    /// be renamed into place. Built with the builder so a path containing
    /// ";" or "=" is still one path.</summary>
    private static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    private int StoredVersion()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Upgrade an older database in place: give the file every
    /// column this build's statements name and it does not have yet.
    ///
    /// Keyed on the table's ACTUAL columns, never on the stored version. The
    /// version cannot be trusted downward: every build up to v0.5.7 stamps
    /// its own number on every open, so running an older build once (a
    /// portable copy, a rollback, a second install) lowers the stamp on a
    /// file that keeps its newer shape. Steps keyed on that stamp then ran
    /// ADD COLUMN a second time, SQLite answered "duplicate column name",
    /// the constructor threw, and the app could not start — with the fight
    /// archive intact on disk and unreachable. Those builds are released
    /// and will keep stamping downward, so the rule for every step here,
    /// present and future, is: SAFE TO RUN ON A FILE THAT ALREADY HAS IT.
    /// Ask the file what it holds; do not ask the stamp what it should hold.
    ///
    /// A step that is more than ADD COLUMN (a backfill, a rewrite of stored
    /// values) hangs off the column check that introduced it, inside the
    /// one transaction below, so "the column exists" always means "and its
    /// backfill ran" — a crash between the two leaves neither.
    ///
    /// Each table is asked separately, and a table the file does not have
    /// is skipped: CreateSchema builds a missing table in the current shape
    /// outright (ALTER would fail on "no such table"), which is also all a
    /// brand-new file needs.
    ///
    /// The steps were checked against CreateSchema column by column, from
    /// the first build's CREATE TABLE statements forward:
    ///  * encounters gained is_boss (v2) and zone_instance_expiry (v3);
    ///  * swings gained observed_at_ms in CREATE TABLE only, with no ALTER
    ///    for a file that already had the table — the step below is that
    ///    omission made good (v4). The change predates the first release,
    ///    so the files that lack the column are from pre-release builds;
    ///  * combatants has not changed since it was created.
    /// Indexes need no step: CreateSchema's IF NOT EXISTS adds a missing
    /// one to an existing table.</summary>
    private void Migrate()
    {
        List<string> steps = [];

        var encounters = ColumnNames("encounters");
        if (encounters.Count > 0)
        {
            // v2: is_boss, backfilled from the same named-mob rule the
            // classifier uses. The backfill belongs to the column's CREATION
            // only: on a file that already has is_boss the stored verdicts are
            // the truth and must not be rewritten from the title.
            if (!encounters.Contains("is_boss"))
            {
                steps.Add("""
                    ALTER TABLE encounters ADD COLUMN is_boss INTEGER NOT NULL DEFAULT 0;
                    UPDATE encounters SET is_boss = 1
                     WHERE title <> 'Encounter'
                       AND substr(title, 1, 2) <> 'a '
                       AND substr(title, 1, 3) <> 'an ';
                    """);
            }
            // v3: zone-instance identity (lockout expiry). Old rows stay NULL —
            // grouping treats null as compatible with anything.
            if (!encounters.Contains("zone_instance_expiry"))
                steps.Add("ALTER TABLE encounters ADD COLUMN zone_instance_expiry INTEGER;");
        }

        var swings = ColumnNames("swings");
        // v4: the arrival stamp. The column was added to CREATE TABLE with
        // no ALTER for the files that already had the table, and the insert
        // and select lists name it — on such a file every save threw inside
        // the archive writer and every restore threw at startup. Old rows
        // stay NULL: "no arrival stamp", which is what they have.
        if (swings.Count > 0 && !swings.Contains("observed_at_ms"))
            steps.Add("ALTER TABLE swings ADD COLUMN observed_at_ms INTEGER;");

        // The usual open finds nothing to do and must not take the write
        // lock to learn that.
        if (steps.Count == 0)
            return;
        using var tx = _conn.BeginTransaction();
        foreach (var step in steps)
            Execute(step, tx);
        tx.Commit();
    }

    /// <summary>The table's column names as the file has them now; empty
    /// when the table does not exist. SQLite compares column names without
    /// regard to case, so the set does too.</summary>
    private HashSet<string> ColumnNames(string table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info($table);";
        cmd.Parameters.AddWithValue("$table", table);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>Raise the file's version to this build's — and NEVER lower
    /// it. A file stamped higher was written by a newer build; stamping it
    /// down is what set up the startup failure described on
    /// <see cref="Migrate"/> for the build that returns to it.
    ///
    /// Such a file is still opened read/write, on purpose:
    ///  * every statement in this class names its columns (no SELECT *, no
    ///    positional INSERT), so columns, tables and indexes a newer build
    ///    added are simply invisible here;
    ///  * a row this build saves takes the default for any newer column —
    ///    the same "old rows stay NULL" contract each migration already
    ///    gives the rows that predate it;
    ///  * deletes cascade by the file's own foreign keys, newer child
    ///    tables included;
    ///  * the alternatives are worse for the person in that seat. They are
    ///    usually on the older build because the newer one let them down;
    ///    opening read-only would silently drop every fight of the session,
    ///    and refusing to open is the startup failure all over again.
    ///
    /// That holds only while schema changes stay ADDITIVE — new nullable or
    /// defaulted columns, new tables, new indexes. A change that removes or
    /// renames something, or changes what an existing column means, breaks
    /// every released build that opens the file afterwards, and bumping the
    /// version does not protect them (they never look). It needs its own
    /// plan — a new file name, at the least.</summary>
    private void StampVersion(int storedVersion)
    {
        if (storedVersion < SchemaVersion)
            Execute($"PRAGMA user_version = {SchemaVersion};");
    }

    public void Dispose() => _conn.Dispose();

    // ---- a file that cannot be opened ----

    /// <summary>SQLite's two verdicts on the FILE (primary result codes).
    /// SQLITE_CORRUPT: "database disk image is malformed" and "malformed
    /// database schema" both carry it, as do its extended forms (a corrupt
    /// index, sequence or virtual table). SQLITE_NOTADB: "file is not a
    /// database" — the first page does not start with SQLite's header, or
    /// names a file format this library cannot read.</summary>
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>Did opening or using the store fail because the file ITSELF
    /// is bad — the one case where setting it aside and starting a fresh
    /// archive is right?
    ///
    /// True for exactly two SQLite result codes, SQLITE_CORRUPT (11) and
    /// SQLITE_NOTADB (26): SQLite read the bytes and they are not a usable
    /// database. Waiting does not fix that and neither does a restart, so a
    /// caller that leaves the file in place fails the same way on every
    /// launch.
    ///
    /// False for everything else, on purpose, because every other failure
    /// is about the moment, the machine or this build — and the file may be
    /// a perfectly good archive:
    ///  * SQLITE_BUSY (5), SQLITE_LOCKED (6), SQLITE_PROTOCOL (15): someone
    ///    else holds it — a second copy of the app on the same data folder,
    ///    a backup or sync tool, an antivirus scan;
    ///  * SQLITE_CANTOPEN (14), SQLITE_PERM (3), SQLITE_AUTH (23),
    ///    SQLITE_READONLY (8): the path, the folder or the permissions (an
    ///    exclusive handle held by another process lands here too);
    ///  * SQLITE_IOERR (10), SQLITE_FULL (13), SQLITE_NOMEM (7): the disk,
    ///    the drive or the machine. A failing read CAN come from a dying
    ///    disk under a damaged file, but it can as easily be a network
    ///    share that dropped, and SQLite did not say the file is bad;
    ///  * SQLITE_ERROR (1), SQLITE_CONSTRAINT (19), SQLITE_SCHEMA (17) and
    ///    the rest: a statement was refused. That is this build's SQL
    ///    meeting a shape it did not expect (or a bug in a migration step),
    ///    and a build that is wrong about the file must not be the one
    ///    that moves it;
    ///  * anything that is not a SQLite error at all (an IO or access
    ///    exception from the folder, a missing native library).
    /// Moving a healthy archive aside would hide every fight the user has
    /// behind a fresh empty one; leaving a damaged one in place costs a
    /// session without an archive and a report that says why. When in
    /// doubt the answer is false.</summary>
    public static bool IsDamagedFile(Exception error) =>
        error is SqliteException { SqliteErrorCode: SqliteCorrupt or SqliteNotADatabase };

    /// <summary>Move a damaged database and its -wal / -shm siblings out of
    /// the way, never deleting anything: <c>history.db</c> becomes
    /// <c>history.db.corrupt-yyyyMMdd-HHmmss</c> (the settings files'
    /// quarantine naming, see PersistedJsonFile) and the siblings follow as
    /// <c>…-wal</c> and <c>…-shm</c> of THAT name — SQLite finds a write-ahead
    /// log by appending "-wal" to the database's file name, so the renamed
    /// set still opens as one database, uncheckpointed fights included, in
    /// any SQLite tool or a later build. Returns the new path of the main
    /// file.
    ///
    /// All or nothing. The main file goes first: if it will not move
    /// (another process has it open) nothing has changed. If a sibling then
    /// will not move, the main file is put back and the failure thrown —
    /// a database parted from its WAL has lost what the WAL held, and a
    /// fresh database created beside a stranger's WAL is worse. An earlier
    /// quarantined archive is never overwritten: a second one inside the
    /// same second gets "-2", "-3" before the sibling suffix.
    ///
    /// Unlike the settings files, nothing here is pruned. A quarantined
    /// archive is the user's fights, not a few kilobytes of options, and
    /// each one needs a newly damaged file to come into being.
    ///
    /// The caller must not hold the file open (a store whose constructor
    /// threw has already let go of it).</summary>
    public static string Quarantine(string path, DateTime now)
    {
        var stem = $"{path}.corrupt-{now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
        var target = stem;
        for (var n = 2; TargetTaken(target) && n < 1000; n++)
            target = $"{stem}-{n}";
        if (TargetTaken(target))
            throw new IOException($"No free quarantine name beside {Path.GetFileName(path)}.");

        File.Move(path, target);
        List<string> movedSiblings = [];
        try
        {
            foreach (var suffix in SiblingSuffixes)
            {
                if (!File.Exists(path + suffix))
                    continue;
                File.Move(path + suffix, target + suffix);
                movedSiblings.Add(suffix);
            }
        }
        catch (Exception)
        {
            // Undo, newest first. Best-effort: if the way back is blocked
            // too, the caller still learns the quarantine failed and must
            // not create a fresh database here.
            try
            {
                foreach (var suffix in movedSiblings.AsEnumerable().Reverse())
                    File.Move(target + suffix, path + suffix);
                File.Move(target, path);
            }
            catch (Exception)
            {
                // Reported by the throw below; nothing was deleted.
            }
            throw;
        }
        return target;

        static bool TargetTaken(string candidate) =>
            File.Exists(candidate) || SiblingSuffixes.Any(suffix => File.Exists(candidate + suffix));
    }

    /// <summary>The files SQLite keeps beside a WAL-mode database.</summary>
    private static readonly string[] SiblingSuffixes = ["-wal", "-shm"];

    /// <summary>Online snapshot to <paramref name="destinationPath"/> via
    /// SQLite's backup API — consistent even mid-write/WAL, unlike a file
    /// copy. Overwrites the destination. The destination is closed, and
    /// its file released, by the time this returns or throws (see
    /// <see cref="ConnectionString"/>), so the caller can rename a finished
    /// snapshot or delete a failed one.</summary>
    public void BackupTo(string destinationPath)
    {
        using var destination = new SqliteConnection(ConnectionString(destinationPath));
        destination.Open();
        _conn.BackupDatabase(destination);
    }

    /// <summary>The whole current shape, idempotently: creates what a new
    /// file lacks and leaves an existing table exactly as it is (bringing
    /// one of those up to date is <see cref="Migrate"/>'s job). It does not
    /// touch the version stamp — see <see cref="StampVersion"/>.</summary>
    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS encounters (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                source_id      TEXT NOT NULL,
                owner          TEXT NOT NULL,
                zone           TEXT NOT NULL,
                title          TEXT NOT NULL,
                start_ts       INTEGER NOT NULL,
                end_ts         INTEGER NOT NULL,
                duration_s     REAL NOT NULL,
                success        INTEGER NOT NULL,
                damage         INTEGER NOT NULL,
                correlation_id TEXT,
                saved_at       INTEGER NOT NULL,
                is_boss        INTEGER NOT NULL DEFAULT 0,
                zone_instance_expiry INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_encounters_start ON encounters(start_ts);
            CREATE INDEX IF NOT EXISTS idx_encounters_zone ON encounters(zone);
            CREATE INDEX IF NOT EXISTS idx_encounters_correlation ON encounters(correlation_id);
            CREATE INDEX IF NOT EXISTS idx_encounters_boss ON encounters(is_boss, start_ts);

            CREATE TABLE IF NOT EXISTS combatants (
                encounter_id INTEGER NOT NULL REFERENCES encounters(id) ON DELETE CASCADE,
                name         TEXT NOT NULL,
                damage       INTEGER NOT NULL,
                healed       INTEGER NOT NULL,
                damage_taken INTEGER NOT NULL,
                heals_taken  INTEGER NOT NULL,
                deaths       INTEGER NOT NULL,
                is_ally      INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_combatants_encounter ON combatants(encounter_id);

            CREATE TABLE IF NOT EXISTS swings (
                encounter_id  INTEGER NOT NULL REFERENCES encounters(id) ON DELETE CASCADE,
                category      INTEGER NOT NULL,
                critical      INTEGER NOT NULL,
                special       TEXT NOT NULL,
                attacker      TEXT NOT NULL,
                ability       TEXT NOT NULL,
                damage_number INTEGER NOT NULL,
                damage_text   TEXT NOT NULL,
                time_ts       INTEGER NOT NULL,
                time_sorter   INTEGER NOT NULL,
                victim        TEXT NOT NULL,
                damage_type   TEXT NOT NULL,
                extra         TEXT,
                observed_at_ms INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_swings_encounter ON swings(encounter_id, time_sorter);
            """);
    }

    /// <summary>Persist one completed encounter with combatant summaries and
    /// the full swing list (single transaction).</summary>
    public long SaveEncounter(Encounter encounter, string? correlationId = null)
    {
        using var tx = _conn.BeginTransaction();

        using var insertEnc = _conn.CreateCommand();
        insertEnc.Transaction = tx;
        insertEnc.CommandText = """
            INSERT INTO encounters (source_id, owner, zone, title, start_ts, end_ts, duration_s, success, damage, correlation_id, saved_at, is_boss, zone_instance_expiry)
            VALUES ($source, $owner, $zone, $title, $start, $end, $duration, $success, $damage, $correlation, unixepoch(), $boss, $instanceExpiry)
            RETURNING id;
            """;
        insertEnc.Parameters.AddWithValue("$source", encounter.SourceId);
        insertEnc.Parameters.AddWithValue("$owner", encounter.OwnerName);
        insertEnc.Parameters.AddWithValue("$zone", encounter.Zone);
        insertEnc.Parameters.AddWithValue("$title", encounter.Title);
        insertEnc.Parameters.AddWithValue("$start", encounter.StartTime.ToUnixTimeSeconds());
        insertEnc.Parameters.AddWithValue("$end", encounter.EndTime.ToUnixTimeSeconds());
        insertEnc.Parameters.AddWithValue("$duration", encounter.Duration.TotalSeconds);
        insertEnc.Parameters.AddWithValue("$success", (int)encounter.GetSuccessLevel());
        insertEnc.Parameters.AddWithValue("$damage", encounter.Damage);
        insertEnc.Parameters.AddWithValue("$correlation", (object?)correlationId ?? DBNull.Value);
        insertEnc.Parameters.AddWithValue("$boss", encounter.IsBossFight ? 1 : 0);
        insertEnc.Parameters.AddWithValue("$instanceExpiry",
            (object?)encounter.ZoneInstanceExpiry?.ToUnixTimeSeconds() ?? DBNull.Value);
        var encounterId = (long)insertEnc.ExecuteScalar()!;

        var allyKeys = new HashSet<string>(encounter.GetAllies().Select(a => a.Key), StringComparer.Ordinal);
        using var insertCombatant = _conn.CreateCommand();
        insertCombatant.Transaction = tx;
        insertCombatant.CommandText = """
            INSERT INTO combatants (encounter_id, name, damage, healed, damage_taken, heals_taken, deaths, is_ally)
            VALUES ($enc, $name, $damage, $healed, $taken, $healstaken, $deaths, $ally);
            """;
        foreach (var combatant in encounter.Combatants.Values)
        {
            insertCombatant.Parameters.Clear();
            insertCombatant.Parameters.AddWithValue("$enc", encounterId);
            insertCombatant.Parameters.AddWithValue("$name", combatant.Name);
            insertCombatant.Parameters.AddWithValue("$damage", combatant.Damage);
            insertCombatant.Parameters.AddWithValue("$healed", combatant.Healed);
            insertCombatant.Parameters.AddWithValue("$taken", combatant.DamageTaken);
            insertCombatant.Parameters.AddWithValue("$healstaken", combatant.HealsTaken);
            insertCombatant.Parameters.AddWithValue("$deaths", combatant.Deaths);
            insertCombatant.Parameters.AddWithValue("$ally", allyKeys.Contains(combatant.Key) ? 1 : 0);
            insertCombatant.ExecuteNonQuery();
        }

        using var insertSwing = _conn.CreateCommand();
        insertSwing.Transaction = tx;
        insertSwing.CommandText = """
            INSERT INTO swings (encounter_id, category, critical, special, attacker, ability, damage_number, damage_text, time_ts, time_sorter, victim, damage_type, extra, observed_at_ms)
            VALUES ($enc, $cat, $crit, $special, $attacker, $ability, $num, $text, $time, $sorter, $victim, $type, $extra, $observed);
            """;
        foreach (var swing in UniqueSwings(encounter))
        {
            insertSwing.Parameters.Clear();
            insertSwing.Parameters.AddWithValue("$enc", encounterId);
            insertSwing.Parameters.AddWithValue("$cat", (int)swing.Category);
            insertSwing.Parameters.AddWithValue("$crit", swing.Critical ? 1 : 0);
            insertSwing.Parameters.AddWithValue("$special", swing.Special);
            insertSwing.Parameters.AddWithValue("$attacker", swing.Attacker);
            insertSwing.Parameters.AddWithValue("$ability", swing.Ability);
            insertSwing.Parameters.AddWithValue("$num", swing.Damage.Number);
            insertSwing.Parameters.AddWithValue("$text", swing.Damage.TextOrDefault);
            insertSwing.Parameters.AddWithValue("$time", swing.Time.ToUnixTimeSeconds());
            insertSwing.Parameters.AddWithValue("$sorter", swing.TimeSorter);
            insertSwing.Parameters.AddWithValue("$victim", swing.Victim);
            insertSwing.Parameters.AddWithValue("$type", swing.DamageType);
            insertSwing.Parameters.AddWithValue("$extra", (object?)swing.Extra ?? DBNull.Value);
            insertSwing.Parameters.AddWithValue("$observed", (object?)swing.ObservedAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            insertSwing.ExecuteNonQuery();
        }

        tx.Commit();
        return encounterId;
    }

    /// <summary>Every swing exactly once: each combatant's outgoing reference
    /// bucket holds all their actions (buckets otherwise duplicate). Except
    /// self-damage — Encounter.AddSwing records it on the TAKEN side only,
    /// so it must be harvested from the incoming reference or archived
    /// fights silently lose that DamageTaken on restore.</summary>
    private static IEnumerable<Swing> UniqueSwings(Encounter encounter)
    {
        var outgoing = encounter.Combatants.Values
            .Where(c => c.OutgoingBuckets.ContainsKey(BucketConfig.AllOutgoingRef))
            .SelectMany(c => c.OutgoingBuckets[BucketConfig.AllOutgoingRef].All.Swings);
        var selfDamage = encounter.Combatants.Values
            .Where(c => c.IncomingBuckets.ContainsKey(BucketConfig.AllIncomingRef))
            .SelectMany(c => c.IncomingBuckets[BucketConfig.AllIncomingRef].All.Swings)
            .Where(s => s.Category is SwingCategory.Melee or SwingCategory.NonMelee
                && string.Equals(s.Attacker, s.Victim, StringComparison.OrdinalIgnoreCase));
        return outgoing.Concat(selfDamage).OrderBy(s => s.TimeSorter);
    }

    public IReadOnlyList<EncounterSummary> QueryEncounters(string? zone = null, int limit = 100) =>
        SearchEncounters(zone: zone, limit: limit);

    /// <summary>The archive query: filter by title/zone substring, start
    /// cutoff, and boss-ness in any combination; newest first.</summary>
    public IReadOnlyList<EncounterSummary> SearchEncounters(
        string? titleContains = null, string? zone = null,
        DateTimeOffset? since = null, bool? bossOnly = null, int limit = 100)
    {
        List<string> where = [];
        using var cmd = _conn.CreateCommand();
        if (titleContains is { Length: > 0 })
        {
            where.Add("(title LIKE $title ESCAPE '\\' OR zone LIKE $title ESCAPE '\\')");
            var escaped = titleContains.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$title", $"%{escaped}%");
        }
        if (zone is not null)
        {
            where.Add("zone = $zone");
            cmd.Parameters.AddWithValue("$zone", zone);
        }
        if (since is not null)
        {
            where.Add("start_ts >= $since");
            cmd.Parameters.AddWithValue("$since", since.Value.ToUnixTimeSeconds());
        }
        if (bossOnly is not null)
        {
            where.Add("is_boss = $boss");
            cmd.Parameters.AddWithValue("$boss", bossOnly.Value ? 1 : 0);
        }
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.CommandText = $"""
            SELECT id, source_id, owner, zone, title, start_ts, end_ts, duration_s, success, damage, correlation_id, is_boss, zone_instance_expiry
            FROM encounters
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY start_ts DESC LIMIT $limit;
            """;

        var results = new List<EncounterSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new EncounterSummary(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                reader.GetDouble(7), (SuccessLevel)reader.GetInt32(8), reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetInt32(11) == 1,
                reader.IsDBNull(12) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(12))));
        }
        return results;
    }

    public IReadOnlyList<CombatantSummary> LoadCombatants(long encounterId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, damage, healed, damage_taken, heals_taken, deaths, is_ally
            FROM combatants WHERE encounter_id = $enc ORDER BY damage DESC;
            """;
        cmd.Parameters.AddWithValue("$enc", encounterId);
        var results = new List<CombatantSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CombatantSummary(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt32(5), reader.GetInt32(6) == 1));
        }
        return results;
    }

    /// <summary>Reload the full swing list in original order — the replay path.</summary>
    public IReadOnlyList<Swing> LoadSwings(long encounterId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT category, critical, special, attacker, ability, damage_number, damage_text, time_ts, time_sorter, victim, damage_type, extra, observed_at_ms
            FROM swings WHERE encounter_id = $enc ORDER BY time_sorter;
            """;
        cmd.Parameters.AddWithValue("$enc", encounterId);
        var results = new List<Swing>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Swing(
                (SwingCategory)reader.GetInt32(0),
                reader.GetInt32(1) == 1,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                new DamageValue(reader.GetInt64(5), reader.GetString(6)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12))));
        }
        return results;
    }

    /// <summary>Rebuild a full Encounter (buckets, combatants, title) by
    /// replaying the stored swing list through the same AddSwing path live
    /// parsing uses — a restored fight drills exactly like a live one.</summary>
    public Encounter RestoreEncounter(EncounterSummary summary)
    {
        var encounter = new Encounter(summary.SourceId, summary.Owner, summary.Zone, summary.ZoneInstanceExpiry);
        foreach (var swing in LoadSwings(summary.Id))
            encounter.AddSwing(swing);
        encounter.End();
        // A scripted win was decided by a say line the swing replay can't
        // reproduce — carry the verdict that was stored at save time.
        // (For ordinary wins this is a no-op: the replayed deaths produce
        // Win again anyway.)
        if (summary.Success == SuccessLevel.Win)
            encounter.ScriptedWin = true;
        return encounter;
    }

    /// <summary>The identity a re-parse reproduces exactly: same log, same
    /// owner, same fight window, same title. Returns the stored id when the
    /// fight is already archived — the duplicate-save guard.</summary>
    public long? FindEncounter(string sourceId, string owner, DateTimeOffset start, DateTimeOffset end, string title)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM encounters
            WHERE source_id = $source AND owner = $owner AND start_ts = $start AND end_ts = $end AND title = $title
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$source", sourceId);
        cmd.Parameters.AddWithValue("$owner", owner);
        cmd.Parameters.AddWithValue("$start", start.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$end", end.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$title", title);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>One-time repair for archives that accumulated re-parse
    /// duplicates before the save guard existed: keeps the oldest row of
    /// each identity. Returns rows removed.</summary>
    public int RemoveDuplicateEncounters()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM encounters WHERE id NOT IN (
                SELECT MIN(id) FROM encounters
                GROUP BY source_id, owner, start_ts, end_ts, title
            );
            """;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Retention sweep for TRASH only: named (boss) fights stay in
    /// the archive until explicitly purged. Returns rows removed.</summary>
    public int PruneTrashBefore(DateTimeOffset cutoff)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM encounters WHERE is_boss = 0 AND start_ts < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToUnixTimeSeconds());
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Does the archive hold any fight at all? What the shutdown
    /// snapshot asks before it rotates the backups: an empty archive has
    /// nothing to protect and must not displace one that did.</summary>
    public bool HasEncounters()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM encounters);";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Stored swing count — the richer-data check for re-parse
    /// upgrades (new grammar shapes add swings old saves lack).</summary>
    public int CountSwings(long encounterId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM swings WHERE encounter_id = $enc;";
        cmd.Parameters.AddWithValue("$enc", encounterId);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool DeleteEncounter(long encounterId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM encounters WHERE id = $enc;";
        cmd.Parameters.AddWithValue("$enc", encounterId);
        return cmd.ExecuteNonQuery() > 0;
    }

    private void Execute(string sql, SqliteTransaction? transaction = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
