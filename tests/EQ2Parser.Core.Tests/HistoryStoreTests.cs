using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.History;

namespace EQ2Parser.Core.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eq2parser-history-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private HistoryStore Store() => new(Path.Combine(_dir, "history.db"));

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private static Encounter BuildEncounter()
    {
        var engine = new ParserEngine("log-a", "Menludiir");
        engine.ChangeZone("Deathtoll");
        Assert.True(engine.SetEncounter(T0, "Menludiir", "Lord Bob"));
        engine.AddSwing(SwingCategory.Melee, true, "Multi Attack", "Menludiir", "Strike", 1500, T0, "Lord Bob", "crushing");
        engine.AddSwing(SwingCategory.Healing, false, "None", "Menludiir", "Ward", 200, T0.AddSeconds(2), "Menludiir", EQ2Parser.Core.Grammar.EnglishGrammar.WardAbsorbType, extra: "remaining=4041");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", "Strike", DamageValue.Miss, T0.AddSeconds(4), "Lord Bob", "avoided");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", Combatant.KillingAbility, DamageValue.Death, T0.AddSeconds(6), "Lord Bob", "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    [Fact]
    public void Save_And_Query_Summaries()
    {
        using var store = Store();
        var id = store.SaveEncounter(BuildEncounter(), correlationId: "fight-1");
        Assert.True(id > 0);

        var summary = Assert.Single(store.QueryEncounters());
        Assert.Equal(("log-a", "Menludiir", "Deathtoll", "Lord Bob"), (summary.SourceId, summary.Owner, summary.Zone, summary.Title));
        Assert.Equal(SuccessLevel.Win, summary.Success);
        Assert.Equal(1500, summary.Damage);
        Assert.Equal("fight-1", summary.CorrelationId);

        Assert.Empty(store.QueryEncounters(zone: "The Emerald Halls"));
        Assert.Single(store.QueryEncounters(zone: "Deathtoll"));
    }

    [Fact]
    public void Swings_RoundTrip_Including_Sentinels_And_Extra()
    {
        using var store = Store();
        var id = store.SaveEncounter(BuildEncounter());

        var swings = store.LoadSwings(id);
        Assert.Equal(4, swings.Count);

        var crit = swings[0];
        Assert.Equal((SwingCategory.Melee, true, "Multi Attack", 1500L), (crit.Category, crit.Critical, crit.Special, crit.Damage.Number));

        var ward = swings[1];
        Assert.Equal("remaining=4041", ward.Extra);
        Assert.Equal(EQ2Parser.Core.Grammar.EnglishGrammar.WardAbsorbType, ward.DamageType);

        var miss = swings[2];
        Assert.True(miss.Damage.IsMiss); // sentinel equality survives the round-trip

        var death = swings[3];
        Assert.True(death.Damage.IsDeath);
        Assert.Equal(Combatant.KillingAbility, death.Ability);
    }

    [Fact]
    public void Combatant_Summaries_Carry_Ally_Flag()
    {
        using var store = Store();
        var id = store.SaveEncounter(BuildEncounter());

        var combatants = store.LoadCombatants(id);
        var menlu = combatants.Single(c => c.Name == "Menludiir");
        var boss = combatants.Single(c => c.Name == "Lord Bob");
        Assert.True(menlu.IsAlly);
        Assert.False(boss.IsAlly);
        Assert.Equal(1500, menlu.Damage);
        Assert.Equal(200, menlu.Healed);
        Assert.Equal(1, boss.Deaths);
    }

    [Fact]
    public void Delete_Cascades_To_Children()
    {
        using var store = Store();
        var id = store.SaveEncounter(BuildEncounter());
        Assert.True(store.DeleteEncounter(id));
        Assert.Empty(store.QueryEncounters());
        Assert.Empty(store.LoadSwings(id));
        Assert.Empty(store.LoadCombatants(id));
        Assert.False(store.DeleteEncounter(id));
    }

    [Fact]
    public void Reopening_The_Store_Preserves_Data()
    {
        var path = Path.Combine(_dir, "history.db");
        long id;
        using (var store = new HistoryStore(path))
            id = store.SaveEncounter(BuildEncounter());
        using (var store = new HistoryStore(path))
        {
            Assert.Single(store.QueryEncounters());
            Assert.Equal(4, store.LoadSwings(id).Count);
        }
    }

    [Fact]
    public void RestoreEncounter_Rebuilds_A_Drillable_Fight()
    {
        using var store = Store();
        var original = BuildEncounter();
        store.SaveEncounter(original);

        var restored = store.RestoreEncounter(Assert.Single(store.QueryEncounters()));

        // Same identity, totals, outcome, and structure as the live fight.
        Assert.Equal(original.Title, restored.Title);
        Assert.Equal(original.Zone, restored.Zone);
        Assert.Equal(original.Damage, restored.Damage);
        Assert.Equal(original.Healed, restored.Healed);
        Assert.Equal(original.Duration, restored.Duration);
        Assert.Equal(original.GetSuccessLevel(), restored.GetSuccessLevel());
        Assert.Equal(original.Combatants.Count, restored.Combatants.Count);
        Assert.False(restored.Active);
        // The rebuilt buckets drill: the owner's outgoing reference bucket
        // carries the same swing count.
        Assert.Equal(
            original.Combatants["MENLUDIIR"].OutgoingBuckets[BucketConfig.AllOutgoingRef].All.Swings.Count,
            restored.Combatants["MENLUDIIR"].OutgoingBuckets[BucketConfig.AllOutgoingRef].All.Swings.Count);
    }

    [Fact]
    public void SelfDamage_Survives_The_Archive_RoundTrip()
    {
        // Self-damage (lifetap procs) records on the TAKEN side only — the
        // outgoing-reference sweep used to miss it, so restored fights
        // silently lost that DamageTaken.
        var engine = new ParserEngine("log-a", "Menludiir");
        engine.ChangeZone("Deathtoll");
        Assert.True(engine.SetEncounter(T0, "Menludiir", "Lord Bob"));
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", "Strike", 1500, T0, "Lord Bob", "crushing");
        engine.AddSwing(SwingCategory.NonMelee, false, "None", "Menludiir", "Vampiric Requiem", 320, T0.AddSeconds(1), "Menludiir", "magic");
        engine.EndCombat();
        var original = engine.History[^1];
        Assert.Equal(320, original.Combatants["MENLUDIIR"].DamageTaken);

        using var store = Store();
        store.SaveEncounter(original);
        var restored = store.RestoreEncounter(Assert.Single(store.QueryEncounters()));

        Assert.Equal(320, restored.Combatants["MENLUDIIR"].DamageTaken);
        Assert.Equal(original.Damage, restored.Damage); // self-damage still not credited as outgoing
    }

    [Fact]
    public void PruneTrash_Never_Touches_Bosses()
    {
        using var store = Store();
        // "Lord Bob" is a named fight — a boss — and immune to the sweep.
        store.SaveEncounter(BuildEncounter());
        Assert.Equal(0, store.PruneTrashBefore(T0.AddDays(1)));
        var summary = Assert.Single(store.QueryEncounters());
        Assert.True(summary.IsBoss);
        // Explicit purge is the only way a boss leaves.
        Assert.True(store.DeleteEncounter(summary.Id));
        Assert.Empty(store.QueryEncounters());
    }

    [Fact]
    public void SearchEncounters_Filters_By_Title_Since_And_Bossness()
    {
        using var store = Store();
        store.SaveEncounter(BuildEncounter());

        Assert.Single(store.SearchEncounters(titleContains: "lord"));
        Assert.Single(store.SearchEncounters(titleContains: "Deathtoll")); // zone matches too
        Assert.Empty(store.SearchEncounters(titleContains: "Mayong"));
        Assert.Empty(store.SearchEncounters(titleContains: "%")); // wildcards are literal
        Assert.Single(store.SearchEncounters(since: T0));
        Assert.Empty(store.SearchEncounters(since: T0.AddHours(1)));
        Assert.Single(store.SearchEncounters(bossOnly: true));
        Assert.Empty(store.SearchEncounters(bossOnly: false));
    }

    [Fact]
    public void FindEncounter_Matches_The_Reparse_Identity()
    {
        using var store = Store();
        var encounter = BuildEncounter();
        var id = store.SaveEncounter(encounter);

        Assert.Equal(id, store.FindEncounter(
            encounter.SourceId, encounter.OwnerName, encounter.StartTime, encounter.EndTime, encounter.Title));
        Assert.Null(store.FindEncounter(
            "other-log", encounter.OwnerName, encounter.StartTime, encounter.EndTime, encounter.Title));
        Assert.Null(store.FindEncounter(
            encounter.SourceId, encounter.OwnerName, encounter.StartTime.AddSeconds(1), encounter.EndTime, encounter.Title));
    }

    [Fact]
    public void RemoveDuplicateEncounters_Keeps_The_Oldest_Copy()
    {
        using var store = Store();
        var encounter = BuildEncounter();
        var first = store.SaveEncounter(encounter);
        store.SaveEncounter(encounter);
        store.SaveEncounter(encounter);
        Assert.Equal(3, store.QueryEncounters().Count);

        Assert.Equal(2, store.RemoveDuplicateEncounters());
        Assert.Equal(first, Assert.Single(store.QueryEncounters()).Id);
        // Cascade removed the duplicate swing sets too — the survivor drills.
        Assert.Equal(4, store.LoadSwings(first).Count);
    }

    [Fact]
    public void V1_Database_Migrates_In_Place_With_Boss_Backfill()
    {
        // Replicate the v1 shape exactly (no is_boss column) with one named
        // and one trash row, then open it with the current store.
        var path = Path.Combine(_dir, "old.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE encounters (
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
                    saved_at       INTEGER NOT NULL
                );
                INSERT INTO encounters (source_id, owner, zone, title, start_ts, end_ts, duration_s, success, damage, saved_at)
                VALUES ('log-a', 'Menludiir', 'Deathtoll', 'Mayong Mistmoore', 1000, 1060, 60, 1, 5000, 1000),
                       ('log-a', 'Menludiir', 'Deathtoll', 'a vampire thrall', 2000, 2020, 20, 1, 500, 2000);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new HistoryStore(path);
        var all = store.QueryEncounters();
        Assert.Equal(2, all.Count);
        Assert.True(all.Single(s => s.Title == "Mayong Mistmoore").IsBoss);
        Assert.False(all.Single(s => s.Title == "a vampire thrall").IsBoss);
        // And the store keeps working on the migrated file.
        store.SaveEncounter(BuildEncounter());
        Assert.Equal(3, store.QueryEncounters().Count);
    }

    // ---- schema version: forward migration, downgrade stamps, newer files ----
    //
    // The stamp (PRAGMA user_version) cannot be trusted downward: every
    // build up to v0.5.7 writes ITS OWN version on every open, so running an
    // older build once (a portable copy, a rollback, a second install)
    // lowers the stamp on a file that keeps its newer shape. The tests below
    // drive the stamp by hand over a real file to stand in for those builds.

    private static void Sql(string path, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HashSet<string> Columns(string path, string table)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info($table);";
        cmd.Parameters.AddWithValue("$table", table);
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static long UserVersion(string path) => Scalar(path, "PRAGMA user_version;");

    /// <summary>This build's schema version, read off a file it just
    /// created — so the tests below follow SchemaVersion without naming it.</summary>
    private long CurrentSchemaVersion()
    {
        var path = Path.Combine(_dir, "probe.db");
        using (new HistoryStore(path)) { }
        return UserVersion(path);
    }

    private static readonly DateTimeOffset InstanceExpiry = T0.AddDays(7);

    /// <summary>A fight that fills every column a migration ever added.</summary>
    private static Encounter BuildInstancedEncounter()
    {
        var engine = new ParserEngine("log-a", "Menludiir");
        engine.ChangeZone("Deathtoll");
        engine.SeedInstanceExpiry(InstanceExpiry);
        Assert.True(engine.SetEncounter(T0, "Menludiir", "Lord Bob"));
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", "Strike", 1500, T0, "Lord Bob", "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Menludiir", Combatant.KillingAbility, DamageValue.Death, T0.AddSeconds(6), "Lord Bob", "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    [Fact]
    public void Brand_New_File_Gets_The_Full_Schema()
    {
        var path = Path.Combine(_dir, "fresh.db");
        using (var store = new HistoryStore(path))
        {
            // Every column the insert and select lists name exists, so the
            // file reads and writes at once — no migration ran to get here.
            store.SaveEncounter(BuildInstancedEncounter());
            var summary = Assert.Single(store.QueryEncounters());
            Assert.True(summary.IsBoss);
            Assert.Equal(InstanceExpiry, summary.ZoneInstanceExpiry);
        }

        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal) { "id", "source_id", "owner", "zone", "title", "start_ts", "end_ts", "duration_s", "success", "damage", "correlation_id", "saved_at", "is_boss", "zone_instance_expiry" },
            Columns(path, "encounters"));
        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal) { "encounter_id", "name", "damage", "healed", "damage_taken", "heals_taken", "deaths", "is_ally" },
            Columns(path, "combatants"));
        Assert.Superset(
            new HashSet<string>(StringComparer.Ordinal) { "encounter_id", "category", "critical", "special", "attacker", "ability", "damage_number", "damage_text", "time_ts", "time_sorter", "victim", "damage_type", "extra", "observed_at_ms" },
            Columns(path, "swings"));
        Assert.True(UserVersion(path) >= 3);
        Assert.Equal(
            5,
            Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('idx_encounters_start', 'idx_encounters_zone', 'idx_encounters_correlation', 'idx_encounters_boss', 'idx_swings_encounter');"));
    }

    [Fact]
    public void Current_File_Stamped_Down_To_Any_Older_Version_Reopens_With_Its_Rows()
    {
        // The reported failure: a newer file an older build has opened once.
        // The columns are all there; only the stamp went backwards. Migrate
        // used to re-run ADD COLUMN off the stamp, SQLite answered
        // "duplicate column name", the constructor threw, and the app could
        // not start while the archive sat intact on disk.
        var current = CurrentSchemaVersion();
        for (var older = 0L; older < current; older++)
        {
            var path = Path.Combine(_dir, $"stamped-down-{older}.db");
            long id;
            using (var store = new HistoryStore(path))
                id = store.SaveEncounter(BuildInstancedEncounter());
            // A verdict the title rule would NOT reproduce — proves the v1
            // backfill does not run again over rows that already have one.
            Sql(path, "UPDATE encounters SET is_boss = 0;");
            Sql(path, $"PRAGMA user_version = {older};");

            // Twice: the second open must be as uneventful as the first.
            for (var open = 0; open < 2; open++)
            {
                using var store = new HistoryStore(path);
                var summary = Assert.Single(store.QueryEncounters());
                Assert.Equal((id, "Lord Bob"), (summary.Id, summary.Title));
                Assert.False(summary.IsBoss);
                Assert.Equal(InstanceExpiry, summary.ZoneInstanceExpiry);
                Assert.Equal(2, store.LoadSwings(id).Count);
                Assert.Equal(2, store.LoadCombatants(id).Count);
            }

            // Back on this build the stamp is this build's again, and the
            // file still takes writes.
            Assert.Equal(current, UserVersion(path));
            using (var store = new HistoryStore(path))
            {
                store.SaveEncounter(BuildEncounter());
                Assert.Equal(2, store.QueryEncounters().Count);
            }
        }
    }

    [Fact]
    public void Rows_An_Older_Build_Wrote_Into_A_Current_File_Survive_The_Return()
    {
        // The whole round trip, not just the stamp: the older build also
        // SAVES fights while it is there, naming only the columns it knows.
        var path = Path.Combine(_dir, "round-trip.db");
        using (var store = new HistoryStore(path))
            store.SaveEncounter(BuildInstancedEncounter());
        Sql(path, """
            INSERT INTO encounters (source_id, owner, zone, title, start_ts, end_ts, duration_s, success, damage, saved_at, is_boss)
            VALUES ('log-a', 'Menludiir', 'Deathtoll', 'Lord Older', 5000, 5060, 60, 1, 777, 5000, 1);
            PRAGMA user_version = 2;
            """);

        using var reopened = new HistoryStore(path);
        var all = reopened.QueryEncounters();
        Assert.Equal(2, all.Count);
        var older = all.Single(s => s.Title == "Lord Older");
        Assert.True(older.IsBoss);
        Assert.Null(older.ZoneInstanceExpiry); // the column it never knew stays NULL
        Assert.Equal(InstanceExpiry, all.Single(s => s.Title == "Lord Bob").ZoneInstanceExpiry);
    }

    [Fact]
    public void File_Stamped_Higher_Than_This_Build_Is_Not_Stamped_Down()
    {
        // A file from a NEWER build. Lowering its stamp is exactly what set
        // up the failure above for the build that comes back to it, so this
        // build leaves the number alone — and still opens the file read/write
        // (see the reasoning on HistoryStore.StampVersion).
        var current = CurrentSchemaVersion();
        foreach (var newer in new[] { current + 1, current + 40 })
        {
            var path = Path.Combine(_dir, $"stamped-up-{newer}.db");
            long id;
            using (var store = new HistoryStore(path))
                id = store.SaveEncounter(BuildInstancedEncounter());
            Sql(path, $"PRAGMA user_version = {newer};");

            for (var open = 0; open < 2; open++)
            {
                using (var store = new HistoryStore(path))
                {
                    Assert.Equal(id, Assert.Single(store.QueryEncounters()).Id);
                    Assert.Equal(2, store.LoadSwings(id).Count);
                }
                Assert.Equal(newer, UserVersion(path));
            }
        }
    }

    [Fact]
    public void Newer_Additive_Schema_Stays_Readable_And_Writable()
    {
        // What a later build is allowed to do to the file (see the rule on
        // HistoryStore.StampVersion): add nullable or defaulted columns, add
        // tables, add indexes. This build names its columns in every
        // statement, so none of that is visible to it — it reads, saves and
        // deletes as before, and the newer build's data rides along.
        var path = Path.Combine(_dir, "future.db");
        long id;
        using (var store = new HistoryStore(path))
            id = store.SaveEncounter(BuildInstancedEncounter());
        var future = UserVersion(path) + 1;
        Sql(path, $"""
            ALTER TABLE encounters ADD COLUMN future_note TEXT;
            ALTER TABLE encounters ADD COLUMN future_flag INTEGER NOT NULL DEFAULT 7;
            ALTER TABLE swings ADD COLUMN future_detail TEXT;
            CREATE TABLE future_things (encounter_id INTEGER NOT NULL REFERENCES encounters(id) ON DELETE CASCADE, payload TEXT NOT NULL);
            CREATE INDEX idx_future_things ON future_things(encounter_id);
            UPDATE encounters SET future_note = 'kept';
            INSERT INTO future_things (encounter_id, payload) VALUES ({id}, 'kept');
            PRAGMA user_version = {future};
            """);

        using (var store = new HistoryStore(path))
        {
            Assert.Equal(id, Assert.Single(store.QueryEncounters()).Id);
            var saved = store.SaveEncounter(BuildEncounter());
            Assert.Equal(2, store.QueryEncounters().Count);
            Assert.Equal(4, store.LoadSwings(saved).Count);
            Assert.NotNull(store.RestoreEncounter(store.QueryEncounters().Single(s => s.Id == saved)));
            Assert.True(store.DeleteEncounter(saved));
        }

        Assert.Equal(future, UserVersion(path));
        Assert.Contains("future_note", Columns(path, "encounters"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM encounters WHERE future_note = 'kept' AND future_flag = 7;"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM future_things;"));
    }

    [Fact]
    public void V2_Database_Migrates_Forward_And_Is_Stamped_Current()
    {
        // The v2 shape exactly (is_boss, no zone_instance_expiry) — what
        // every release before v0.3.2 left on disk.
        var path = Path.Combine(_dir, "v2.db");
        Sql(path, """
            CREATE TABLE encounters (
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
                is_boss        INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO encounters (source_id, owner, zone, title, start_ts, end_ts, duration_s, success, damage, saved_at, is_boss)
            VALUES ('log-a', 'Menludiir', 'Deathtoll', 'Mayong Mistmoore', 1000, 1060, 60, 1, 5000, 1000, 1),
                   ('log-a', 'Menludiir', 'Deathtoll', 'Lord Unflagged', 2000, 2020, 20, 1, 500, 2000, 0);
            PRAGMA user_version = 2;
            """);

        using (var store = new HistoryStore(path))
        {
            var all = store.QueryEncounters();
            Assert.Equal(2, all.Count);
            Assert.All(all, s => Assert.Null(s.ZoneInstanceExpiry));
            // is_boss was already this file's own: the v1 backfill is not
            // for it, and must not promote the row v2 stored as trash.
            Assert.True(all.Single(s => s.Title == "Mayong Mistmoore").IsBoss);
            Assert.False(all.Single(s => s.Title == "Lord Unflagged").IsBoss);

            store.SaveEncounter(BuildInstancedEncounter());
            Assert.Equal(InstanceExpiry, store.QueryEncounters().Single(s => s.Title == "Lord Bob").ZoneInstanceExpiry);
        }

        Assert.Contains("zone_instance_expiry", Columns(path, "encounters"));
        Assert.Equal(CurrentSchemaVersion(), UserVersion(path));
    }

    [Fact]
    public void Old_Shape_Under_A_Wrong_Stamp_Still_Migrates()
    {
        // The other half of "the stamp is only a hint": a file with the v1
        // SHAPE whose stamp says something else entirely (never written, or
        // written by a build that died between creating the table and
        // stamping it). The columns decide, so it still gains what it lacks.
        var current = CurrentSchemaVersion();
        foreach (var stamp in new[] { 0L, current, current + 1 })
        {
            var path = Path.Combine(_dir, $"mis-stamped-{stamp}.db");
            Sql(path, $"""
                CREATE TABLE encounters (
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
                    saved_at       INTEGER NOT NULL
                );
                INSERT INTO encounters (source_id, owner, zone, title, start_ts, end_ts, duration_s, success, damage, saved_at)
                VALUES ('log-a', 'Menludiir', 'Deathtoll', 'Mayong Mistmoore', 1000, 1060, 60, 1, 5000, 1000),
                       ('log-a', 'Menludiir', 'Deathtoll', 'a vampire thrall', 2000, 2020, 20, 1, 500, 2000);
                PRAGMA user_version = {stamp};
                """);

            using (var store = new HistoryStore(path))
            {
                var all = store.QueryEncounters();
                Assert.True(all.Single(s => s.Title == "Mayong Mistmoore").IsBoss);
                Assert.False(all.Single(s => s.Title == "a vampire thrall").IsBoss);
                store.SaveEncounter(BuildInstancedEncounter());
                Assert.Equal(3, store.QueryEncounters().Count);
            }
            Assert.Equal(Math.Max(stamp, current), UserVersion(path));
        }
    }
}
