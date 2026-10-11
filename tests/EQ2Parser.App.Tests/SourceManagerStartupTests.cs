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

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "EQ2Parser.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not locate the repo root (EQ2Parser.slnx) above the test bin dir");
        return dir!;
    }
}
