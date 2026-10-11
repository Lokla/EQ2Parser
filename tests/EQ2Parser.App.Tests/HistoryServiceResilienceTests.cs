using System.IO;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;
using Microsoft.Data.Sqlite;

namespace EQ2Parser.App.Tests;

/// <summary>
/// The archive is never what stops the app. A history.db that cannot be
/// opened leaves a session that still parses (the damaged file set aside,
/// or the unreachable one left alone); a fight that cannot be restored
/// costs that fight; a save that fails costs that save — and each of them
/// leaves a crash report, a journal line and a property that says so.
/// </summary>
public sealed class HistoryServiceResilienceTests : IDisposable
{
    private readonly TestDataDir _dir = new();
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    /// <summary>Wide enough that the fixed test dates are always "recent".</summary>
    private const int AllDays = 100_000;

    private string Db => _dir.FileIn("history.db");

    public HistoryServiceResilienceTests()
    {
        // Each test asserts on the report IT caused.
        CrashLog.ResetRateLimit();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    /// <summary>A distinct boss fight per <paramref name="n"/>.</summary>
    private static Encounter Fight(int n)
    {
        var start = T0.AddMinutes(10 * n);
        var boss = $"Lord Oak {n}";
        var engine = new ParserEngine("log-a", "Alder");
        engine.ChangeZone("The Oak Halls");
        Assert.True(engine.SetEncounter(start, "Alder", boss));
        engine.AddSwing(SwingCategory.Melee, false, "None", "Alder", "Strike", 1000, start, boss, "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", "Alder", Combatant.KillingAbility, DamageValue.Death, start.AddSeconds(10), boss, "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    /// <summary>One launch, the given fights, one clean exit.</summary>
    private static void Session(params Encounter[] fights)
    {
        using var history = new HistoryService();
        Assert.Equal(ArchiveState.Healthy, history.State);
        foreach (var fight in fights)
            history.QueueSave(fight);
    }

    private void Sql(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={Db};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string[] Reports(string kind) =>
        Directory.Exists(CrashLog.Directory)
            ? Directory.GetFiles(CrashLog.Directory, $"crash-*-{kind}*.txt")
            : [];

    private static string Journal() =>
        File.Exists(SessionJournal.FilePath) ? File.ReadAllText(SessionJournal.FilePath) : "";

    private string[] QuarantinedFiles() => Directory.GetFiles(_dir.Path, "history.db.corrupt-*");

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    // ── A file SQLite calls bad: set aside, fresh archive ───────────────────

    [Fact]
    public async Task A_File_Of_Garbage_Is_Quarantined_And_A_Fresh_Archive_Works()
    {
        var garbage = new string('x', 8192);
        File.WriteAllText(Db, garbage);

        // Before: this constructor threw, which failed every launch.
        using var history = new HistoryService();

        Assert.Equal(ArchiveState.Quarantined, history.State);
        Assert.True(history.IsAvailable);
        // Renamed, never deleted — and the bytes are the same bytes.
        var quarantined = history.QuarantinedPath;
        Assert.NotNull(quarantined);
        Assert.StartsWith(Db + ".corrupt-", quarantined);
        Assert.Equal(garbage, File.ReadAllText(quarantined));
        // The evidence says where the fights went and that they are kept.
        var name = Path.GetFileName(quarantined);
        Assert.Contains(name, history.DegradedReason);
        Assert.Contains("Nothing was deleted", history.DegradedReason);
        Assert.Equal(history.DegradedReason, history.Problem);
        var report = File.ReadAllText(Assert.Single(Reports(HistoryService.QuarantinedKind)));
        Assert.Contains(name, report);
        Assert.Contains("Nothing was deleted", report);
        Assert.Contains("SqliteException", report);
        var journal = Journal();
        Assert.Contains("archive QUARANTINED", journal);
        Assert.Contains(name, journal);
        Assert.Contains("SqliteException 26", journal);
        // The journal names files, never a path.
        Assert.DoesNotContain(_dir.Path, journal);

        // And the fresh archive is a working one.
        history.QueueSave(Fight(1));
        await WaitFor(() => history.Search(null, null).Count == 1, "the fresh archive to take a save");
        Assert.Equal("Lord Oak 1", Assert.Single(history.Search(null, null)).Title);
        Assert.Equal(0, history.SaveFailures);
    }

    [Fact]
    public void A_Real_Archive_Cut_Short_Is_Quarantined_With_Every_Byte_It_Had()
    {
        // Not garbage: a genuine archive that lost its second half (a copy
        // that did not finish, a disk that filled mid-write). The header is
        // intact, so SQLite's verdict is "malformed", not "not a database".
        Session([.. Enumerable.Range(1, 30).Select(Fight)]);
        var cut = File.ReadAllBytes(Db);
        cut = cut[..(cut.Length / 2)];
        File.WriteAllBytes(Db, cut);

        using var history = new HistoryService();
        var restored = history.RestoreInto(new EncounterCorrelator(), AllDays, AllDays);

        Assert.Equal(ArchiveState.Quarantined, history.State);
        Assert.Equal(cut, File.ReadAllBytes(history.QuarantinedPath!));
        Assert.Equal(0, restored);
        Assert.Contains("SqliteException 11", Journal());
    }

    [Fact]
    public void A_Quarantined_Archive_Is_Still_There_After_The_Next_Clean_Launch()
    {
        File.WriteAllText(Db, new string('x', 8192));
        string quarantined;
        using (var history = new HistoryService())
        {
            quarantined = history.QuarantinedPath!;
            history.QueueSave(Fight(1));
        }

        // The next launch finds a healthy archive and has nothing to say —
        // and does not tidy the quarantined one away.
        using (var next = new HistoryService())
        {
            Assert.Equal(ArchiveState.Healthy, next.State);
            Assert.Null(next.Problem);
            Assert.Single(next.Search(null, null));
        }
        Assert.True(File.Exists(quarantined));
        Assert.Single(QuarantinedFiles());
    }

    [Fact]
    public void A_Damaged_File_That_Will_Not_Move_Is_Left_In_Place_Without_An_Archive()
    {
        var garbage = new string('x', 8192);
        File.WriteAllText(Db, garbage);
        // Another process has it open in a way that allows reading (so
        // SQLite can see it is not a database) but not renaming.
        using (new FileStream(Db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using var history = new HistoryService();

            Assert.Equal(ArchiveState.Unavailable, history.State);
            Assert.False(history.IsAvailable);
            Assert.Null(history.QuarantinedPath);
            Assert.Contains("could not be moved aside", history.DegradedReason);
        }

        Assert.Equal(garbage, File.ReadAllText(Db));
        Assert.Empty(QuarantinedFiles());
        Assert.Single(Reports(HistoryService.UnavailableKind));
        Assert.Contains("archive UNAVAILABLE", Journal());
    }

    // ── A file that is merely out of reach: left alone, no archive ──────────

    [Fact]
    public void A_Locked_File_Is_Left_Alone_And_The_Session_Runs_Without_An_Archive()
    {
        Session(Fight(1));
        var before = File.ReadAllBytes(Db);
        var correlator = new EncounterCorrelator();

        // Held exclusively by something else: a backup tool, a sync
        // client, an antivirus scan.
        using (new FileStream(Db, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var history = new HistoryService();

            Assert.Equal(ArchiveState.Unavailable, history.State);
            Assert.False(history.IsAvailable);
            Assert.Null(history.QuarantinedPath);
            Assert.Contains("left exactly as it is", history.DegradedReason);
            Assert.Equal(history.DegradedReason, history.Problem);

            // Everything the app calls still answers, as an empty archive
            // would: fights parse and show, nothing is saved.
            history.QueueSave(Fight(2));
            Assert.Equal(0, history.RestoreInto(correlator, AllDays, AllDays));
            Assert.Empty(history.Search(null, null));
            Assert.Empty(history.ArchivedZones());
            Assert.Empty(history.EnumerateArchivedFights("The Oak Halls"));
            Assert.False(history.IsLoaded(1));
            history.PurgeFromArchive(1);
            Assert.Equal(0, history.SaveFailures);
        }

        // Not renamed, not rewritten, not backed up over.
        Assert.Empty(QuarantinedFiles());
        Assert.Equal(before, File.ReadAllBytes(Db));
        var report = File.ReadAllText(Assert.Single(Reports(HistoryService.UnavailableKind)));
        Assert.Contains("left exactly as it is", report);
        Assert.Empty(Reports(HistoryService.QuarantinedKind));
        var journal = Journal();
        Assert.Contains("archive UNAVAILABLE", journal);
        Assert.Contains("left untouched", journal);
        Assert.DoesNotContain(_dir.Path, journal);

        // Once whatever held it lets go, the archive is exactly what it was.
        using var later = new HistoryService();
        Assert.Equal(ArchiveState.Healthy, later.State);
        Assert.Equal("Lord Oak 1", Assert.Single(later.Search(null, null)).Title);
    }

    [Fact]
    public void A_Healthy_File_Is_Untouched()
    {
        Session(Fight(1), Fight(2));

        using (var history = new HistoryService())
        {
            Assert.Equal(ArchiveState.Healthy, history.State);
            Assert.True(history.IsAvailable);
            Assert.Null(history.DegradedReason);
            Assert.Null(history.QuarantinedPath);
            Assert.Null(history.Problem);
            Assert.Equal(2, history.Search(null, null).Count);
        }

        Assert.Empty(QuarantinedFiles());
        Assert.Empty(Reports("archive"));
        Assert.DoesNotContain("archive", Journal());
    }

    // ── Restore: one unreadable fight costs that fight ──────────────────────

    [Fact]
    public void A_Fight_That_Cannot_Be_Restored_Costs_That_Fight_Not_The_Launch()
    {
        Session(Fight(1), Fight(2), Fight(3));
        // A stored value no fight can be rebuilt from, standing in for a
        // damaged page SQLite only finds when that fight's swings are read.
        Sql("UPDATE swings SET time_ts = 9000000000000000 WHERE encounter_id = (SELECT id FROM encounters WHERE title = 'Lord Oak 2');");
        var correlator = new EncounterCorrelator();

        using var history = new HistoryService();
        // Before: this threw, inside App startup — a failed launch, every launch.
        var restored = history.RestoreInto(correlator, AllDays, AllDays);

        Assert.Equal(2, restored);
        Assert.Equal(["Lord Oak 1", "Lord Oak 3"], correlator.History.Select(f => f.Title).Order());
        Assert.Equal(1, history.RestoreFailures);
        Assert.NotNull(history.RestoreError);
        Assert.Contains("1 recent fight could not be loaded", history.Problem);
        // The archive is left in place: most of it is readable.
        Assert.Equal(ArchiveState.Healthy, history.State);
        Assert.Empty(QuarantinedFiles());
        Assert.Equal(3, history.Search(null, null).Count);
        var report = File.ReadAllText(Assert.Single(Reports(HistoryService.RestoreKind)));
        Assert.Contains("1 of 3 recent fights", report);
        Assert.Contains("archive restore: 1 of 3 recent fights could not be loaded", Journal());
    }

    [Fact]
    public void A_Restore_That_Cannot_Even_List_The_Fights_Restores_None_And_Says_So()
    {
        Session(Fight(1));
        Sql("UPDATE encounters SET start_ts = 9000000000000000;");
        var correlator = new EncounterCorrelator();

        using var history = new HistoryService();
        var restored = history.RestoreInto(correlator, AllDays, AllDays);

        Assert.Equal(0, restored);
        Assert.Empty(correlator.History);
        Assert.NotNull(history.RestoreError);
        Assert.Contains("recent fights could not be loaded", history.Problem);
        Assert.Single(Reports(HistoryService.RestoreKind));
        Assert.Contains("archive restore FAILED", Journal());
    }

    // ── Saves: a failure is counted, reported and journaled ─────────────────

    [Fact]
    public async Task A_Failing_Save_Is_Counted_Reported_And_Journaled_Sparingly()
    {
        using (var history = new HistoryService())
        {
            // The archive stops taking writes mid-session (a full disk in
            // real life; here the table the insert names is gone).
            Sql("DROP TABLE swings;");

            history.QueueSave(Fight(1));
            // The count is published LAST, so once it moves the evidence
            // is already on disk.
            await WaitFor(() => history.SaveFailures == 1, "the first save to fail");

            Assert.IsType<SqliteException>(history.LastSaveError);
            Assert.Contains("1 fight from this session could not be saved", history.Problem);
            var report = File.ReadAllText(Assert.Single(Reports(HistoryService.SaveKind)));
            Assert.Contains("SqliteException", report);
            Assert.Contains("   at ", report); // a stack trace, not just a message
            Assert.Contains("Lord Oak 1", report);
            Assert.Contains("archive save FAILED: SqliteException 1 (1 this session)", Journal());

            // A failure that is there to stay: one more per fight.
            for (var n = 2; n <= 12; n++)
            {
                CrashLog.ResetRateLimit(); // as if each fight were minutes apart
                history.QueueSave(Fight(n));
                await WaitFor(() => history.SaveFailures == n, $"save {n} to fail");
            }

            // Every failure counted, three reports, and a journal that has
            // the 1st and the 10th but not one line per fight.
            Assert.Equal(12, history.SaveFailures);
            Assert.Equal(3, Reports(HistoryService.SaveKind).Length);
            var journal = Journal();
            Assert.Contains("(10 this session)", journal);
            Assert.DoesNotContain("(2 this session)", journal);
            Assert.DoesNotContain("(12 this session)", journal);
            Assert.Equal(2, journal.Split("archive save FAILED").Length - 1);
        }

        // The exit leaves the total, which the sparse lines above cannot.
        Assert.Contains("archive: 12 fight saves failed this session (last: SqliteException 1)", Journal());
    }

    [Fact]
    public async Task A_Failed_Save_Does_Not_Stop_The_Next_One()
    {
        using var history = new HistoryService();
        // One fight the store cannot take (a trigger added behind its back
        // refuses that title), then a normal one.
        Sql("CREATE TRIGGER refuse_oak_1 BEFORE INSERT ON encounters WHEN NEW.title = 'Lord Oak 1' BEGIN SELECT RAISE(ABORT, 'refused'); END;");

        history.QueueSave(Fight(1));
        history.QueueSave(Fight(2));
        await WaitFor(() => history.Search(null, null).Count == 1, "the second fight to be saved");

        Assert.Equal(1, history.SaveFailures);
        Assert.Equal("Lord Oak 2", Assert.Single(history.Search(null, null)).Title);
    }

    [Fact]
    public void Fights_Still_Queued_When_The_Service_Is_Disposed_Are_Not_Reported_As_Failed_Saves()
    {
        // Dispose completes the queue and waits for the writer; whatever
        // it saved or did not, closing is not a save FAILURE and must not
        // leave evidence that reads like one.
        var history = new HistoryService();
        for (var n = 1; n <= 40; n++)
            history.QueueSave(Fight(n));
        history.Dispose();
        history.Dispose(); // idempotent

        Assert.Equal(0, history.SaveFailures);
        Assert.Empty(Reports(HistoryService.SaveKind));
        Assert.DoesNotContain("archive save FAILED", Journal());
    }
}
