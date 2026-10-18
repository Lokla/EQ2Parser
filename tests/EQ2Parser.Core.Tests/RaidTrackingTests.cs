using EQ2Parser.Core.Raid;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.Core.Tests;

/// <summary>Raid-attendance building blocks: /who block parsing (verbatim
/// live shapes), roster accumulation from deltas + presence + fight allies,
/// the positional who-pair classification, DKP command files, and
/// install-dir derivation.</summary>
public sealed class RaidTrackingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 25, 21, 4, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    // ── WhoParser (verbatim corpus lines, 2026-06-25 raid night) ────────────

    [Fact]
    public void Who_Block_Parses_Detail_Anonymous_And_Afk_Rows()
    {
        var p = new WhoParser();
        Assert.Null(p.Feed("/who search results for Throne of New Tunaria:", T0));
        Assert.Null(p.Feed("------------------------------------------", T0));
        Assert.Null(p.Feed("[70 Conjuror] Tsuna (Freeblood) <Exordium> Zone: Throne of New Tunaria", T0));
        Assert.Null(p.Feed("[Anonymous] Betabonk", T0));
        Assert.Null(p.Feed("[Anonymous] Ashtar (AFK)", T0));
        Assert.Null(p.Feed("[70 Fury] Vessix (Arasai) <Gin and Jumjum> Zone: The City of Freeport", T0));
        var result = p.Feed("4 players found", T0);

        Assert.NotNull(result);
        Assert.Equal("Throne of New Tunaria", result.ZoneFilter);
        Assert.Equal(4, result.Rows.Count);
        var tsuna = result.Rows[0];
        Assert.Equal(("Tsuna", 70, "Conjuror", "Freeblood", "Exordium"), (tsuna.Name, tsuna.Level, tsuna.Class, tsuna.Race, tsuna.Guild));
        Assert.False(tsuna.Afk);
        Assert.True(result.Rows[2].Afk);           // Ashtar (AFK)
        Assert.Null(result.Rows[1].Level);         // anonymous rows carry name only
    }

    // \d+ admits a level no int can hold; int.Parse threw on it, on the
    // log-line path. Such a row is simply not a /who row.
    [Fact]
    public void Who_Row_With_An_Unreadable_Level_Is_Ignored_Not_Thrown()
    {
        var p = new WhoParser();
        Assert.Null(p.Feed("/who search results:", T0));
        Assert.Null(p.Feed("[99999999999 Fury] Tester (Arasai) Zone: Somewhere", T0));
        Assert.Null(p.Feed("[70 Fury] Other (Arasai) Zone: Somewhere", T0));
        var result = p.Feed("1 player found", T0);

        Assert.NotNull(result);
        var row = Assert.Single(result.Rows);
        Assert.Equal(("Other", 70), (row.Name, row.Level));
    }

    [Fact]
    public void Who_Singular_Footer_And_Plain_Header()
    {
        var p = new WhoParser();
        Assert.Null(p.Feed("/who search results:", T0));
        Assert.Null(p.Feed("[Anonymous] Ashtar (AFK)", T0));
        var result = p.Feed("1 player found", T0);
        Assert.NotNull(result);
        Assert.Null(result.ZoneFilter);
        Assert.True(Assert.Single(result.Rows).Afk);
    }

    [Fact]
    public void Who_Block_Abandoned_After_Timeout()
    {
        var p = new WhoParser();
        p.Feed("/who search results:", T0);
        p.Feed("[Anonymous] Betabonk", T0);
        // A footer arriving way later belongs to some other output.
        Assert.Null(p.Feed("1 player found", At(30)));
    }

    // ── RaidRosterTracker ───────────────────────────────────────────────────

    /// <summary>Feeds a minimal VALIDATED macro pair (non-empty whoraid +
    /// pure single-guild block) — the only way a session starts.</summary>
    private static void Arm(RaidRosterTracker t, DateTimeOffset at)
    {
        t.OnLine("/whoraid search results for Veeshan's Peak:", at);
        t.OnLine("[70 Conjuror] Tsuna (Freeblood)", at);
        t.OnLine("1 player found", at);
        t.OnLine("/who search results:", at.AddSeconds(1));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood) <Paragon> Zone: Veeshan's Peak", at.AddSeconds(1));
        t.OnLine("[70 Templar] Menludiir (Gnome) <Paragon> Zone: Veeshan's Peak", at.AddSeconds(1));
        t.OnLine("2 players found", at.AddSeconds(1));
    }

    [Fact]
    public void Raid_Deltas_And_Presence_Accumulate()
    {
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        Arm(t, T0);
        t.OnLine("Shadynecro has joined the raid.", At(1));
        t.OnLine("Betabonk's group has joined the raid.", At(2));
        t.OnLine("Guildmate: Coyi has logged in.", At(3));
        t.OnLine("Shadynecro has left the raid.", At(60));
        t.OnLine("Distraction's group has left the raid, Betabonk is now the raid leader.", At(61));
        t.OnLine("Guildmate: Coyi has logged out.", At(90));

        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.False(by["Shadynecro"].InRaid);
        Assert.True(by["Shadynecro"].Online);        // left raid, still online
        Assert.True(by["Betabonk"].InRaid);
        Assert.False(by["Distraction"].InRaid);      // group-leave variant with leader suffix
        Assert.False(by["Coyi"].Online);
        Assert.False(by.ContainsKey("Friend"));      // nothing bogus
    }

    [Fact]
    public void Fight_Allies_Catch_PreJoin_Members_Once_Armed()
    {
        // Martyn was in the raid before our own join — no delta ever names
        // him joining, but he appears in a fight's ally set.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        Arm(t, T0);
        t.OnFightAllies(["Martyn", "a krait patriarch", "Betabonk"], At(300));
        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.True(by["Martyn"].InRaid);
        Assert.True(by["Betabonk"].InRaid);
        Assert.False(by.ContainsKey("a krait patriarch")); // articled mob filtered
    }

    [Fact]
    public void Unarmed_Tracker_Ignores_Every_Passive_Signal()
    {
        // The live 2026-09-10 disaster: guildies running SIX-MAN content all
        // day minted phantom "raids" — fight allies, deltas and presence
        // lines fed attendance with no macro press anywhere. Until a
        // validated pair arms the tracker, none of it may count.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("Shadynecro has joined the raid.", At(1));
        t.OnLine("Betabonk's group has joined the raid.", At(2));
        t.OnLine("Guildmate: Coyi has logged in.", At(3));
        t.OnFightAllies(["Martyn", "Betabonk"], At(10));
        Assert.False(t.Armed);
        Assert.Empty(t.Snapshot());

        // The macro press arms it — from then on the same signals count.
        Arm(t, At(60));
        Assert.True(t.Armed);
        t.OnLine("Shadynecro has joined the raid.", At(70));
        Assert.True(t.Snapshot().Single(m => m.Name == "Shadynecro").InRaid);
    }

    [Fact]
    public void Macro_Pair_Outside_A_Raid_Is_Rejected()
    {
        // Verbatim live capture 2026-09-10: the macro pressed during a
        // 6-man — whoraid answers "Not in a raid" and completes EMPTY, then
        // the (valid-looking) guild who lands. An empty raid half means
        // this was not a raid: nothing applies, nothing arms.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/whoraid search results for Charasis: Vault of Eternal Sleep:", At(0));
        t.OnLine("------------------------------------------", At(0));
        t.OnLine("Not in a raid", At(0));
        t.OnLine("0 players found", At(0));
        t.OnLine("/who search results:", At(1));
        t.OnLine("[74 Troubador] Waverat (Ratonga) <Paragon> Zone: The Crypt of Agony", At(1));
        t.OnLine("[70 Illusionist] Neomi (Fae) <Paragon> Zone:    12 Qeynos Place", At(1));
        t.OnLine("2 players found", At(1));

        Assert.False(t.Armed);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Pair_With_A_Mixed_Guild_Block_Is_Rejected()
    {
        // A zone who right after a whoraid is NOT the macro — its rows span
        // guilds (or carry none). The pure-guild validation catches it.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/whoraid search results for Veeshan's Peak:", At(0));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood)", At(0));
        t.OnLine("1 player found", At(0));
        t.OnLine("/who search results for The Commonlands:", At(1));
        t.OnLine("[70 Brigand] Xantos (Dark Elf) <Some Other Guild> Zone: The Commonlands", At(1));
        t.OnLine("[70 Necromancer] Shadynecro (Gnome) <Paragon> Zone: The Commonlands", At(1));
        t.OnLine("2 players found", At(1));

        Assert.False(t.Armed);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Pair_With_An_Anonymous_Or_Guildless_Row_Is_Rejected()
    {
        // "/who all" output right after a whoraid: anonymous rows (and
        // detailed strangers without a guild tag) can never appear in a
        // guild who — guildmates always show full detail to each other.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/whoraid search results for Veeshan's Peak:", At(0));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood)", At(0));
        t.OnLine("1 player found", At(0));
        t.OnLine("/who search results:", At(1));
        t.OnLine("[70 Templar] Menludiir (Gnome) <Paragon> Zone: Veeshan's Peak", At(1));
        t.OnLine("[Anonymous] Betabonk", At(1));
        t.OnLine("2 players found", At(1));
        Assert.False(t.Armed);
        Assert.Empty(t.Snapshot());

        // Guildless detailed row — same verdict.
        t.OnLine("/whoraid search results for Veeshan's Peak:", At(20));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood)", At(20));
        t.OnLine("1 player found", At(20));
        t.OnLine("/who search results:", At(21));
        t.OnLine("[70 Templar] Menludiir (Gnome) Zone: Veeshan's Peak", At(21));
        t.OnLine("1 player found", At(21));
        Assert.False(t.Armed);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Fresh_Pair_A_Raid_Night_Later_Auto_Rolls_The_Session()
    {
        // Nobody pressed "New session" between raid nights — the tracker
        // must not let Tuesday's roster haunt Thursday's attendance.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        Arm(t, T0); // night 1: Tsuna in raid
        Assert.Contains(t.Snapshot(), m => m.Name == "Tsuna" && m.InRaid);

        var night2 = T0.AddHours(48);
        t.OnLine("/whoraid search results for Chelsith:", night2);
        t.OnLine("[80 Berserker] Badbang (Ogre)", night2);
        t.OnLine("1 player found", night2);
        t.OnLine("/who search results:", night2.AddSeconds(1));
        t.OnLine("[80 Berserker] Badbang (Ogre) <Paragon> Zone: Chelsith", night2.AddSeconds(1));
        t.OnLine("1 player found", night2.AddSeconds(1));

        var members = t.Snapshot();
        Assert.DoesNotContain(members, m => m.Name == "Tsuna"); // night 1 wiped
        Assert.Contains(members, m => m.Name == "Badbang" && m.InRaid);
        Assert.Equal(night2.AddSeconds(1), t.SessionStarted);
    }

    [Fact]
    public void Plain_Who_Blocks_Are_Ignored_Without_A_Whoraid_Partner()
    {
        // Live bug 2026-09-02: a manual "/who all" (arbitrary server
        // players, same header shape as the guild who) was polluting the
        // sit-out list. Plain blocks now count ONLY as the guild half of
        // the whoraid pair — alone (or paired with each other) they are
        // ignored entirely.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/who search results:", At(0));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood) <Exordium> Zone: Throne of New Tunaria", At(0));
        t.OnLine("1 player found", At(0));
        t.OnLine("/who search results:", At(2));
        t.OnLine("[70 Templar] Coyi (High Elf) <Exordium> Zone: Qeynos Harbor", At(2));
        t.OnLine("1 player found", At(2));

        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Zone_Who_During_A_Session_Does_Not_Touch_The_Roster()
    {
        // A "/who" of the current zone mid-raid (strangers + a guildie who
        // IS raiding) must change nothing — not even for known members.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        Arm(t, T0);
        t.OnLine("Shadynecro has joined the raid.", At(2));
        t.OnLine("/who search results for The Commonlands:", At(60));
        t.OnLine("[70 Brigand] Xantos (Dark Elf) <Some Other Guild> Zone: The Commonlands", At(60));
        t.OnLine("[70 Necromancer] Shadynecro (Gnome) <Paragon> Zone: The Commonlands", At(60));
        t.OnLine("2 players found", At(60));

        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.False(by.ContainsKey("Xantos"));       // stranger never enters the roster
        Assert.True(by["Shadynecro"].InRaid);         // known member untouched
    }

    [Fact]
    public void Whoraid_Then_Guild_Pair_Classifies_From_Live_Macro_Output()
    {
        // Verbatim lines from the live macro run 2026-09-02 (the whoraid
        // echo has its own header shape and its rows carry no guild/zone —
        // the original header regex missed it, dumping everyone into the
        // guild-only lone-block path = the whole raid shown as sitting out).
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/whoraid search results for Qeynos Province District:", At(0));
        t.OnLine("----------------------------------------", At(0));
        t.OnLine("[70 Templar] Menludiir (Gnome)", At(0));
        t.OnLine("[70 Berserker] Badbang (Ogre)", At(0));
        t.OnLine("[70 Inquisitor] Avacii (Dwarf)", At(0));
        t.OnLine("3 players found", At(0));
        t.OnLine("/who search results:", At(1));
        t.OnLine("[70 Coercer] Adomia (Ratonga) <Paragon> (AFK) Zone: The Feerrott", At(1));
        t.OnLine("[70 Berserker] Badbang (Ogre) <Paragon> Zone: The Sinking Sands", At(1));
        t.OnLine("[70 Wizard] Masqueraid (High Elf) <Paragon> Zone: Qeynos Capitol District", At(1));
        t.OnLine("[70 Illusionist] Neomi (Fae) <Paragon> Zone:    12 Qeynos Place", At(1));
        t.OnLine("10 players found", At(1));

        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.True(by["Menludiir"].InRaid);
        Assert.True(by["Badbang"].InRaid);            // in both blocks — raid wins
        Assert.True(by["Avacii"].InRaid);
        Assert.False(by["Adomia"].InRaid);            // guild who = online only
        Assert.True(by["Adomia"].Online);
        Assert.True(by["Adomia"].Afk);                // (AFK) between guild tag and Zone
        Assert.False(by["Masqueraid"].InRaid);
        Assert.True(by.ContainsKey("Neomi"));         // house-address zone with padded spaces
        Assert.Equal("Templar", by["Menludiir"].Class); // whoraid rows still carry class

        // Guild-membership evidence from the pair: in the guild who = true;
        // in raid but absent from the guild who = provably not in guild.
        Assert.True(by["Badbang"].InGuild);
        Assert.True(by["Adomia"].InGuild);
        Assert.False(by["Menludiir"].InGuild);
        Assert.False(by["Avacii"].InGuild);
    }

    [Fact]
    public void Not_In_Guild_Is_Unknown_Without_A_Guild_Who_And_Sticky_Once_True()
    {
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        Arm(t, T0);
        // Delta join + guildmate line after arming: no false conclusions.
        t.OnLine("Shadynecro has joined the raid.", At(2));
        t.OnLine("Guildmate: Coyi has logged in.", At(3));
        var by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.Null(by["Shadynecro"].InGuild);        // unknown — still gets DKP (best effort)
        Assert.True(by["Coyi"].InGuild);

        // A later pair whose guild block misses Coyi must NOT downgrade the
        // sticky guildmate evidence; Shadynecro (absent) flips to false.
        t.OnLine("/whoraid search results for Veeshan's Peak:", At(10));
        t.OnLine("[70 Necromancer] Shadynecro (Gnome)", At(10));
        t.OnLine("1 player found", At(10));
        t.OnLine("/who search results:", At(11));
        t.OnLine("[70 Templar] Menludiir (Gnome) <Paragon> Zone: Veeshan's Peak", At(11));
        t.OnLine("1 player found", At(11));
        by = t.Snapshot().ToDictionary(m => m.Name);
        Assert.False(by["Shadynecro"].InGuild);
        Assert.True(by["Coyi"].InGuild);
    }

    [Fact]
    public void Lone_Whoraid_Block_Is_Held_Never_Applied()
    {
        // A whoraid alone proves nothing (typed by hand, pickup raid, or a
        // partial macro read) — it is only the pending half of the pair.
        var t = new RaidRosterTracker();
        t.StartNewSession(T0);
        t.OnLine("/whoraid search results for Veeshan's Peak:", At(0));
        t.OnLine("[70 Conjuror] Tsuna (Freeblood)", At(0));
        t.OnLine("1 player found", At(0));

        Assert.False(t.Armed);
        Assert.Empty(t.Snapshot());

        // The guild half arriving PAST the pair window doesn't pair either.
        t.OnLine("/who search results:", At(30));
        t.OnLine("[70 Templar] Menludiir (Gnome) <Paragon> Zone: Veeshan's Peak", At(30));
        t.OnLine("1 player found", At(30));
        Assert.False(t.Armed);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Prefilter_Accepts_All_Signal_Shapes()
    {
        Assert.True(RaidRosterTracker.LooksRelevant("Shadynecro has joined the raid."));
        Assert.True(RaidRosterTracker.LooksRelevant("Guildmate: Coyi has logged in."));
        Assert.True(RaidRosterTracker.LooksRelevant("/who search results:"));
        Assert.True(RaidRosterTracker.LooksRelevant("[Anonymous] Betabonk"));
        Assert.True(RaidRosterTracker.LooksRelevant("24 players found"));
        Assert.False(RaidRosterTracker.LooksRelevant("YOU hit a training dummy for 100 points of crushing damage."));
    }

    // ── DkpCommandFile ──────────────────────────────────────────────────────

    [Fact]
    public void Awards_Without_Mains_Are_Raid_Grant_Plus_SitOuts()
    {
        var entries = DkpCommandFile.BuildAwardEntries(5, "Raid DKP: end of raid", ["Whoever"], ["Menludiir", "Coyi", "a mob"]);
        Assert.Equal(
            ["raid", "Coyi", "Menludiir"], // "a mob" filtered by the player-name shape
            entries.Select(e => e.Player).ToList());
        Assert.Equal("guild points add 5 raid Raid DKP: end of raid", DkpCommandFile.AwardCommand(entries[0]));
        // The bulk grant announces as "the raid".
        Assert.Equal("5 dkp awarded to the raid (Raid DKP: end of raid)", DkpCommandFile.AwardAnnouncement(entries[0]));
        Assert.Equal("5 dkp awarded to Coyi (Raid DKP: end of raid)", DkpCommandFile.AwardAnnouncement(entries[1]));
    }

    [Fact]
    public void Award_Reason_Is_Sanitised()
    {
        var entry = Assert.Single(DkpCommandFile.BuildAwardEntries(3, "/quit\r\nhaha", [], []));
        Assert.Equal("guild points add 3 raid quit haha", DkpCommandFile.AwardCommand(entry));
    }

    [Fact]
    public void Awards_With_Mains_Grant_Individually_To_Mains()
    {
        // Alty is Mainy's raid alt; Tanky raids on their main. No bulk grant —
        // every award is individual and addressed to the MAIN.
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alty"] = "Mainy",
            ["Mainy"] = "Mainy",
            ["Tanky"] = "Tanky",
        };
        var entries = DkpCommandFile.BuildAwardEntries(5, "DKP", ["Alty", "Tanky", "Pugsy"], [], mains);
        Assert.Equal(["Mainy", "Pugsy", "Tanky"], entries.Select(e => e.Player).ToList()); // unmapped pug → self
    }

    [Fact]
    public void Awards_With_Mains_Dedupe_Main_And_Alt_Both_Present()
    {
        // Dual-boxing main + alt, and a sit-out alt whose main already got
        // the raid grant: exactly one award per main.
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alty"] = "Mainy",
            ["Mainy"] = "Mainy",
            ["Benchalt"] = "Tanky",
            ["Tanky"] = "Tanky",
        };
        var entries = DkpCommandFile.BuildAwardEntries(5, "DKP", ["Mainy", "Alty", "Tanky"], ["Benchalt", "Coyi"], mains);
        Assert.Equal(["Mainy", "Tanky", "Coyi"], entries.Select(e => e.Player).ToList()); // sit-out, unmapped → self
    }

    [Fact]
    public void Awards_With_Mains_Collapse_Two_Boxed_Characters_To_One_Grant()
    {
        // One player runs TWO characters in the raid (second account); their
        // main isn't even present. Both rows map to the main -> ONE award.
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alty"] = "Mainy",
            ["Boxling"] = "Mainy",
            ["Tanky"] = "Tanky",
        };
        var entries = DkpCommandFile.BuildAwardEntries(5, "DKP", ["Alty", "Boxling", "Tanky"], [], mains);
        Assert.Equal(["Mainy", "Tanky"], entries.Select(e => e.Player).ToList());
    }

    [Fact]
    public void Awards_With_Mains_Are_Case_Insensitive()
    {
        var mains = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ALTY"] = "Mainy" };
        var entry = Assert.Single(DkpCommandFile.BuildAwardEntries(5, "DKP", ["alty"], [], mains));
        Assert.Equal("Mainy", entry.Player);
    }

    [Fact]
    public void Step_File_Is_Command_Announcement_Marker()
    {
        var entry = new AwardEntry("Mainy", 5, "DKP");
        var text = DkpCommandFile.BuildStepFile(
            DkpCommandFile.AwardCommand(entry), DkpCommandFile.AwardAnnouncement(entry), DkpCommandFile.MarkerCommand);
        Assert.Equal(
            "guild points add 5 Mainy DKP\r\nof 5 dkp awarded to Mainy (DKP)\r\neq2lexicon_dkp_done\r\n",
            text);
    }

    // ── DKP press-until-done loop (throttle discovered live 2026-09-02) ─────

    [Fact]
    public void Queue_File_For_Empty_Queue_Is_Marker_Only()
    {
        Assert.Equal("eq2lexicon_dkp_done\r\n", DkpCommandFile.BuildQueueFile([]));
        Assert.Equal("eq2lexicon_loot_done\r\n", DkpCommandFile.BuildStepFile(null, null, DkpCommandFile.LootMarkerCommand));
    }

    [Fact]
    public void Award_Echo_Confirms_With_Zero_Failures()
    {
        // Own echo wrapper. The points command ran (no throttle line), so the
        // echo arrives with failures == 0 — the confirmation to advance on.
        var p = new DkpAwardProgress();
        var echoes = new List<(string Message, int Failures)>();
        p.AwardEchoSeen += (m, f) => echoes.Add((m, f));

        p.OnLine(@"You say to the officers, ""5 dkp awarded to Mainy (Raid DKP)""", T0);
        // Unrelated chatter must not contaminate anything.
        p.OnLine("Guildmate: Coyi has logged in.", T0);

        var echo = Assert.Single(echoes);
        Assert.Equal(0, echo.Failures);
        Assert.Contains("5 dkp awarded to Mainy (Raid DKP)", echo.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Throttled_Press_Reports_Failures_With_The_Echo()
    {
        // Pressed too soon: the points command throttles but the chat line
        // still fires — failures > 0 tells the app NOT to advance.
        var p = new DkpAwardProgress();
        var echoes = new List<int>();
        p.AwardEchoSeen += (_, f) => echoes.Add(f);

        p.OnLine(DkpCommandFile.ThrottleLogLine, T0);
        p.OnLine(@"You say to the officers, ""5 dkp awarded to Mainy (Raid DKP)""", T0);
        // Next press: applied cleanly.
        p.OnLine(@"You say to the officers, ""5 dkp awarded to Mainy (Raid DKP)""", At(20));

        Assert.Equal([1, 0], echoes);
    }

    [Fact]
    public void Loot_Echo_Routes_Separately_And_Sees_Other_Officers()
    {
        var p = new DkpAwardProgress();
        var loot = new List<(string Message, int Failures)>();
        var award = new List<string>();
        p.LootEchoSeen += (m, f) => loot.Add((m, f));
        p.AwardEchoSeen += (m, _) => award.Add(m);

        // ANOTHER officer's macro press is visible too (cross-parser sync).
        p.OnLine(@"\aPC -1 Ariadneh:Ariadneh\/a says to the officers, ""\aITEM -809902670 226691356:Torc of Winding Waters\/a assigned to Alty for 25 dkp""", T0);

        var echo = Assert.Single(loot);
        Assert.Equal(0, echo.Failures);
        Assert.Empty(award);
    }

    [Fact]
    public void Markers_Route_By_File_With_Failure_Counts()
    {
        var p = new DkpAwardProgress();
        var presses = new List<(string Marker, int Failures)>();
        p.PressDetected += (marker, failures) => presses.Add((marker, failures));

        p.OnLine(DkpCommandFile.MarkerLogLine, T0);
        p.OnLine(DkpCommandFile.ThrottleLogLine, At(1));
        p.OnLine(DkpCommandFile.LootMarkerLogLine, At(1));

        Assert.Equal([(DkpCommandFile.MarkerCommand, 0), (DkpCommandFile.LootMarkerCommand, 1)], presses);
    }

    [Fact]
    public void Dkp_Progress_Lines_Pass_The_Raid_Prefilter()
    {
        Assert.True(RaidRosterTracker.LooksRelevant(DkpCommandFile.ThrottleLogLine));
        Assert.True(RaidRosterTracker.LooksRelevant(DkpCommandFile.MarkerLogLine));
        Assert.True(RaidRosterTracker.LooksRelevant(DkpCommandFile.LootMarkerLogLine));
        Assert.True(RaidRosterTracker.LooksRelevant(@"You say to the officers, ""5 dkp awarded to Mainy (Raid DKP)"""));
        Assert.True(RaidRosterTracker.LooksRelevant(@"You say to the officers, ""\aITEM 1 2:Torc\/a assigned to Alty for 25 dkp"""));
        Assert.False(RaidRosterTracker.LooksRelevant("Unknown command: 'somethingelse'"));
    }

    [Fact]
    public void Refresh_File_Is_The_Ordered_Pair()
    {
        Assert.Equal("whoraid\r\nwho all guild\r\n", DkpCommandFile.BuildRefresh());
    }

    // ── LogPaths.ParseInstallDir ────────────────────────────────────────────

    [Theory]
    [InlineData("/home/u/EverQuest 2/logs/Varsoon/eq2log_Menludiir.txt", "/home/u/EverQuest 2")]
    [InlineData("/home/u/EQ2/logs/eq2log.txt", "/home/u/EQ2")]
    [InlineData("/home/u/SomewhereElse/eq2log_Menludiir.txt", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseInstallDir_Cases(string? logPath, string? expected)
    {
        Assert.Equal(expected, LogPaths.ParseInstallDir(logPath));
    }

    [WindowsTheory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\EverQuest 2\logs\Varsoon\eq2log_Menludiir.txt", @"D:\SteamLibrary\steamapps\common\EverQuest 2")]
    [InlineData(@"C:\EQ2\logs\eq2log.txt", @"C:\EQ2")]
    [InlineData(@"C:\SomewhereElse\eq2log_Menludiir.txt", null)]
    public void ParseInstallDir_Windows_Paths(string logPath, string? expected)
    {
        Assert.Equal(expected, LogPaths.ParseInstallDir(logPath));
    }
}
