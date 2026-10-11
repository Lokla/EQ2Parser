using System.IO;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Tests;

/// <summary>
/// What SourceManager promises at construction and while it runs: status
/// strings resolved (never raw keys), a bad user override file reported
/// instead of ignored in silence, a trigger disabled for being too slow
/// announced, and one failing fight consumer unable to starve the others.
/// And as it lets go: a fight in progress when its source is removed or the
/// app closes is ended, announced and archived; a stopped source does not
/// hold the UI in import mode; one failing raid-line consumer does not
/// starve the other two.
/// </summary>
public sealed class SourceManagerStartupTests : IDisposable
{
    private readonly TestDataDir _dir = new();
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    public SourceManagerStartupTests()
    {
        VelopackTestHost.Ensure();
        Loc.Initialize("en");
        CrashLog.ResetRateLimit();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _dir.Dispose();
    }

    /// <summary>Nothing here may reach the real site: the base URL is a
    /// reserved test host, so any probe a service fires fails fast.</summary>
    private static AppSettings Settings(bool uploadEnabled = false) =>
        new() { LexiconBaseUrl = "https://example.test", UploadEnabled = uploadEnabled };

    private static string Journal() =>
        File.Exists(SessionJournal.FilePath) ? File.ReadAllText(SessionJournal.FilePath) : "";

    private LogSource AddSource(SourceManager manager, string owner = "Tester")
    {
        var path = _dir.FileIn($"eq2log_{owner}.txt");
        File.WriteAllText(path, "");
        return manager.Add(path, parseFromStart: false);
    }

    [Fact]
    public void The_Upload_Status_Set_During_Construction_Is_Text_Not_A_Key()
    {
        // Uploads on, no token: Configure sets a status line from inside
        // the constructor. With the dictionaries loaded first (the order
        // the settings parameter forces), it is the sentence.
        using var manager = new SourceManager(Settings(uploadEnabled: true));

        Assert.Equal(Loc.Get("UploadSvc_NoTokenSaveBelow"), manager.Uploads.Status);
        Assert.NotEqual("UploadSvc_NoTokenSaveBelow", manager.Uploads.Status);
        Assert.DoesNotContain("UploadSvc_", manager.Uploads.Status);
    }

    [Fact]
    public void The_Manager_Uses_The_Settings_It_Is_Given()
    {
        var settings = Settings() with { LanguageCode = "de", IdleEndSeconds = 9 };

        using var manager = new SourceManager(settings);

        Assert.Same(settings, manager.Settings);
    }

    [Fact]
    public void App_Startup_Loads_The_Dictionaries_Before_It_Builds_Any_Service()
    {
        // Source-level on purpose (as LayeringTests is): the bug was an
        // ORDER of statements in App.OnStartup, which no unit test can run
        // — it creates the real window. Settings first (the language is in
        // them), then Loc.Initialize, then the manager and everything it
        // constructs.
        var startup = File.ReadAllText(Path.Combine(RepoRoot(), "src", "EQ2Parser.App", "App.xaml.cs"));

        var settings = startup.IndexOf("AppSettings.Load()", StringComparison.Ordinal);
        var dictionaries = startup.IndexOf("Loc.Initialize(", StringComparison.Ordinal);
        var manager = startup.IndexOf("new SourceManager(", StringComparison.Ordinal);

        Assert.True(settings >= 0 && dictionaries >= 0 && manager >= 0, "App.xaml.cs no longer has the three startup steps this test orders");
        Assert.True(settings < dictionaries, "settings must be loaded before Loc.Initialize — the language is a setting");
        Assert.True(dictionaries < manager, "Loc.Initialize must run before SourceManager is constructed — its services resolve strings as they are built");
    }

    [Fact]
    public void A_Malformed_User_Override_File_Never_Stops_Construction_And_Is_Reported()
    {
        File.WriteAllText(_dir.FileIn("source_overrides.json"), "{ \"overrides\": [ { \"ability\": ");

        using var manager = new SourceManager(Settings());

        Assert.NotNull(manager.SourceOverridesError);
        Assert.Contains("source_overrides.json", manager.SourceOverridesError);
        Assert.Contains("user override file IGNORED", Journal());
        // The embedded rules still apply — only the user layer was dropped.
        Assert.NotNull(manager.Classifier);
    }

    [Fact]
    public void A_Good_Or_Absent_User_Override_File_Reports_Nothing()
    {
        using (var absent = new SourceManager(Settings()))
            Assert.Null(absent.SourceOverridesError);

        File.WriteAllText(_dir.FileIn("source_overrides.json"),
            """{ "overrides": [ { "ability": "Divine Smash", "source": "Pet" } ] }""");
        using var good = new SourceManager(Settings());

        Assert.Null(good.SourceOverridesError);
        Assert.DoesNotContain("IGNORED", Journal());
    }

    [Fact]
    public void A_Trigger_Disabled_For_Timing_Out_Is_Journaled_And_Announced()
    {
        using var manager = new SourceManager(Settings());
        // Catastrophic backtracking: "$" never matches, so every split of
        // the run of a's is tried.
        manager.Triggers.AddOrUpdate(new Trigger("^(a+)+$", category: "Slow Boss", zone: "Test Keep") { SoundType = TriggerSound.Beep });
        var source = AddSource(manager);
        List<string> toasts = [];
        manager.CalloutAnnounced += toasts.Add;

        lock (manager.Sync)
        {
            source.TriggerEngine!.Process(new string('a', 40) + "!", T0);
        }

        var toast = Assert.Single(toasts);
        Assert.Contains("Test Keep / Slow Boss", toast);
        Assert.DoesNotContain("Sources_TriggerTooSlow", toast);
        var journal = Journal();
        Assert.Contains("trigger DISABLED for this session", journal);
        Assert.Contains("Test Keep / Slow Boss", journal);
        // A pattern the user typed stays out of the journal.
        Assert.DoesNotContain("(a+)+", journal);
    }

    [Fact]
    public void A_Lexicon_Pack_Trigger_That_Times_Out_Is_Journaled_With_Its_Pattern()
    {
        // Pack content is public and curated — and the pattern is the one
        // thing a curator needs in order to fix it.
        using var manager = new SourceManager(Settings());
        manager.Triggers.AddOrUpdate(new Trigger("^(a+)+$", category: "Slow Boss") { Source = "lexicon" });
        var source = AddSource(manager);

        lock (manager.Sync)
        {
            source.TriggerEngine!.Process(new string('a', 40) + "!", T0);
        }

        var journal = Journal();
        Assert.Contains("lexicon trigger, filed under: Slow Boss", journal);
        Assert.Contains("(a+)+", journal);
    }

    [Fact]
    public void A_Removed_Source_No_Longer_Reports_Trigger_Timeouts()
    {
        using var manager = new SourceManager(Settings());
        manager.Triggers.AddOrUpdate(new Trigger("^(a+)+$", category: "Slow Boss"));
        var source = AddSource(manager);
        var engine = source.TriggerEngine!;
        manager.Remove(source);
        List<string> toasts = [];
        manager.CalloutAnnounced += toasts.Add;

        engine.Process(new string('a', 40) + "!", T0);

        Assert.Empty(toasts);
    }

    [Fact]
    public void One_Failing_Fight_Consumer_Does_Not_Starve_The_Others_Or_Fail_The_Line()
    {
        using var manager = new SourceManager(Settings());
        // Uploads active with the raid-only rule on: the gate asks the
        // player counter — which is made to throw, standing in for any
        // consumer fault (an archive write, a classifier bug).
        manager.Uploads.PlayerCounter = _ => throw new InvalidOperationException("consumer broke");
        manager.Uploads.Configure("https://example.test", "token", enabled: true, raidOnly: true);
        // In a server-named folder: the upload gate skips a log whose folder
        // is not a server name the site accepts BEFORE it asks the player
        // counter, and the sandbox folder (32 hex characters) is not one.
        var log = _dir.FileIn(Path.Combine("Varsoon", "eq2log_Tester.txt"));
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "");
        var source = manager.Add(log, parseFromStart: false);

        lock (manager.Sync)
        {
            Assert.True(source.Engine.SetEncounter(T0, "Tester", "a gnoll"));
            source.Engine.AddSwing(SwingCategory.Melee, false, "None", "Tester", "Strike", 1000, T0, "a gnoll", "crushing");
            // Before the consumers were isolated this threw — on the log
            // pump, that was a failed line.
            source.Engine.EndCombat();

            // The correlator ran BEFORE the failing consumer and still has
            // the fight…
            var fight = Assert.Single(manager.Correlator.History);
            Assert.Equal("a gnoll", fight.Title);
        }
        // …and the failure left its evidence.
        Assert.Contains("fight consumer FAILED: upload (InvalidOperationException", Journal());
        Assert.Single(Directory.GetFiles(CrashLog.Directory, "crash-*-fight-consumer*.txt"));
    }

    [Fact]
    public void A_Fight_Still_Reaches_The_Correlator_And_Merges_Across_Sources()
    {
        // The correlator used to subscribe to each engine itself; it is
        // now fed through the manager's fan-out. Two logs of one fight
        // must still merge into one correlated fight.
        using var manager = new SourceManager(Settings());
        var first = AddSource(manager, "Tester");
        var second = AddSource(manager, "Helper");

        lock (manager.Sync)
        {
            foreach (var source in new[] { first, second })
            {
                source.Engine.ChangeZone("Test Keep");
                Assert.True(source.Engine.SetEncounter(T0, source.Owner, "a gnoll"));
                source.Engine.AddSwing(SwingCategory.Melee, false, "None", source.Owner, "Strike", 1000, T0, "a gnoll", "crushing");
                source.Engine.EndCombat();
            }

            var fight = Assert.Single(manager.Correlator.History);
            Assert.Equal(2, fight.Sources.Count);
        }
        Assert.DoesNotContain("fight consumer FAILED", Journal());
    }

    // ── A fight in progress when its source goes away ───────────────────────

    /// <summary>A hit on a named mob, stamped at <paramref name="epoch"/>.</summary>
    private static string Hit(long epoch) => $"({epoch})[s] YOUR Smite hits Lord Oak for 500 divine damage.";

    /// <summary>A manager tailing one log whose owner is two lines into a
    /// fight: read by the real pump, from a real file, stamped now.</summary>
    private async Task<(LogSource Source, string Path)> MidFight(SourceManager manager)
    {
        var path = _dir.FileIn("eq2log_Tester.txt");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await File.WriteAllLinesAsync(path, [Hit(now), Hit(now + 1)]);
        var source = manager.Add(path, parseFromStart: true);
        await WaitFor(() => source.Processor.LinesMatched >= 2, "the pump to read the fight");
        lock (manager.Sync)
        {
            Assert.True(source.Engine.InCombat);
            // Not ended, so nothing has heard of it yet.
            Assert.Empty(manager.Correlator.History);
        }
        return (source, path);
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_Source_Removed_Mid_Fight_Has_Its_Fight_Ended_Announced_And_Archived()
    {
        using var manager = new SourceManager(Settings());
        var (source, _) = await MidFight(manager);

        // Before: the handlers were detached and the pump cancelled with
        // the engine still in combat. The fight was never ended, so it
        // reached no consumer and no archive.
        manager.Remove(source);

        lock (manager.Sync)
        {
            Assert.False(source.Engine.InCombat);
            Assert.False(Assert.Single(source.Engine.History).Active);
            // The consumers were still attached when it ended.
            Assert.Equal("Lord Oak", Assert.Single(manager.Correlator.History).Title);
        }
        Assert.Empty(manager.Sources);
        await WaitFor(() => manager.History.Search(null, null).Count == 1, "the archive to take the fight");
        Assert.Equal("Lord Oak", Assert.Single(manager.History.Search(null, null)).Title);
        Assert.Equal(0, manager.History.SaveFailures);
        var journal = Journal();
        Assert.Contains("source removed: eq2log_Tester.txt; its fight in progress was ended", journal);
        Assert.DoesNotContain("fight consumer FAILED", journal);
    }

    [Fact]
    public void A_Source_Removed_Out_Of_Combat_Announces_Nothing()
    {
        using var manager = new SourceManager(Settings());
        var source = AddSource(manager);

        manager.Remove(source);

        lock (manager.Sync)
        {
            Assert.Empty(manager.Correlator.History);
        }
        Assert.Contains("source removed: eq2log_Tester.txt", Journal());
        Assert.DoesNotContain("fight in progress", Journal());
    }

    [Fact]
    public async Task Closing_The_App_Mid_Fight_Leaves_The_Fight_In_The_Archive_File()
    {
        var manager = new SourceManager(Settings());
        var (source, path) = await MidFight(manager);

        // The exit, as App.OnExit runs it. Before: the pumps were cancelled
        // with the engine in combat and the archive closed without the
        // last pull of the night.
        manager.StopFolderWatch();
        manager.StopPumps();
        manager.PersistSources();
        manager.Dispose();

        Assert.False(source.Engine.InCombat);
        // The FILE, read with nothing of the app left open on it.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            EQ2Parser.Core.History.HistoryStore.ConnectionString(_dir.FileIn("history.db"))))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT title || ' / ' || (SELECT COUNT(*) FROM swings WHERE encounter_id = encounters.id) FROM encounters;";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read(), "history.db holds no fight");
            Assert.Equal("Lord Oak / 2", reader.GetString(0));
            Assert.False(reader.Read(), "history.db holds more than the one fight");
        }
        // The exit snapshot was taken after the save, so it has it too.
        Assert.True(File.Exists(Path.Combine(_dir.Path, "backups", "history.db.bak")));
        // The resume position is the end of what that fight holds: the
        // next session starts on the line after it, not inside it.
        var saved = Assert.Single(manager.Settings.Sources);
        Assert.Equal(new FileInfo(path).Length, saved.LastPosition);
        var journal = Journal();
        Assert.Contains("exit: fight in progress ended — eq2log_Tester.txt", journal);
        Assert.DoesNotContain("exit did not wait", journal);
        Assert.DoesNotContain("fight consumer FAILED", journal);
    }

    [Fact]
    public void Closing_The_App_Out_Of_Combat_Ends_Nothing()
    {
        var manager = new SourceManager(Settings());
        AddSource(manager);

        manager.Dispose();

        Assert.DoesNotContain("fight in progress", Journal());
        Assert.False(File.Exists(Path.Combine(_dir.Path, "backups", "history.db.bak")));
    }

    [Fact]
    public void App_Exit_Stops_Reading_Before_It_Saves_Positions_And_Ends_Fights_Before_The_Archive_Closes()
    {
        // Source-level, like the startup-order test above and for the same
        // reason: App.OnExit cannot run in a test. Two orders matter. In
        // OnExit, the pumps stop before the resume positions are saved (a
        // line read after the save would be in tonight's last fight AND
        // read again next session). In SourceManager.Dispose, the fights
        // in progress end before the archive is disposed, or the save they
        // queue has nowhere to go.
        var exit = File.ReadAllText(Path.Combine(RepoRoot(), "src", "EQ2Parser.App", "App.xaml.cs"));
        var onExit = exit.IndexOf("protected override void OnExit(", StringComparison.Ordinal);
        var stopPumps = exit.IndexOf("_manager?.StopPumps();", onExit, StringComparison.Ordinal);
        var persist = exit.IndexOf("_manager?.PersistSources();", onExit, StringComparison.Ordinal);
        var dispose = exit.IndexOf("_manager?.Dispose();", onExit, StringComparison.Ordinal);
        Assert.True(onExit >= 0 && stopPumps >= 0 && persist >= 0 && dispose >= 0, "App.OnExit no longer has the three steps this test orders");
        Assert.True(stopPumps < persist, "the pumps must stop before PersistSources saves their positions");
        Assert.True(persist < dispose, "PersistSources must run before the manager is disposed");

        var manager = File.ReadAllText(Path.Combine(RepoRoot(), "src", "EQ2Parser.App", "Services", "SourceManager.cs"));
        var disposeBody = manager.IndexOf("public void Dispose()", StringComparison.Ordinal);
        var endFights = manager.IndexOf("EndFightInProgress(\"the app is closing\")", disposeBody, StringComparison.Ordinal);
        var closeArchive = manager.IndexOf("History.Dispose();", disposeBody, StringComparison.Ordinal);
        Assert.True(disposeBody >= 0 && endFights >= 0 && closeArchive >= 0, "SourceManager.Dispose no longer has the steps this test orders");
        Assert.True(endFights < closeArchive, "fights in progress must be ended before the archive is disposed");
    }

    // ── A stopped source and the import throttle ────────────────────────────

    [Fact]
    public async Task A_Stopped_Source_Far_Behind_Its_Log_Does_Not_Put_The_Ui_In_Import_Mode()
    {
        using var manager = new SourceManager(Settings());
        var path = _dir.FileIn("eq2log_Tester.txt");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await File.WriteAllLinesAsync(path, [Hit(now), Hit(now + 1)]);
        // The log cannot be read (a byte-range lock held by another
        // handle), so the source stops with a reader failure.
        using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var lockedLength = locker.Length;
        locker.Lock(0, lockedLength);
        var source = manager.Add(path, parseFromStart: true);
        await WaitFor(() => source.Error is not null, "the source to stop");

        // The game keeps writing. Nobody reads this log any more, so it
        // runs megabytes ahead of where the source stopped.
        locker.Position = lockedLength;
        await locker.WriteAsync(new byte[2_000_000]);
        await locker.FlushAsync();
        Assert.True(new FileInfo(path).Length - source.LastPosition > 1_000_000);

        // Before: PendingBytes was that gap, so ImportBusy was true from
        // here to the end of the session — the main page at one refresh
        // every two seconds, the overlays showing "Importing history…".
        Assert.Equal(0, source.PendingBytes);
        Assert.False(manager.ImportBusy);
        locker.Unlock(0, lockedLength);
    }

    // ── Raid-line consumers, each isolated ──────────────────────────────────

    [Fact]
    public void Every_Raid_Line_Consumer_Runs_And_Every_Failure_Reaches_The_Caller()
    {
        List<string> ran = [];

        // One fails: the two around it run, and the failure comes out as
        // itself for the processor's raid-line guard to report.
        var one = Assert.Throws<InvalidOperationException>(() => SourceManager.RunRaidLineConsumers(
            "line", T0,
            (_, _) => ran.Add("roster"),
            (_, _) => throw new InvalidOperationException("dkp broke"),
            (_, _) => ran.Add("loot")));
        Assert.Equal(["roster", "loot"], ran);
        Assert.Equal("dkp broke", one.Message);
        Assert.Contains(nameof(Every_Raid_Line_Consumer_Runs_And_Every_Failure_Reaches_The_Caller), one.StackTrace);

        // Two fail: the third still runs, and neither failure is lost.
        ran.Clear();
        var two = Assert.Throws<AggregateException>(() => SourceManager.RunRaidLineConsumers(
            "line", T0,
            (_, _) => throw new InvalidOperationException("roster broke"),
            (_, _) => ran.Add("dkp"),
            (_, _) => throw new FormatException("loot broke")));
        Assert.Equal(["dkp"], ran);
        Assert.Equal(["roster broke", "loot broke"], two.InnerExceptions.Select(e => e.Message));

        // None fail: nothing is thrown.
        ran.Clear();
        SourceManager.RunRaidLineConsumers("line", T0, (_, _) => ran.Add("roster"), (_, _) => ran.Add("dkp"), (_, _) => ran.Add("loot"));
        Assert.Equal(["roster", "dkp", "loot"], ran);
    }

    [Fact]
    public void A_Raid_Line_Consumer_That_Throws_On_A_Real_Line_Does_Not_Cost_The_One_After_It()
    {
        using var manager = new SourceManager(Settings());
        var source = AddSource(manager);
        // DKP progress is the second of the three raid-line consumers and
        // the loot tracker the third. A subscriber of the first's event
        // throws, as a bug on the Raid page would.
        manager.DkpProgress.LootEchoSeen += static (_, _) => throw new InvalidOperationException("dkp consumer broke");
        // One line both act on: a loot line (the tracker records the item)
        // that also reads as a loot-charge echo (the item link, " assigned
        // to " and " dkp").
        var line = $"({T0.ToUnixTimeSeconds()})[s] Alder loots \\aITEM 1 2:Oak Blade\\/a from the Exquisite Chest of Lord Oak assigned to the raid for 5 dkp.";

        Assert.True(source.PumpLine(new EQ2Parser.Core.Logs.TailedLine(line, T0)));

        // Before: the throw ended the handler, and the loot tracker never
        // saw the line.
        var item = Assert.Single(manager.Loot.Snapshot());
        Assert.Equal("Oak Blade", item.ItemName);
        Assert.Equal("Alder", item.LootedBy);
        // Still one contained raid-line alert fault, reported as before:
        // the line was not skipped.
        Assert.Equal(1, source.AlertFaults);
        Assert.IsType<InvalidOperationException>(source.LastAlertError);
        Assert.Equal(0, source.SkippedLines);
        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(CrashLog.Directory, $"crash-*-{LogSource.AlertKind}*.txt")));
        Assert.Contains("Alert stage: raid line", report);
        Assert.Contains("dkp consumer broke", report);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "EQ2Parser.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not locate the repo root (EQ2Parser.slnx) above the test bin dir");
        return dir!;
    }
}
