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
    private const int SchemaVersion = 3;
    private readonly SqliteConnection _conn;

    public HistoryStore(string path)
    {
        _conn = new SqliteConnection($"Data Source={path}");
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
    /// backfill ran" — a crash between the two leaves neither.</summary>
    private void Migrate()
    {
        var columns = ColumnNames("encounters");
        // A brand-new file: no table to alter. CreateSchema builds the
        // current shape outright (ALTER here would fail on "no such table").
        if (columns.Count == 0)
            return;

        List<string> steps = [];
        // v2: is_boss, backfilled from the same named-mob rule the
        // classifier uses. The backfill belongs to the column's CREATION
        // only: on a file that already has is_boss the stored verdicts are
        // the truth and must not be rewritten from the title.
        if (!columns.Contains("is_boss"))
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
        if (!columns.Contains("zone_instance_expiry"))
            steps.Add("ALTER TABLE encounters ADD COLUMN zone_instance_expiry INTEGER;");

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

    /// <summary>Online snapshot to <paramref name="destinationPath"/> via
    /// SQLite's backup API — consistent even mid-write/WAL, unlike a file
    /// copy. Overwrites the destination.</summary>
    public void BackupTo(string destinationPath)
    {
        using var destination = new SqliteConnection($"Data Source={destinationPath}");
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
