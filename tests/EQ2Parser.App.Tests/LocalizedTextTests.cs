using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.History;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.App.Tests;

/// <summary>
/// The strings that were hard-coded English and now come from the
/// dictionaries: the Sources status row, the overlay window headers, the
/// curation workbench, the archive window, the trigger/timer tree sections,
/// the callout toast and the crash dialogs. Two promises per string: the
/// English reads exactly as it did (each "legacy" helper below is the code
/// this replaced), and no language shows a raw key.
/// </summary>
public sealed class LocalizedTextTests : IDisposable
{
    private static readonly string[] Languages = ["en", "de", "fr", "ru"];
    private static readonly long Epoch = 1_775_000_000;
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(Epoch);
    private const string PoisonMark = "private words";

    private readonly TestDataDir _dir = new();

    public LocalizedTextTests()
    {
        Loc.Initialize("en");
        CrashLog.ResetRateLimit();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Loc.Initialize("en");
        _dir.Dispose();
    }

    private static void InEveryLanguage(Action<string> body)
    {
        foreach (var language in Languages)
            LocTestHost.In(language, () => body(language));
    }

    // ── Sources page: the status row ───────────────────────────────────────

    /// <summary>A source whose raid-line stage throws on a marked line —
    /// the way LogSourceFaultIsolationTests makes a line fail.</summary>
    private LogSource Source(bool parseFromStart = true, bool autoDiscovered = false)
    {
        var source = new LogSource(_dir.FileIn("eq2log_Tester.txt"), parseFromStart, new object(), new EngineOptions(), TimeSpan.FromMilliseconds(5))
        {
            AutoDiscovered = autoDiscovered,
        };
        source.Processor.RaidLine += static (message, _) =>
        {
            if (message.Contains(PoisonMark, StringComparison.Ordinal))
                throw new InvalidOperationException("raid-line handler broke");
        };
        return source;
    }

    private static TailedLine Hit(long secondsIn) =>
        new($"({Epoch + secondsIn})[s] YOUR Smite hits a gnoll for 500 divine damage.", T0);

    private static readonly TailedLine Poison =
        new($"({Epoch})[s] Somebody tells you, \"{PoisonMark}\" about the raid", T0);

    /// <summary>SourcesViewModel.StatusFor as it was before the strings
    /// moved to the dictionaries, character for character.</summary>
    private static string LegacyStatus(LogSource source)
    {
        if (source.Error is { } error)
            return $"error: {error.Message}";
        var skipped = source.SkippedLines;
        return $"{source.Processor.LinesSeen:N0} lines · {source.Processor.LinesMatched:N0} matched · {source.Engine.History.Count} encounters{(source.Engine.InCombat ? " · IN COMBAT" : "")}"
            + (skipped > 0
                ? $" · {skipped:N0} skipped on error (last: {source.LastLineError?.GetType().Name}) — see the crashes folder"
                : "");
    }

    [Fact]
    public void The_English_Sources_Row_Reads_Exactly_As_It_Did()
    {
        using var source = Source();
        Assert.Equal("0 lines · 0 matched · 0 encounters", SourcesViewModel.StatusFor(source));
        Assert.Equal(LegacyStatus(source), SourcesViewModel.StatusFor(source));

        source.PumpLine(Hit(0));
        Assert.Equal("1 lines · 1 matched · 1 encounters · IN COMBAT", SourcesViewModel.StatusFor(source));
        Assert.Equal(LegacyStatus(source), SourcesViewModel.StatusFor(source));

        source.PumpLine(Poison);
        Assert.Equal(
            "2 lines · 1 matched · 1 encounters · IN COMBAT · 1 skipped on error (last: InvalidOperationException) — see the crashes folder",
            SourcesViewModel.StatusFor(source));
        Assert.Equal(LegacyStatus(source), SourcesViewModel.StatusFor(source));
    }

    [Fact]
    public void The_English_Error_Row_Reads_Exactly_As_It_Did()
    {
        using var source = Source();
        for (var i = 0; i < LineFaultBreaker.DefaultTripAfter; i++)
            source.PumpLine(Poison);
        Assert.NotNull(source.Error);

        var status = SourcesViewModel.StatusFor(source);

        Assert.Equal("error: " + source.Error.Message, status);
        Assert.Equal(LegacyStatus(source), status);
    }

    [Fact]
    public void The_Sources_Row_Is_Translated_And_Never_A_Key()
    {
        using var source = Source();
        source.PumpLine(Hit(0));
        source.PumpLine(Poison);

        InEveryLanguage(language =>
        {
            var status = SourcesViewModel.StatusFor(source);
            Assert.DoesNotContain("Sources_", status);
            // What is not interface text survives every language.
            Assert.Contains("InvalidOperationException", status);
            if (language != "en")
                Assert.NotEqual(LegacyStatus(source), status);
        });
        LocTestHost.In("de", () => Assert.Equal(
            "Zeilen: 2 · erkannt: 1 · Kämpfe: 1 · IM KAMPF · nach Fehler übersprungen: 1 (zuletzt: InvalidOperationException) — siehe den Ordner „crashes“",
            SourcesViewModel.StatusFor(source)));
    }

    [Fact]
    public void A_Stopped_Source_Says_So_In_The_Interface_Language_With_The_Reason_Untouched()
    {
        using var source = Source();
        for (var i = 0; i < LineFaultBreaker.DefaultTripAfter; i++)
            source.PumpLine(Poison);

        LocTestHost.In("de", () =>
            Assert.Equal("Fehler: " + source.Error!.Message, SourcesViewModel.StatusFor(source)));
    }

    [Fact]
    public void The_Source_Mode_Pill_Reads_As_It_Did_In_English_And_Is_Translated()
    {
        using var full = Source(parseFromStart: true);
        using var live = Source(parseFromStart: false);
        using var auto = Source(parseFromStart: false, autoDiscovered: true);

        Assert.Equal("full file", new SourceRow(full).Mode);
        Assert.Equal("live tail", new SourceRow(live).Mode);
        Assert.Equal("auto", new SourceRow(auto).Mode);
        LocTestHost.In("de", () => Assert.Equal("ganze Datei", new SourceRow(full).Mode));
        InEveryLanguage(_ => Assert.DoesNotContain("Sources_", new SourceRow(live).Mode));
    }

    // ── Overlay window headers ─────────────────────────────────────────────

    [Fact]
    public void Every_Overlay_Header_Reads_As_It_Did_In_English_And_Is_Never_A_Key()
    {
        Assert.Equal("DPS — drag, then lock", OverlayController.TitleFor(OverlayKind.MiniParseDps));
        Assert.Equal("HEALING — drag, then lock", OverlayController.TitleFor(OverlayKind.MiniParseHps));
        Assert.Equal("TANKING — drag, then lock", OverlayController.TitleFor(OverlayKind.MiniParseTank));
        Assert.Equal("TIMERS — drag, then lock", OverlayController.TitleFor(OverlayKind.TimerA));
        Assert.Equal("TIMERS B — drag, then lock", OverlayController.TitleFor(OverlayKind.TimerB));
        Assert.Equal("ALERTS — drag, then lock", OverlayController.TitleFor(OverlayKind.Notifications));

        InEveryLanguage(_ =>
        {
            foreach (var kind in Enum.GetValues<OverlayKind>())
                Assert.DoesNotContain("OverlayShell_", OverlayController.TitleFor(kind));
        });
        LocTestHost.In("de", () =>
            Assert.Equal("HEILUNG — ziehen, dann sperren", OverlayController.TitleFor(OverlayKind.MiniParseHps)));
    }

    // ── Curation workbench ─────────────────────────────────────────────────

    private static MinedAbility Mined(
        int casts = 3, int fights = 1, double? median = 30, double? baseline = 20, double swipe = 0.5,
        bool melee = false, bool detriment = false, string? effect = "stunned", double? effectSeconds = 4,
        bool inferred = true, string damageTypes = "heat") =>
        new("Test Keep", "a gnoll", "Smite", casts, fights, median, MinIntervalSeconds: 28, baseline, swipe,
            TotalDamage: 36_900, AvgTargets: 3, TicksPerCast: 2, melee, detriment, effect, effectSeconds, inferred, damageTypes);

    [Fact]
    public void The_English_Curation_Labels_Read_Exactly_As_They_Did()
    {
        var swiped = new CurationAbilityRow(Mined());
        Assert.Equal("3 casts / 1 fight", swiped.CastsLabel);
        Assert.Equal("~20s base  (raw 30s · min 28s)", swiped.RecastLabel);
        Assert.Equal(
            $"Swipe-adjusted: {0.5:P0} of interval time was under a recast debuff (counted at 1/1.5 rate). Base ≈ 20s — the right duration to store, because the live timer re-stretches automatically when the mob gets swiped.",
            swiped.RecastToolTip);
        Assert.Equal(
            $"{CombatantRow.Compact(12_300)}/cast · heat · stun ~4s · AOE ×3 · reapplies ×2 · source inferred",
            swiped.ProfileLabel);

        var plain = new CurationAbilityRow(Mined(casts: 1, fights: 2, swipe: 0, melee: true, effect: null, effectSeconds: null, inferred: false, damageTypes: ""));
        Assert.Equal("1 cast / 2 fights", plain.CastsLabel);
        Assert.Equal("~30s  (min 28s)", plain.RecastLabel);
        Assert.Equal("No recast debuffs observed on this mob — raw and base agree.", plain.RecastToolTip);
        Assert.Equal($"{CombatantRow.Compact(36_900)}/cast · melee · AOE ×3 · reapplies ×2", plain.ProfileLabel);

        var spell = new CurationAbilityRow(Mined(median: null, melee: false, effect: "gloomy", effectSeconds: null, inferred: false, damageTypes: ""));
        Assert.Equal("single cast", spell.RecastLabel);
        // A flavour word the log used, with no measured duration, is shown as the log spelled it.
        Assert.Equal($"{CombatantRow.Compact(12_300)}/cast · spell · gloomy · AOE ×3 · reapplies ×2", spell.ProfileLabel);

        var control = new CurationAbilityRow(Mined(detriment: true));
        Assert.Equal("detriment / control · stun ~4s · AOE ×3 · reapplies ×2 · source inferred", control.ProfileLabel);
    }

    [Fact]
    public void The_English_Curation_Counts_Read_As_They_Did_And_One_Ability_Is_Singular()
    {
        Assert.Equal("2 mobs · 5 abilities · 3 timeable without a timer", CurationViewModel.SummaryLabel(2, 5, 3));
        Assert.Equal("1 mob · 1 ability · 0 timeable without a timer", CurationViewModel.SummaryLabel(1, 1, 0));
        Assert.Equal("5 abilities", new CurationMobRow("a gnoll", 5).AbilityCountLabel);
        // The header used to read "1 abilities": it was a XAML StringFormat with no singular.
        Assert.Equal("1 ability", new CurationMobRow("a gnoll", 1).AbilityCountLabel);
    }

    [Fact]
    public void The_Curation_Labels_Are_Translated_And_Keep_What_The_Log_Named()
    {
        var row = new CurationAbilityRow(Mined());

        InEveryLanguage(_ =>
        {
            foreach (var label in new[] { row.CastsLabel, row.RecastLabel, row.RecastToolTip, row.ProfileLabel, CurationViewModel.SummaryLabel(2, 5, 3) })
                Assert.DoesNotContain("Curation_", label);
            // The damage school and the control word are the log's (and
            // the timer's stored value), not interface text.
            Assert.Contains("heat", row.ProfileLabel);
            Assert.Contains("stun", row.ProfileLabel);
        });
        LocTestHost.In("de", () => Assert.Equal("3 Casts / 1 Kampf", row.CastsLabel));
        // Russian counts are worded so the noun never has to agree with the number.
        LocTestHost.In("ru", () => Assert.Equal("кастов: 3 / боёв: 1", row.CastsLabel));
    }

    // ── Archive window ─────────────────────────────────────────────────────

    [Fact]
    public void The_English_Archive_Labels_Read_Exactly_As_They_Did()
    {
        Assert.Equal("Nothing found.", ArchiveViewModel.ResultLabelFor(0, 0));
        Assert.Equal("1 fight · 1 boss", ArchiveViewModel.ResultLabelFor(1, 1));
        Assert.Equal("12 fights · 3 bosses", ArchiveViewModel.ResultLabelFor(12, 3));

        var trash = new ArchiveRow(Summary(isBoss: false), loaded: false);
        var boss = new ArchiveRow(Summary(isBoss: true), loaded: false);
        Assert.Equal($"1:35  ·  {CombatantRow.Compact(36_900)} dmg  ·  Tester's log  ·  trash", trash.DetailLabel);
        Assert.Equal($"1:35  ·  {CombatantRow.Compact(36_900)} dmg  ·  Tester's log", boss.DetailLabel);
    }

    [Fact]
    public void The_Archive_Labels_Are_Translated_And_Never_A_Key()
    {
        var row = new ArchiveRow(Summary(isBoss: false), loaded: false);

        InEveryLanguage(_ =>
        {
            Assert.DoesNotContain("Archive_", row.DetailLabel);
            Assert.Contains("Tester", row.DetailLabel);
            Assert.DoesNotContain("Archive_", ArchiveViewModel.ResultLabelFor(0, 0));
            Assert.DoesNotContain("Archive_", ArchiveViewModel.ResultLabelFor(12, 3));
        });
        LocTestHost.In("de", () => Assert.Equal("12 Kämpfe · 3 Bosse", ArchiveViewModel.ResultLabelFor(12, 3)));
    }

    private static EncounterSummary Summary(bool isBoss) =>
        new(1, "source", "Tester", "Test Keep", "a gnoll", T0, T0.AddSeconds(95), 95, SuccessLevel.Win, 36_900, null, isBoss);

    // ── Trigger / timer tree ───────────────────────────────────────────────

    private sealed record TreeItem(string Zone, string Category, bool Lexicon);

    private static CategoryTree<TreeItem> Tree()
    {
        var tree = new CategoryTree<TreeItem>
        {
            ZoneOf = i => i.Zone,
            CategoryOf = i => i.Category,
            IsLexicon = i => i.Lexicon,
            EnabledOf = _ => true,
            SortKeyOf = i => i.Category,
            MakeRow = i => i,
            LexiconHost = () => "example.test",
        };
        tree.Rebuild([new TreeItem("", "General", false), new TreeItem("Test Keep", "a gnoll", true)], _ => true, filtering: false);
        return tree;
    }

    [Fact]
    public void The_Tree_Sections_Read_As_They_Did_In_English()
    {
        var rows = Tree().Rows;

        var sections = rows.OfType<SectionRow>().ToList();
        Assert.Equal(new SectionRow("Custom", "yours — edit, drag, delete"), sections[0]);
        Assert.Equal(new SectionRow("Lexicon", "curated on example.test — enable/disable here, edit there"), sections[1]);
        Assert.Equal("General", rows.OfType<ZoneRow>().First().Name);
    }

    [Fact]
    public void The_No_Zone_Header_Is_Translated_But_Its_Key_Is_Not()
    {
        LocTestHost.In("de", () =>
        {
            var rows = Tree().Rows;
            var general = rows.OfType<ZoneRow>().First();

            Assert.Equal("Allgemein", general.Name);
            // Collapse state, drop targets and the "is this the no-zone
            // group" checks all go by the key, which no language changes.
            Assert.Equal("General", general.Key);
            Assert.Equal("General", rows.OfType<CategoryRow>().First().Tag);
            Assert.Equal("Eigene", rows.OfType<SectionRow>().First().Title);
            // A zone that has a name is the log's, and is shown as it is.
            Assert.Equal("Test Keep", rows.OfType<ZoneRow>().Last().Name);
        });
    }

    // ── Callout toast ──────────────────────────────────────────────────────

    private static string Spoken(string effect, int count)
    {
        var word = Vocabulary.CalloutWord(effect);
        return count == 1 ? $"1 player {word}" : $"{count} players {word}";
    }

    [Fact]
    public void The_English_Callout_Toast_Is_The_Sentence_That_Is_Spoken()
    {
        foreach (var effect in Vocabulary.ControlEffects)
        {
            foreach (var count in new[] { 1, 8 })
                Assert.Equal(Spoken(effect, count), SourceManager.CalloutToast(effect, count, Spoken(effect, count)));
        }
    }

    [Fact]
    public void The_Callout_Toast_Is_Translated_While_The_Spoken_Sentence_Stays_English()
    {
        LocTestHost.In("de", () => Assert.Equal("8 Spieler betäubt", SourceManager.CalloutToast("stun", 8, Spoken("stun", 8))));
        LocTestHost.In("fr", () => Assert.Equal("1 joueur étourdi", SourceManager.CalloutToast("stun", 1, Spoken("stun", 1))));
        LocTestHost.In("fr", () => Assert.Equal("8 joueurs étourdis", SourceManager.CalloutToast("stun", 8, Spoken("stun", 8))));
        LocTestHost.In("ru", () => Assert.Equal("Игроков в стане: 8", SourceManager.CalloutToast("stun", 8, Spoken("stun", 8))));
        InEveryLanguage(_ =>
        {
            foreach (var effect in Vocabulary.ControlEffects)
                Assert.DoesNotContain("Callout_", SourceManager.CalloutToast(effect, 3, Spoken(effect, 3)));
        });
    }

    [Fact]
    public void A_Callout_For_An_Effect_With_No_String_Shows_The_Spoken_Sentence_Not_A_Key() =>
        InEveryLanguage(_ => Assert.Equal("3 players gloomy", SourceManager.CalloutToast("gloomy", 3, "3 players gloomy")));

    // ── Crash dialogs ──────────────────────────────────────────────────────

    [Fact]
    public void The_Crash_Dialog_English_In_Code_Is_The_English_In_The_Dictionary()
    {
        foreach (var (key, english) in CrashLog.DialogEnglish)
            Assert.Equal(english, Loc.Get(key));
    }

    [Fact]
    public void The_English_Crash_Dialog_Reads_Exactly_As_It_Did()
    {
        Assert.Equal(
            "EQ2Parser could not start and has to close.\n\nboom\n\nDetails saved to:\nsomewhere",
            CrashLog.DialogBody("CrashDlg_StartupFailed", "boom", "somewhere"));
        Assert.Equal("EQ2Parser hit the same error repeatedly and has to close.", CrashLog.DialogText("CrashDlg_CrashLoop"));
        Assert.Equal("EQ2Parser hit an unexpected error and kept running.", CrashLog.DialogText("CrashDlg_KeptRunning"));
        Assert.Equal("EQ2Parser hit a fatal error and has to close.", CrashLog.DialogText("CrashDlg_Fatal"));
        Assert.Equal("EQ2Parser — error report", CrashLog.DialogText("CrashDlg_Title"));
    }

    [Fact]
    public void The_Crash_Dialog_Is_Translated_With_The_Exception_Message_Untouched()
    {
        LocTestHost.In("de", () => Assert.Equal(
            "EQ2Parser konnte nicht starten und muss beendet werden.\n\nboom\n\nDetails gespeichert unter:\nsomewhere",
            CrashLog.DialogBody("CrashDlg_StartupFailed", "boom", "somewhere")));
        InEveryLanguage(_ =>
        {
            foreach (var key in CrashLog.DialogEnglish.Keys)
                Assert.DoesNotContain("CrashDlg_", CrashLog.DialogText(key));
        });
    }

    [Fact]
    public void A_Crash_Before_The_Dictionaries_Load_Still_Explains_Itself_In_English()
    {
        // Loc.Get hands the key back when it has nothing loaded — which is
        // the state a startup failure can be in. The dialog must not show it.
        foreach (var (key, english) in CrashLog.DialogEnglish)
            Assert.Equal(english, CrashLog.OrEnglish(key, resolved: key));

        Assert.Equal("übersetzt", CrashLog.OrEnglish("CrashDlg_Fatal", resolved: "übersetzt"));
    }
}
