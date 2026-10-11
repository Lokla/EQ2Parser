using System.IO;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;
using Microsoft.Data.Sqlite;

namespace EQ2Parser.App.Tests;

/// <summary>
/// A degraded archive is said on screen, in the interface language: the
/// strip in the main window (<see cref="ArchiveNotice"/>) and the Archive
/// window, which also has to survive an archive that is missing or answers
/// with an error. Before, <see cref="HistoryService"/> knew all of this and
/// no view showed any of it: a session whose archive could not be opened
/// saved no fight and looked exactly like one that saved them all.
/// </summary>
public sealed class ArchiveNoticeTests : IDisposable
{
    private static readonly string[] Languages = ["en", "de", "fr", "ru"];
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);
    private const int AllDays = 100_000;

    /// <summary>The "Everything" entry of the Archive window's range list.
    /// The fights below carry a fixed date, so any shorter range would
    /// stop finding them as the calendar moves on.</summary>
    private const int EverythingRange = 4;

    private readonly TestDataDir _dir = new();

    private string Db => _dir.FileIn("history.db");

    public ArchiveNoticeTests()
    {
        VelopackTestHost.Ensure();
        Loc.Initialize("en");
        CrashLog.ResetRateLimit();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Loc.Initialize("en");
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private static AppSettings Settings() => new() { LexiconBaseUrl = "https://example.test" };

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

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    private static void InEveryLanguage(Action<string> body)
    {
        foreach (var language in Languages)
            LocTestHost.In(language, () => body(language));
    }

    // ── The words, for each state and count ────────────────────────────────

    private static readonly ArchiveHealth Healthy = new(ArchiveState.Healthy);

    private static readonly ArchiveHealth Quarantined = new(
        ArchiveState.Quarantined, QuarantinedFile: "history.db.corrupt-20260314-210501", OpenErrorKind: "SqliteException 26");

    private static readonly ArchiveHealth Unavailable = new(ArchiveState.Unavailable, OpenErrorKind: "SqliteException 5");

    [Fact]
    public void A_Healthy_Archive_Shows_Nothing_In_Any_Language()
    {
        InEveryLanguage(_ =>
        {
            Assert.Equal("", ArchiveNotice.Text(Healthy));
            Assert.Null(ArchiveNotice.Startup(Healthy));
        });

        // And a real one, before and after it has saved a fight.
        Session(Fight(1));
        using var history = new HistoryService();
        Assert.Equal("", ArchiveNotice.For(history));
        Assert.Equal(Healthy, ArchiveNotice.Read(history));
    }

    [Fact]
    public void A_Quarantined_Archive_Says_The_Old_One_Was_Kept_And_Under_What_Name()
    {
        Assert.Equal(
            "The fight archive was damaged and could not be read, so a new, empty one was started. "
            + "Nothing was deleted: your earlier fights are kept in the file history.db.corrupt-20260314-210501 in the data folder.",
            ArchiveNotice.Text(Quarantined));
        LocTestHost.In("de", () => Assert.Equal(
            "Das Kampfarchiv war beschädigt und ließ sich nicht lesen, deshalb wurde ein neues, leeres angelegt. "
            + "Es wurde nichts gelöscht: Deine früheren Kämpfe liegen in der Datei history.db.corrupt-20260314-210501 im Datenordner.",
            ArchiveNotice.Text(Quarantined)));
    }

    [Fact]
    public void An_Unavailable_Archive_Says_Nothing_Is_Being_Saved()
    {
        Assert.Equal(
            "The fight archive could not be opened (SqliteException 5). "
            + "Fights from this session are not being saved; the file was left as it is, and the next start will try again.",
            ArchiveNotice.Text(Unavailable));
        LocTestHost.In("fr", () => Assert.Equal(
            "L'archive des combats n'a pas pu être ouverte (SqliteException 5). "
            + "Les combats de cette session ne sont pas enregistrés ; le fichier n'a pas été modifié et le prochain démarrage réessaiera.",
            ArchiveNotice.Text(Unavailable)));

        // Damaged, renamed, and then no fresh archive could be created: no
        // archive this session, and the old fights are in the renamed file.
        var renamed = new ArchiveHealth(
            ArchiveState.Unavailable, QuarantinedFile: "history.db.corrupt-20260314-210501", OpenErrorKind: "IOException");
        Assert.Equal(
            "The fight archive was damaged, and a new one could not be created (IOException). "
            + "Fights from this session are not being saved; your earlier fights are kept in the file history.db.corrupt-20260314-210501 in the data folder.",
            ArchiveNotice.Text(renamed));
    }

    [Fact]
    public void Failed_Saves_Are_Counted_With_A_Singular_And_A_Plural()
    {
        var one = Healthy with { SaveFailures = 1, SaveErrorKind = "SqliteException 13" };
        var many = Healthy with { SaveFailures = 12, SaveErrorKind = "SqliteException 13" };

        Assert.Equal(
            "1 fight from this session could not be saved to the archive (SqliteException 13). "
            + "It was parsed and shown as usual, but it is not in the archive.",
            ArchiveNotice.Text(one));
        Assert.Equal(
            "12 fights from this session could not be saved to the archive (SqliteException 13). "
            + "They were parsed and shown as usual, but they are not in the archive.",
            ArchiveNotice.Text(many));
        LocTestHost.In("de", () =>
        {
            Assert.Equal(
                "1 Kampf aus dieser Sitzung konnte nicht im Archiv gespeichert werden (SqliteException 13). "
                + "Er wurde wie gewohnt ausgewertet und angezeigt, fehlt aber im Archiv.",
                ArchiveNotice.Text(one));
            Assert.Equal(
                "12 Kämpfe aus dieser Sitzung konnten nicht im Archiv gespeichert werden (SqliteException 13). "
                + "Sie wurden wie gewohnt ausgewertet und angezeigt, fehlen aber im Archiv.",
                ArchiveNotice.Text(many));
        });
        // Russian is worded so the noun never has to agree with the number.
        LocTestHost.In("ru", () => Assert.StartsWith("Не сохранено в архив боёв этого сеанса: 12 (SqliteException 13).", ArchiveNotice.Text(many)));
        // Nothing was wrong at startup, so nothing is said about startup.
        Assert.Null(ArchiveNotice.Startup(many));
    }

    [Fact]
    public void Fights_That_Did_Not_Come_Back_Are_Counted_Or_Named_As_All_Of_Them()
    {
        var one = Healthy with { RestoreFailed = true, RestoreFailures = 1, RestoreErrorKind = "SqliteException 11" };
        var many = Healthy with { RestoreFailed = true, RestoreFailures = 3, RestoreErrorKind = "SqliteException 11" };
        // The list itself could not be read: no count to give.
        var all = Healthy with { RestoreFailed = true, RestoreFailures = 0, RestoreErrorKind = "SqliteException 11" };

        Assert.Equal(
            "1 recent fight could not be loaded from the archive (SqliteException 11). "
            + "It is missing from the fight list; the archive file was left as it is.",
            ArchiveNotice.Text(one));
        Assert.Equal(
            "3 recent fights could not be loaded from the archive (SqliteException 11). "
            + "They are missing from the fight list; the archive file was left as it is.",
            ArchiveNotice.Text(many));
        Assert.Equal(
            "The recent fights could not be loaded from the archive (SqliteException 11). "
            + "The fight list starts empty; the archive file was left as it is.",
            ArchiveNotice.Text(all));
        LocTestHost.In("de", () => Assert.Equal(
            "3 der letzten Kämpfe konnten nicht aus dem Archiv geladen werden (SqliteException 11). "
            + "Sie fehlen in der Kampfliste; die Archivdatei wurde nicht angerührt.",
            ArchiveNotice.Text(many)));
    }

    [Fact]
    public void Several_Problems_Are_Several_Lines_Startup_First()
    {
        var everything = Quarantined with
        {
            SaveFailures = 2,
            SaveErrorKind = "SqliteException 13",
            RestoreFailed = true,
            RestoreFailures = 1,
            RestoreErrorKind = "SqliteException 11",
        };

        var lines = ArchiveNotice.Text(everything).Split(Environment.NewLine);

        Assert.Equal(3, lines.Length);
        Assert.Equal(ArchiveNotice.Text(Quarantined), lines[0]);
        Assert.StartsWith("2 fights from this session could not be saved", lines[1]);
        Assert.StartsWith("1 recent fight could not be loaded", lines[2]);
    }

    [Fact]
    public void No_State_Shows_A_Raw_Key_And_Every_One_Keeps_Its_Details_In_Every_Language()
    {
        ArchiveHealth[] states =
        [
            Quarantined,
            Unavailable,
            Unavailable with { QuarantinedFile = "history.db.corrupt-20260314-210501" },
            Healthy with { SaveFailures = 1, SaveErrorKind = "SqliteException 13" },
            Healthy with { SaveFailures = 1234, SaveErrorKind = "SqliteException 13" },
            Healthy with { RestoreFailed = true, RestoreFailures = 1, RestoreErrorKind = "SqliteException 11" },
            Healthy with { RestoreFailed = true, RestoreFailures = 7, RestoreErrorKind = "SqliteException 11" },
            Healthy with { RestoreFailed = true, RestoreErrorKind = "SqliteException 11" },
        ];
        var english = states.Select(ArchiveNotice.Text).ToList();

        InEveryLanguage(language =>
        {
            for (var i = 0; i < states.Length; i++)
            {
                var text = ArchiveNotice.Text(states[i]);
                Assert.NotEqual("", text);
                Assert.DoesNotContain("ArchiveNotice_", text);
                // What is not interface text survives every language: the
                // exception's kind (the quarantine notice has none to
                // show: its news is the file) and the file's name.
                if (states[i].State != ArchiveState.Quarantined)
                    Assert.Contains("SqliteException", text);
                if (states[i].QuarantinedFile is { } file)
                    Assert.Contains(file, text);
                if (language != "en")
                    Assert.NotEqual(english[i], text);
            }
        });
    }

    // ── The same words from a real archive in each state ───────────────────

    [Fact]
    public void A_File_Of_Garbage_Shows_The_Quarantine_Notice_With_The_Real_File_Name()
    {
        File.WriteAllText(Db, new string('x', 8192));

        using var history = new HistoryService();

        var name = Path.GetFileName(history.QuarantinedPath!);
        Assert.StartsWith("history.db.corrupt-", name);
        var text = ArchiveNotice.For(history);
        Assert.Equal(
            "The fight archive was damaged and could not be read, so a new, empty one was started. "
            + $"Nothing was deleted: your earlier fights are kept in the file {name} in the data folder.",
            text);
        // A file name, never a path, and never the English sentence the
        // crash report carries.
        Assert.DoesNotContain(_dir.Path, text);
        Assert.NotEqual(history.Problem, text);
        LocTestHost.In("de", () => Assert.Contains($"in der Datei {name} im Datenordner", ArchiveNotice.For(history)));
    }

    [Fact]
    public void A_Locked_File_Shows_The_Unavailable_Notice_Without_The_Exception_Message()
    {
        Session(Fight(1));

        using (new FileStream(Db, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var history = new HistoryService();

            Assert.Equal(ArchiveState.Unavailable, history.State);
            var error = Assert.IsType<SqliteException>(history.OpenError);
            var text = ArchiveNotice.For(history);
            // 14 is SQLite's "unable to open": what a file another process
            // holds exclusively answers with.
            Assert.Equal(
                "The fight archive could not be opened (SqliteException 14). "
                + "Fights from this session are not being saved; the file was left as it is, and the next start will try again.",
                text);
            // The type and SQLite's code are shown; the message is not.
            Assert.DoesNotContain(error.Message, text);
            Assert.DoesNotContain(_dir.Path, text);
        }
    }

    [Fact]
    public async Task A_Failed_Save_And_An_Unrestored_Fight_Show_Up_With_Their_Counts()
    {
        Session(Fight(1), Fight(2), Fight(3));
        Sql("UPDATE swings SET time_ts = 9000000000000000 WHERE encounter_id = (SELECT id FROM encounters WHERE title = 'Lord Oak 2');");

        using var history = new HistoryService();
        Assert.Equal("", ArchiveNotice.For(history));

        history.RestoreInto(new EncounterCorrelator(), AllDays, AllDays);
        var restoreKind = HistoryService.Kind(history.RestoreError);
        Assert.Equal(
            $"1 recent fight could not be loaded from the archive ({restoreKind}). "
            + "It is missing from the fight list; the archive file was left as it is.",
            ArchiveNotice.For(history));

        Sql("DROP TABLE swings;");
        history.QueueSave(Fight(4));
        await WaitFor(() => history.SaveFailures == 1, "the save to fail");

        Assert.Equal(
            "1 fight from this session could not be saved to the archive (SqliteException 1). "
            + "It was parsed and shown as usual, but it is not in the archive."
            + Environment.NewLine
            + $"1 recent fight could not be loaded from the archive ({restoreKind}). "
            + "It is missing from the fight list; the archive file was left as it is.",
            ArchiveNotice.For(history));
    }

    // ── The Archive window ─────────────────────────────────────────────────

    private static ArchiveRow Row(ArchiveViewModel archive, string title) =>
        archive.Rows.Single(r => r.Title == title);

    [Fact]
    public void An_Empty_Healthy_Archive_Still_Reads_Nothing_Found()
    {
        using var manager = new SourceManager(Settings());

        var archive = new ArchiveViewModel(manager);

        Assert.Equal("Nothing found.", archive.ResultLabel);
        Assert.Equal("", archive.Notice);
        Assert.False(archive.HasNotice);
    }

    [Fact]
    public async Task Without_An_Archive_The_Window_Says_Why_And_Its_Commands_Do_Nothing()
    {
        Session(Fight(1));
        var summary = new EQ2Parser.Core.History.EncounterSummary(
            1, "log-a", "Alder", "The Oak Halls", "Lord Oak 1", T0, T0.AddSeconds(10), 10, SuccessLevel.Win, 1000, null, true);

        using (new FileStream(Db, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var manager = new SourceManager(Settings());
            Assert.False(manager.History.IsAvailable);

            var archive = new ArchiveViewModel(manager);

            // Before: "Nothing found." under an empty list, as if the
            // archive had been searched and held nothing.
            var reason = ArchiveNotice.Startup(ArchiveNotice.Read(manager.History));
            Assert.StartsWith("The fight archive could not be opened (SqliteException ", reason);
            Assert.Equal(reason, archive.Notice);
            Assert.True(archive.HasNotice);
            Assert.Equal("", archive.ResultLabel);
            Assert.Empty(archive.Rows);

            // Nothing to do, nothing thrown, and the reason stays up.
            var row = new ArchiveRow(summary, loaded: false);
            archive.SearchCommand.Execute(null);
            archive.LoadRowCommand.Execute(row);
            await archive.LoadAllCommand.ExecuteAsync(null);
            archive.PurgeRowCommand.Execute(row);
            archive.RangeIndex = EverythingRange;

            Assert.False(row.IsLoaded);
            Assert.Equal(reason, archive.Notice);
            Assert.Equal("", archive.ResultLabel);
            lock (manager.Sync)
            {
                Assert.Empty(manager.Correlator.History);
            }
            LocTestHost.In("de", () =>
            {
                archive.SearchCommand.Execute(null);
                Assert.StartsWith("Das Kampfarchiv konnte nicht geöffnet werden (SqliteException ", archive.Notice);
            });
        }
        Assert.Empty(Reports(ArchiveViewModel.FailureKind));
    }

    [Fact]
    public void A_Search_That_Errors_Says_So_And_Keeps_The_Last_Results()
    {
        Session(Fight(1), Fight(2));
        using var manager = new SourceManager(Settings());
        var archive = new ArchiveViewModel(manager) { RangeIndex = EverythingRange };
        Assert.Equal("2 fights · 2 bosses", archive.ResultLabel);

        // The archive stops answering (a damaged page or a file gone
        // read-only in real life; here the table is taken away).
        Sql("ALTER TABLE encounters RENAME TO encounters_gone;");
        // Before: this threw out of the command, on the UI thread.
        archive.SearchCommand.Execute(null);

        Assert.Equal("The archive could not be searched (SqliteException 1).", archive.Notice);
        Assert.Equal("", archive.ResultLabel);
        Assert.Equal(2, archive.Rows.Count);
        var report = File.ReadAllText(Assert.Single(Reports(ArchiveViewModel.FailureKind)));
        Assert.Contains("Archive window: the search failed", report);
        Assert.Contains("SqliteException", report);
        LocTestHost.In("de", () =>
        {
            archive.SearchCommand.Execute(null);
            Assert.Equal("Das Archiv konnte nicht durchsucht werden (SqliteException 1).", archive.Notice);
        });

        // Once it answers again the notice goes and the count is back.
        Sql("ALTER TABLE encounters_gone RENAME TO encounters;");
        archive.SearchCommand.Execute(null);
        Assert.Equal("", archive.Notice);
        Assert.Equal("2 fights · 2 bosses", archive.ResultLabel);
    }

    [Fact]
    public async Task A_Fight_That_Cannot_Be_Loaded_Says_So_And_The_Others_Still_Load()
    {
        Session(Fight(1), Fight(2), Fight(3));
        Sql("UPDATE swings SET time_ts = 9000000000000000 WHERE encounter_id = (SELECT id FROM encounters WHERE title = 'Lord Oak 2');");
        using var manager = new SourceManager(Settings());
        var archive = new ArchiveViewModel(manager) { RangeIndex = EverythingRange };
        Assert.Equal(3, archive.Rows.Count);
        var bad = Row(archive, "Lord Oak 2");

        // Before: this threw out of the command, on the UI thread.
        archive.LoadRowCommand.Execute(bad);

        Assert.Equal("That fight could not be loaded from the archive (ArgumentOutOfRangeException).", archive.Notice);
        // Not marked as loaded: it is not in the parser, and the button
        // stays for another try.
        Assert.False(bad.IsLoaded);
        lock (manager.Sync)
        {
            Assert.Empty(manager.Correlator.History);
        }

        // One that loads clears the notice.
        archive.LoadRowCommand.Execute(Row(archive, "Lord Oak 1"));
        Assert.True(Row(archive, "Lord Oak 1").IsLoaded);
        Assert.Equal("", archive.Notice);

        // Load all: the unreadable fight costs itself only.
        CrashLog.ResetRateLimit();
        await archive.LoadAllCommand.ExecuteAsync(null);

        Assert.True(Row(archive, "Lord Oak 3").IsLoaded);
        Assert.False(bad.IsLoaded);
        Assert.StartsWith("1 fight could not be loaded from the archive (", archive.Notice);
        lock (manager.Sync)
        {
            Assert.Equal(["Lord Oak 1", "Lord Oak 3"], manager.Correlator.History.Select(f => f.Title).Order());
        }
        Assert.Equal(2, Reports(ArchiveViewModel.FailureKind).Length);
    }

    [Fact]
    public void A_Purge_That_Errors_Says_So_And_Leaves_The_Row()
    {
        Session(Fight(1), Fight(2));
        using var manager = new SourceManager(Settings());
        var archive = new ArchiveViewModel(manager) { RangeIndex = EverythingRange };
        Sql("CREATE TRIGGER keep_oak_1 BEFORE DELETE ON encounters WHEN OLD.title = 'Lord Oak 1' BEGIN SELECT RAISE(ABORT, 'refused'); END;");

        // Before: this threw out of the command, on the UI thread.
        archive.PurgeRowCommand.Execute(Row(archive, "Lord Oak 1"));

        // 19 is SQLite's "constraint": the trigger's refusal.
        Assert.Equal("That fight could not be purged from the archive (SqliteException 19).", archive.Notice);
        // The row stays, because the fight is still in the archive.
        Assert.Equal(2, archive.Rows.Count);
        Assert.Equal(2, manager.History.Search(null, null).Count);

        // A purge that works removes its row and clears the notice.
        archive.PurgeRowCommand.Execute(Row(archive, "Lord Oak 2"));
        Assert.Equal("Lord Oak 1", Assert.Single(archive.Rows).Title);
        Assert.Equal("", archive.Notice);
        Assert.Single(manager.History.Search(null, null));
    }
}
