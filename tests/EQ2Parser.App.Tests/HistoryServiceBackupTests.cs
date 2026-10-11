using System.IO;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using Microsoft.Data.Sqlite;

namespace EQ2Parser.App.Tests;

/// <summary>The clean-shutdown snapshot (backups\history.db.bak, previous
/// one rotated to .bak2). Each <see cref="Session"/> is one launch and one
/// clean exit against the sandbox data directory.</summary>
public sealed class HistoryServiceBackupTests : IDisposable
{
    private readonly TestDataDir _dir = new();
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private string Bak => Path.Combine(_dir.Path, "backups", "history.db.bak");
    private string Bak2 => Path.Combine(_dir.Path, "backups", "history.db.bak2");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    /// <summary>A distinct boss fight per <paramref name="n"/> (the archive
    /// folds a re-save of the same fight into the row it already has).</summary>
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
    private static void Session(params Encounter[] fights) => Session(null, fights);

    /// <summary>The same, with the service adjusted before it is used —
    /// how a test makes this exit's snapshot fail.</summary>
    private static void Session(Action<HistoryService>? configure, params Encounter[] fights)
    {
        using (var history = new HistoryService())
        {
            configure?.Invoke(history);
            foreach (var fight in fights)
                history.QueueSave(fight);
        }
        // A real exit ends the process and with it every file handle; the
        // SQLite provider otherwise keeps the closed files open in its pool,
        // and the next rotation (or the wipe below) could not move them.
        SqliteConnection.ClearAllPools();
    }

    /// <summary>The classic wipe — the glob the backups folder exists to
    /// survive: history.db and its WAL siblings, nothing in a subfolder.</summary>
    private void WipeArchive()
    {
        foreach (var file in Directory.EnumerateFiles(_dir.Path, "history.db*", SearchOption.TopDirectoryOnly))
            File.Delete(file);
    }

    /// <summary>Fights held by a snapshot file, or null when there is none.</summary>
    private static long? FightsIn(string path)
    {
        if (!File.Exists(path))
            return null;
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM encounters;";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Clean_Exit_Snapshots_The_Archive_And_Rotates_The_Previous_Snapshot()
    {
        Session(Fight(1));
        Assert.Equal(1, FightsIn(Bak));
        Assert.Null(FightsIn(Bak2));

        Session(Fight(2));
        Assert.Equal(2, FightsIn(Bak));
        Assert.Equal(1, FightsIn(Bak2));

        // No new fight is still a snapshot: the archive has something in it.
        Session();
        Assert.Equal(2, FightsIn(Bak));
        Assert.Equal(2, FightsIn(Bak2));
    }

    [Fact]
    public void Empty_Archive_Never_Rotates_Out_A_Backup()
    {
        Session(Fight(1));
        Session(Fight(2));
        Assert.Equal<(long?, long?)>((2, 1), (FightsIn(Bak), FightsIn(Bak2)));

        // The archive is lost. The person notices an empty history, closes
        // the app, opens it again to see whether that helps, closes it
        // again: two clean exits, which used to leave BOTH backups holding
        // the empty archive — the insurance spent on the very accident it
        // was bought for.
        WipeArchive();
        Session();
        Assert.Equal<(long?, long?)>((2, 1), (FightsIn(Bak), FightsIn(Bak2)));
        Session();
        Session();
        Assert.Equal<(long?, long?)>((2, 1), (FightsIn(Bak), FightsIn(Bak2)));
    }

    [Fact]
    public void Rotation_Resumes_With_The_First_Fight_After_A_Loss()
    {
        // The limit of the guard, stated as a test so nobody reads more
        // into it: it holds while the archive is EMPTY. Once a fight is
        // saved there is something new worth protecting, the normal
        // rotation resumes, and the lost archive is one more non-empty
        // exit from leaving .bak2.
        Session(Fight(1), Fight(2), Fight(3));
        WipeArchive();
        Session();
        Assert.Equal(3, FightsIn(Bak));

        Session(Fight(4));
        Assert.Equal(1, FightsIn(Bak));
        Assert.Equal(3, FightsIn(Bak2));
    }

    [Fact]
    public void Empty_Archive_With_No_Backup_Writes_None()
    {
        // First run, nothing parsed: a snapshot of nothing is not a backup.
        Session();
        Assert.Null(FightsIn(Bak));
        Assert.Null(FightsIn(Bak2));
    }

    [Fact]
    public void Purging_Every_Fight_Keeps_The_Last_Snapshot_Until_There_Is_A_New_One()
    {
        // The same rule seen from the deliberate side: an archive emptied
        // on purpose does not push its emptiness into the backups either.
        // They catch up on the first clean exit after a new fight is saved.
        Session(Fight(1));
        using (var history = new HistoryService())
        {
            foreach (var summary in history.Search(null, null))
                history.PurgeFromArchive(summary.Id);
            Assert.Empty(history.Search(null, null));
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(1, FightsIn(Bak));
        Assert.Null(FightsIn(Bak2));

        Session(Fight(2));
        Assert.Equal(1, FightsIn(Bak));
        Assert.Equal(1, FightsIn(Bak2));
        Session();
        // Two exits with something in the archive: the purged fight is gone.
        using var conn = new SqliteConnection($"Data Source={Bak2};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM encounters WHERE title = 'Lord Oak 1';";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── A snapshot that does not finish must cost nothing ───────────────────

    private string BackupDir => Path.Combine(_dir.Path, "backups");

    private string[] BackupFiles() =>
        [.. Directory.GetFiles(BackupDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static string Journal() =>
        File.Exists(SessionJournal.FilePath) ? File.ReadAllText(SessionJournal.FilePath) : "";

    [Fact]
    public void A_Snapshot_That_Fails_Part_Way_Leaves_Both_Backups_As_They_Were()
    {
        Session(Fight(1));
        Session(Fight(2));
        Assert.Equal<(long?, long?)>((2, 1), (FightsIn(Bak), FightsIn(Bak2)));
        CrashLog.ResetRateLimit();

        // The disk fills while the snapshot is being written: part of a
        // file exists, then the write throws. The old order had already
        // moved .bak to .bak2 by then, so this exit left NO good .bak and
        // the next one rotated the partial file over the last good copy.
        Session(
            history => history.SnapshotTo = static (_, path) =>
            {
                File.WriteAllText(path, "half a database");
                throw new IOException("There is not enough space on the disk.");
            },
            Fight(3));

        Assert.Equal<(long?, long?)>((2, 1), (FightsIn(Bak), FightsIn(Bak2)));
        Assert.Equal(["history.db.bak", "history.db.bak2"], BackupFiles());
        Assert.Contains("archive: exit backup FAILED (IOException)", Journal());
        Assert.Single(Directory.GetFiles(CrashLog.Directory, "crash-*-archive-backup*.txt"));

        // The exit after that is an ordinary one, and nothing was lost on
        // the way: the newest archive in .bak, the last good one in .bak2.
        Session();
        Assert.Equal<(long?, long?)>((3, 2), (FightsIn(Bak), FightsIn(Bak2)));
    }

    [Fact]
    public void A_Failing_Snapshot_On_Every_Exit_Never_Erodes_The_Backups()
    {
        Session(Fight(1));
        Session(Fight(2));

        for (var n = 3; n <= 6; n++)
        {
            Session(
                history => history.SnapshotTo = static (_, path) =>
                {
                    File.WriteAllText(path, "half a database");
                    throw new IOException("There is not enough space on the disk.");
                },
                Fight(n));
        }

        Assert.Equal<(long?, long?)>((2, 1), (FightsIn(Bak), FightsIn(Bak2)));
    }

    [Fact]
    public void A_Temporary_Snapshot_Left_By_A_Dead_Exit_Is_Never_Promoted()
    {
        Session(Fight(1));
        // An exit that was killed mid-snapshot: the temporary file is of
        // unknown completeness. The next exit writes its own.
        File.WriteAllText(Path.Combine(BackupDir, "history.db.bak.tmp"), "half a database");

        Session(Fight(2));

        Assert.Equal<(long?, long?)>((2, 1), (FightsIn(Bak), FightsIn(Bak2)));
        Assert.Equal(["history.db.bak", "history.db.bak2"], BackupFiles());
    }

    [Fact]
    public void A_Snapshot_That_Cannot_Even_Start_Leaves_Both_Backups_As_They_Were()
    {
        Session(Fight(1));
        Session(Fight(2));
        // Something is in the way of the temporary name and will not go.
        var tmp = Path.Combine(BackupDir, "history.db.bak.tmp");
        File.WriteAllText(tmp, "held");
        using (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Session(Fight(3));
        }

        Assert.Equal<(long?, long?)>((2, 1), (FightsIn(Bak), FightsIn(Bak2)));
    }

    [Fact]
    public void A_Clean_Exit_Leaves_Nothing_In_The_Backups_Folder_But_The_Two_Snapshots()
    {
        Session(Fight(1));
        Assert.Equal(["history.db.bak"], BackupFiles());

        Session(Fight(2));
        Assert.Equal(["history.db.bak", "history.db.bak2"], BackupFiles());
    }
}
