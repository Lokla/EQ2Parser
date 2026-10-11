using System.Text.Json;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Logs;
using EQ2Parser.Core.Upload;

namespace EQ2Parser.Core.Tests;

/// <summary>
/// The per-ability rows of the upload (<c>attack_types</c>): one row per
/// (ability, swing category), each built from that category's swings only,
/// labelled the way the site's tabs read them. Everything else in the
/// payload is pinned to the bytes it had before these rows changed.
/// </summary>
public class PayloadAttackTypeTests
{
    private const string Owner = "Aldwyn";
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    /// <summary>A twelve-second boss fight written as real log lines and
    /// run through the grammar, so the swing categories and damage-type
    /// markers the payload builder keys on are the ones the parser really
    /// produces. Invented names throughout.</summary>
    private static readonly (int At, string Message)[] BossFight =
    [
        (0, "YOU hit Lord Vexmoor for 400 slashing damage."),
        (0, "YOUR Taunting Blow hits Lord Vexmoor for a critical of 900 crushing damage."),
        (0, "YOUR Taunting Blow increases YOUR hate with Lord Vexmoor for 2,500 threat."),
        (1, "Lord Vexmoor hits YOU for 650 crushing damage."),
        (1, "Brenna's Sanguine Lash heals Aldwyn for 300 hit points."),
        (2, "Brenna's Sanguine Lash hits Lord Vexmoor for 700 disease damage."),
        (2, "YOU try to slash Lord Vexmoor, but miss."),
        (3, "YOUR Leeching Edge hits Lord Vexmoor for 1,200 piercing damage."),
        (3, "YOUR Leeching Edge heals YOU for a critical of 450 hit points."),
        (3, "YOUR Leeching Edge heals YOU for 150 hit points."),
        (4, "Brenna's Stoneguard absorbs 500 points of damage from being done to Aldwyn. (1500 points remaining)"),
        (4, "Brenna's Stoneguard absorbs 250 points of damage from being done to Aldwyn. (1250 points remaining)"),
        (4, "Lord Vexmoor tries to crush YOU, but YOU parry."),
        (5, "Brenna's Mending Veil heals Aldwyn for 800 hit points."),
        (5, "Brenna's Mending Veil absorbs 200 points of damage from being done to Aldwyn."),
        (5, "Lord Vexmoor's Rending Howl hits Brenna for 1,100 mental damage."),
        (6, "Brenna's Purify relieves Rending Howl from Aldwyn."),
        (6, "Brenna's Purify relieves Festering Bite from Aldwyn."),
        (6, "Brenna's Purify relieves Rending Howl from Brenna."),
        (6, "YOU multi attack Lord Vexmoor for 380 slashing damage."),
        (7, "Brenna's Quiet Step reduces THEIR hate with Lord Vexmoor for 1,800 threat."),
        (7, "Brenna's Clarity refreshes Aldwyn for 120 mana points."),
        (7, "Lord Vexmoor is stunned!"),
        (8, "YOU try to slash Lord Vexmoor with Feint, but miss."),
        (8, "YOUR Hollow Strike hits Lord Vexmoor but fails to inflict any damage."),
        (9, "Lord Vexmoor is no longer stunned."),
        (9, "YOU hit Lord Vexmoor for 420 slashing damage."),
        (10, "YOU hit a bone sentry for 310 slashing damage."),
        (10, "You have killed a bone sentry."),
        (11, "Brenna's Sanguine Lash hits Lord Vexmoor for a critical of 1,400 disease damage."),
        (12, "YOUR Taunting Blow hits Lord Vexmoor for 950 crushing damage."),
        (12, "YOUR Taunting Blow increases YOUR hate with Lord Vexmoor for 2,700 threat."),
        (12, "You have killed Lord Vexmoor."),
    ];

    private static Encounter Fight(params (int At, string Message)[] lines)
    {
        var engine = new ParserEngine("log-a", Owner);
        engine.ChangeZone("Vexmoor Keep");
        var processor = new LogLineProcessor(engine);
        foreach (var (at, message) in lines)
        {
            Assert.True(LogLine.TryParse($"({T0.ToUnixTimeSeconds() + at})[t] {message}", out var line), message);
            var matched = processor.LinesMatched;
            processor.Process(line);
            Assert.True(processor.LinesMatched == matched + 1, $"the grammar did not parse: {message}");
        }
        engine.EndCombat();
        return engine.History[^1];
    }

    private static LexiconPayload BossPayload() => PayloadBuilder.Build(Fight(BossFight), "Varsoon");

    private static PayloadAttackType Row(LexiconPayload payload, string attacker, string ability, SwingCategory category) =>
        Assert.Single(payload.AttackTypes, a => a.Attacker == attacker && a.Type == ability && a.SwingType == (int)category);

    private static List<PayloadAttackType> Rows(LexiconPayload payload, string attacker, string ability) =>
        [.. payload.AttackTypes.Where(a => a.Attacker == attacker && a.Type == ability)];

    /// <summary>One attack row as it goes on the wire.</summary>
    private static string Wire(PayloadAttackType row)
    {
        var payload = new LexiconPayload
        {
            LoggerName = Owner,
            LoggerServer = "Varsoon",
            Encounter = new PayloadEncounter { EncId = "00000000", Title = "t", StartTime = "s", EndTime = "e" },
            AttackTypes = [row],
        };
        using var doc = JsonDocument.Parse(payload.ToJson());
        return doc.RootElement.GetProperty("attack_types")[0].GetRawText();
    }

    // ── What must not move ──────────────────────────────────────────────────

    /// <summary>The wire bytes of everything the site ranks and compares
    /// across uploads — the identity fields, the encounter (encid, totals,
    /// duration, EncDPS), every combatant row and every damage_types row —
    /// one line each, in wire order. attack_types is the only key left out.</summary>
    private static string[] EverythingButAttackTypes(LexiconPayload payload)
    {
        using var doc = JsonDocument.Parse(payload.ToJson());
        var root = doc.RootElement;
        Assert.Equal(
            ["logger_name", "logger_server", "encounter", "combatants", "damage_types", "attack_types"],
            root.EnumerateObject().Select(p => p.Name));
        List<string> lines =
        [
            $"logger_name {root.GetProperty("logger_name").GetRawText()}",
            $"logger_server {root.GetProperty("logger_server").GetRawText()}",
            $"encounter {root.GetProperty("encounter").GetRawText()}",
        ];
        lines.AddRange(root.GetProperty("combatants").EnumerateArray().Select(c => $"combatant {c.GetRawText()}"));
        lines.AddRange(root.GetProperty("damage_types").EnumerateArray().Select(d => $"damage_type {d.GetRawText()}"));
        return [.. lines];
    }

    /// <summary>Captured from the builder BEFORE the attack rows were split
    /// per category (same fight, same code path) — not derived from the
    /// code under test. Site rankings compare these figures across uploads,
    /// so a diff here is a moved stat definition, never a test to update
    /// without that being the intent.</summary>
    private const string BeforeTheSplit = """
        logger_name "Aldwyn"
        logger_server "Varsoon"
        encounter {"encid":"b7e49221","title":"Lord Vexmoor","zone":"Vexmoor Keep","starttime":"2026-03-31T23:33:20Z","endtime":"2026-03-31T23:33:32Z","duration":12,"damage":6660,"encdps":555,"kills":2,"deaths":0,"success":1}
        combatant {"name":"Aldwyn","ally":"T","starttime":"2026-03-31T23:33:20Z","endtime":"2026-03-31T23:33:32Z","duration":12,"damage":4560,"kills":2,"healed":600,"critheals":1,"heals":2,"curedispels":0,"powerdrain":0,"powerreplenish":0,"dps":380,"encdps":380,"enchps":50,"hits":8,"crithits":1,"blocked":0,"misses":2,"swings":10,"healstaken":2650,"damagetaken":650,"deaths":0,"tohit":80}
        combatant {"name":"Brenna","ally":"T","starttime":"2026-03-31T23:33:21Z","endtime":"2026-03-31T23:33:31Z","duration":10,"damage":2100,"kills":0,"healed":2050,"critheals":0,"heals":5,"curedispels":3,"powerdrain":0,"powerreplenish":120,"dps":210,"encdps":175,"enchps":170.83,"hits":2,"crithits":1,"blocked":0,"misses":0,"swings":2,"healstaken":0,"damagetaken":1100,"deaths":0,"tohit":100}
        combatant {"name":"Lord Vexmoor","ally":"F","starttime":"2026-03-31T23:33:21Z","endtime":"2026-03-31T23:33:25Z","duration":4,"damage":1750,"kills":0,"healed":0,"critheals":0,"heals":0,"curedispels":0,"powerdrain":0,"powerreplenish":0,"dps":437.5,"encdps":145.83,"enchps":0,"hits":2,"crithits":0,"blocked":1,"misses":0,"swings":3,"healstaken":0,"damagetaken":6350,"deaths":1,"tohit":66.67}
        combatant {"name":"Unknown","ally":"F","starttime":"2026-03-31T23:33:27Z","endtime":"2026-03-31T23:33:29Z","duration":2,"damage":0,"kills":0,"healed":0,"critheals":0,"heals":0,"curedispels":0,"powerdrain":0,"powerreplenish":0,"dps":0,"encdps":0,"enchps":0,"hits":0,"crithits":0,"blocked":0,"misses":0,"swings":0,"healstaken":0,"damagetaken":0,"deaths":0,"tohit":0}
        combatant {"name":"a bone sentry","ally":"F","starttime":"2026-03-31T23:33:20Z","endtime":"2026-03-31T23:33:20Z","duration":0,"damage":0,"kills":0,"healed":0,"critheals":0,"heals":0,"curedispels":0,"powerdrain":0,"powerreplenish":0,"dps":0,"encdps":0,"enchps":0,"hits":0,"crithits":0,"blocked":0,"misses":0,"swings":0,"healstaken":0,"damagetaken":310,"deaths":1,"tohit":0}
        damage_type {"combatant":"Aldwyn","type":"Auto-Attack (Out)","starttime":"2026-03-31T23:33:20Z","endtime":"2026-03-31T23:33:32Z","damage":1510,"encdps":125.83,"dps":125.83,"critperc":0,"median":400,"minhit":310,"maxhit":420,"hits":4,"crithits":0,"blocked":0,"misses":1,"swings":5}
        damage_type {"combatant":"Aldwyn","type":"Outgoing Damage","starttime":"2026-03-31T23:33:20Z","endtime":"2026-03-31T23:33:32Z","damage":4560,"encdps":380,"dps":380,"critperc":12.5,"median":420,"minhit":0,"maxhit":1200,"hits":8,"crithits":1,"blocked":0,"misses":2,"swings":10}
        damage_type {"combatant":"Aldwyn","type":"Skill/Ability (Out)","starttime":"2026-03-31T23:33:20Z","endtime":"2026-03-31T23:33:32Z","damage":3050,"encdps":254.17,"dps":254.17,"critperc":25,"median":950,"minhit":0,"maxhit":1200,"hits":4,"crithits":1,"blocked":0,"misses":1,"swings":5}
        damage_type {"combatant":"Aldwyn","type":"Threat (Out)","starttime":"2026-03-31T23:33:20Z","endtime":"2026-03-31T23:33:32Z","damage":5200,"encdps":433.33,"dps":433.33,"critperc":0,"median":2700,"minhit":2500,"maxhit":2700,"hits":2,"crithits":0,"blocked":0,"misses":0,"swings":2}
        damage_type {"combatant":"Aldwyn","type":"Healed (Out)","starttime":"2026-03-31T23:33:23Z","endtime":"2026-03-31T23:33:23Z","damage":600,"encdps":50,"dps":600,"critperc":50,"median":450,"minhit":150,"maxhit":450,"hits":2,"crithits":1,"blocked":0,"misses":0,"swings":2}
        damage_type {"combatant":"Brenna","type":"Healed (Out)","starttime":"2026-03-31T23:33:21Z","endtime":"2026-03-31T23:33:25Z","damage":2050,"encdps":170.83,"dps":512.5,"critperc":0,"median":300,"minhit":200,"maxhit":800,"hits":5,"crithits":0,"blocked":0,"misses":0,"swings":5}
        damage_type {"combatant":"Brenna","type":"Skill/Ability (Out)","starttime":"2026-03-31T23:33:22Z","endtime":"2026-03-31T23:33:31Z","damage":2100,"encdps":175,"dps":233.33,"critperc":50,"median":1400,"minhit":700,"maxhit":1400,"hits":2,"crithits":1,"blocked":0,"misses":0,"swings":2}
        damage_type {"combatant":"Brenna","type":"Outgoing Damage","starttime":"2026-03-31T23:33:22Z","endtime":"2026-03-31T23:33:31Z","damage":2100,"encdps":175,"dps":233.33,"critperc":50,"median":1400,"minhit":700,"maxhit":1400,"hits":2,"crithits":1,"blocked":0,"misses":0,"swings":2}
        damage_type {"combatant":"Brenna","type":"Cure/Dispel (Out)","starttime":"2026-03-31T23:33:26Z","endtime":"2026-03-31T23:33:26Z","damage":0,"encdps":0,"dps":0,"critperc":0,"median":0,"minhit":0,"maxhit":0,"hits":3,"crithits":0,"blocked":0,"misses":0,"swings":3}
        damage_type {"combatant":"Brenna","type":"Threat (Out)","starttime":"2026-03-31T23:33:27Z","endtime":"2026-03-31T23:33:27Z","damage":1800,"encdps":150,"dps":1800,"critperc":0,"median":1800,"minhit":1800,"maxhit":1800,"hits":1,"crithits":0,"blocked":0,"misses":0,"swings":1}
        damage_type {"combatant":"Brenna","type":"Power Replenish (Out)","starttime":"2026-03-31T23:33:27Z","endtime":"2026-03-31T23:33:27Z","damage":120,"encdps":10,"dps":120,"critperc":0,"median":120,"minhit":120,"maxhit":120,"hits":1,"crithits":0,"blocked":0,"misses":0,"swings":1}
        damage_type {"combatant":"Lord Vexmoor","type":"Auto-Attack (Out)","starttime":"2026-03-31T23:33:21Z","endtime":"2026-03-31T23:33:24Z","damage":650,"encdps":54.17,"dps":216.67,"critperc":0,"median":650,"minhit":650,"maxhit":650,"hits":1,"crithits":0,"blocked":1,"misses":0,"swings":2}
        damage_type {"combatant":"Lord Vexmoor","type":"Outgoing Damage","starttime":"2026-03-31T23:33:21Z","endtime":"2026-03-31T23:33:25Z","damage":1750,"encdps":145.83,"dps":437.5,"critperc":0,"median":1100,"minhit":650,"maxhit":1100,"hits":2,"crithits":0,"blocked":1,"misses":0,"swings":3}
        damage_type {"combatant":"Lord Vexmoor","type":"Skill/Ability (Out)","starttime":"2026-03-31T23:33:25Z","endtime":"2026-03-31T23:33:25Z","damage":1100,"encdps":91.67,"dps":1100,"critperc":0,"median":1100,"minhit":1100,"maxhit":1100,"hits":1,"crithits":0,"blocked":0,"misses":0,"swings":1}
        """;

    [Fact]
    public void Everything_But_Attack_Types_Is_Byte_For_Byte_What_It_Was()
    {
        var expected = BeforeTheSplit.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(expected, EverythingButAttackTypes(BossPayload()));
    }

    [Fact]
    public void EncId_Is_Unchanged_So_Re_Uploads_Still_Dedupe()
    {
        // The content hash the site dedupes on (UNIQUE(world, act_encid)):
        // the value this fight had before the attack rows changed.
        var fight = Fight(BossFight);
        Assert.Equal("b7e49221", PayloadBuilder.ComputeEncId(fight));
        Assert.Equal("b7e49221", PayloadBuilder.Build(fight, "Varsoon").Encounter.EncId);
    }

    /// <summary>Every outgoing swing of the fight, straight off the
    /// encounter — the raw material the figures below are recomputed from
    /// without going through a bucket's or the builder's arithmetic.</summary>
    private static List<Swing> SwingsBy(Encounter fight, string name) =>
        [.. fight.Combatants[name.ToUpperInvariant()].OutgoingBuckets[BucketConfig.AllOutgoingRef].All.Swings];

    private static readonly Dictionary<string, SwingCategory[]> BucketCategories = new()
    {
        [BucketConfig.AutoAttackOut] = [SwingCategory.Melee],
        [BucketConfig.SkillOut] = [SwingCategory.NonMelee],
        [BucketConfig.OutgoingDamage] = [SwingCategory.Melee, SwingCategory.NonMelee],
        [BucketConfig.HealedOut] = [SwingCategory.Healing],
        [BucketConfig.ThreatOut] = [SwingCategory.Threat],
        [BucketConfig.CureOut] = [SwingCategory.CureDispel],
        [BucketConfig.PowerReplenishOut] = [SwingCategory.PowerReplenish],
    };

    [Fact]
    public void Totals_And_Damage_Types_Match_Figures_Recomputed_From_The_Swings()
    {
        var fight = Fight(BossFight);
        var payload = PayloadBuilder.Build(fight, "Varsoon");
        const double seconds = 12;

        long allyDamage = 0;
        foreach (var combatant in payload.Combatants.Where(c => c.Name is "Aldwyn" or "Brenna" or "Lord Vexmoor"))
        {
            var swings = SwingsBy(fight, combatant.Name);
            var hostile = swings.Where(s => s.Category is SwingCategory.Melee or SwingCategory.NonMelee).ToList();
            var damage = hostile.Where(s => s.Damage.Number > 0).Sum(s => s.Damage.Number);
            var healed = swings.Where(s => s.Category == SwingCategory.Healing && s.Damage.Number > 0).Sum(s => s.Damage.Number);

            Assert.Equal(damage, combatant.Damage);
            Assert.Equal(healed, combatant.Healed);
            Assert.Equal(Math.Round(damage / seconds, 2), combatant.EncDps);
            Assert.Equal(Math.Round(healed / seconds, 2), combatant.EncHps);
            Assert.Equal(hostile.Count(s => s.Damage.Number >= 0), combatant.Hits);
            Assert.Equal(hostile.Count(s => s.Damage.Number != DamageValue.DeathNumber), combatant.Swings);
            Assert.Equal(swings.Count(s => s.Category == SwingCategory.CureDispel), combatant.CureDispels);
            if (combatant.Ally == "T")
                allyDamage += damage;

            foreach (var row in payload.DamageTypes.Where(d => d.Combatant == combatant.Name))
            {
                var mine = swings.Where(s => BucketCategories[row.Type].Contains(s.Category)).ToList();
                var landed = mine.Where(s => s.Damage.Number >= 0).Select(s => s.Damage.Number).Order().ToList();
                Assert.Equal(mine.Where(s => s.Damage.Number > 0).Sum(s => s.Damage.Number), row.Damage);
                Assert.Equal(landed.Count, row.Hits);
                Assert.Equal(landed.Count == 0 ? 0 : landed[landed.Count / 2], row.Median);
                Assert.Equal(landed.Count == 0 ? 0 : landed[0], row.MinHit);
                Assert.Equal(mine.Max(s => Math.Max(0, s.Damage.Number)), row.MaxHit);
                Assert.Equal(mine.Count(s => s.Critical && s.Damage.Number >= 0), row.CritHits);
                Assert.Equal(mine.Count(s => s.Damage.Number == DamageValue.MissNumber), row.Misses);
                Assert.Equal(mine.Count(s => s.Damage.Number is < DamageValue.MissNumber and > DamageValue.DeathNumber), row.Blocked);
                Assert.Equal(mine.Count(s => s.Damage.Number != DamageValue.DeathNumber), row.Swings);
                Assert.Equal(Math.Round(row.Damage / seconds, 2), row.EncDps);
            }
        }

        // One damage_types row per bucket the combatant filled — none
        // added, none lost — and the encounter totals are the allies' sum.
        Assert.Equal(14, payload.DamageTypes.Count);
        Assert.Equal(allyDamage, payload.Encounter.Damage);
        Assert.Equal(allyDamage / seconds, payload.Encounter.EncDps);
        Assert.Equal(12, payload.Encounter.Duration);
    }

    // ── 1. One row per (ability, category) ──────────────────────────────────

    [Fact]
    public void An_Ability_That_Damages_Then_Heals_Uploads_A_Row_For_Each()
    {
        // The bug: one row per NAME, filed under the category of the first
        // swing and summed over all of them — this lifetap showed 1,800
        // damage on the site's Damage tab (1,200 damage + 600 healing) and
        // nothing on the Healed tab.
        var payload = BossPayload();
        Assert.Equal(2, Rows(payload, "Aldwyn", "Leeching Edge").Count);

        var damage = Row(payload, "Aldwyn", "Leeching Edge", SwingCategory.NonMelee);
        Assert.Equal(1200, damage.Damage);
        Assert.Equal((1, 0, 1), (damage.Hits, damage.CritHits, damage.Swings));
        Assert.Equal((1200, 1200, 1200), (damage.MinHit, damage.MaxHit, damage.Median));
        Assert.Equal(0, damage.CritPerc);
        Assert.Equal(100, damage.EncDps);

        var heal = Row(payload, "Aldwyn", "Leeching Edge", SwingCategory.Healing);
        Assert.Equal(600, heal.Damage);
        Assert.Equal((2, 1, 2), (heal.Hits, heal.CritHits, heal.Swings));
        Assert.Equal((150, 450, 450), (heal.MinHit, heal.MaxHit, heal.Median));
        Assert.Equal(50, heal.CritPerc);
        Assert.Equal(50, heal.EncDps);
    }

    [Fact]
    public void An_Ability_That_Heals_Then_Damages_Keeps_Its_Damage_Row()
    {
        // The other half of the bug: first swing a heal, so the whole
        // ability went to the Healed tab (2,400 "healed") and its 2,100
        // damage vanished from the Damage tab.
        var payload = BossPayload();
        Assert.Equal(2, Rows(payload, "Brenna", "Sanguine Lash").Count);

        var heal = Row(payload, "Brenna", "Sanguine Lash", SwingCategory.Healing);
        Assert.Equal(300, heal.Damage);
        Assert.Equal((1, 0, 1), (heal.Hits, heal.CritHits, heal.Swings));

        var damage = Row(payload, "Brenna", "Sanguine Lash", SwingCategory.NonMelee);
        Assert.Equal(2100, damage.Damage);
        Assert.Equal((2, 1, 2), (damage.Hits, damage.CritHits, damage.Swings));
        Assert.Equal((700, 1400, 1400), (damage.MinHit, damage.MaxHit, damage.Median));
        Assert.Equal(50, damage.CritPerc);
        // Its own window: first to last DAMAGE swing (2 s → 11 s).
        Assert.Equal(233.33, damage.Dps);
    }

    [Fact]
    public void An_Ability_And_Its_Threat_Component_Are_Separate_Rows()
    {
        var payload = BossPayload();
        Assert.Equal(2, Rows(payload, "Aldwyn", "Taunting Blow").Count);

        var damage = Row(payload, "Aldwyn", "Taunting Blow", SwingCategory.NonMelee);
        Assert.Equal(1850, damage.Damage); // was 7,050: its damage plus 5,200 threat
        Assert.Equal((2, 1, 2), (damage.Hits, damage.CritHits, damage.Swings));
        Assert.Equal((900, 950, 950), (damage.MinHit, damage.MaxHit, damage.Median));

        // Threat keeps the parser's swingtype — ACT's own 16; the site
        // reads it beside the ACT plugin's 100.
        var threat = Row(payload, "Aldwyn", "Taunting Blow", SwingCategory.Threat);
        Assert.Equal(16, threat.SwingType);
        Assert.Equal(5200, threat.Damage);
        Assert.Equal((2, 2700), (threat.Hits, threat.MaxHit));
        Assert.Equal("Increase", threat.Resist);

        var reducer = Row(payload, "Brenna", "Quiet Step", SwingCategory.Threat);
        Assert.Equal(16, reducer.SwingType);
        Assert.Equal(1800, reducer.Damage);
        Assert.Equal("Decrease", reducer.Resist);
    }

    [Fact]
    public void A_Name_Logged_As_Both_Auto_Attack_And_Skill_Is_A_Row_For_Each()
    {
        // The grammar files a multi attack / flurry as melee (1) even under
        // an ability name, and that name's plain hits as skill damage (2).
        // Both rows land on the site's Damage tab (it selects 1 and 2), as
        // the Auto-Attack (Out) / Skill (Out) split has them; between them
        // they carry the combatant's damage exactly once.
        var payload = PayloadBuilder.Build(
            Fight(
                (0, "YOU hit Lord Vexmoor for 400 slashing damage."),
                (1, "Brenna's Sanguine Lash heals Aldwyn for 300 hit points."),
                (1, "Brenna's spectral hammer hits Lord Vexmoor for 100 crushing damage."),
                (2, "Brenna's spectral hammer multi attacks Lord Vexmoor for a critical of 250 crushing damage."),
                (3, "You have killed Lord Vexmoor.")),
            "Varsoon");

        var skill = Row(payload, "Brenna", "spectral hammer", SwingCategory.NonMelee);
        var melee = Row(payload, "Brenna", "spectral hammer", SwingCategory.Melee);
        Assert.Equal((100L, 1, 0), (skill.Damage, skill.Hits, skill.CritHits));
        Assert.Equal((250L, 1, 1), (melee.Damage, melee.Hits, melee.CritHits));
        Assert.Equal(350, payload.Combatants.Single(c => c.Name == "Brenna").Damage);
    }

    [Fact]
    public void Each_Tab_Adds_Up_To_The_Combatant_Total_It_Is_Shown_Beside()
    {
        // With rows split by category, the rows a site tab selects sum to
        // exactly the figure of the bucket behind that tab.
        var payload = BossPayload();
        foreach (var combatant in payload.Combatants)
        {
            long Sum(params SwingCategory[] categories) => payload.AttackTypes
                .Where(a => a.Attacker == combatant.Name && categories.Contains((SwingCategory)a.SwingType))
                .Sum(a => a.Damage);
            long BucketDamage(string name) =>
                payload.DamageTypes.SingleOrDefault(d => d.Combatant == combatant.Name && d.Type == name)?.Damage ?? 0;

            Assert.Equal(combatant.Damage, Sum(SwingCategory.Melee, SwingCategory.NonMelee));
            Assert.Equal(combatant.Healed, Sum(SwingCategory.Healing));
            Assert.Equal(BucketDamage(BucketConfig.ThreatOut), Sum(SwingCategory.Threat));
            Assert.Equal(combatant.PowerReplenish, Sum(SwingCategory.PowerReplenish));
            Assert.Equal(combatant.CureDispels, Sum(SwingCategory.CureDispel));
        }
    }

    [Fact]
    public void Rows_Are_Unique_On_The_Key_The_Site_Enforces()
    {
        // (combatant, swing_type, attack_name): the site's unique index and
        // the duplicate check ahead of it. Two rows that share attacker and
        // ability but differ in swingtype are two keys, not a conflict.
        var payload = BossPayload();
        var keys = payload.AttackTypes.Select(a => (a.Attacker, a.SwingType, a.Type)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains(("Aldwyn", 2, "Taunting Blow"), keys);
        Assert.Contains(("Aldwyn", 16, "Taunting Blow"), keys);
    }

    /// <summary>attack_types rows of the boss fight exactly as the builder
    /// sent them BEFORE the split, for the abilities that only ever logged
    /// in one category. Captured from that build, like
    /// <see cref="BeforeTheSplit"/>.</summary>
    public static TheoryData<string, string, int, string> UnsplitRowsBefore => new()
    {
        { "Aldwyn", "Auto-Attack", 1, """{"attacker":"Aldwyn","type":"Auto-Attack","swingtype":1,"damage":1510,"encdps":125.83,"dps":151,"critperc":0,"median":400,"minhit":310,"maxhit":420,"hits":4,"crithits":0,"blocked":0,"misses":1,"swings":5}""" },
        { "Aldwyn", "Feint", 2, """{"attacker":"Aldwyn","type":"Feint","swingtype":2,"damage":0,"encdps":0,"dps":0,"critperc":0,"median":0,"minhit":0,"maxhit":0,"hits":0,"crithits":0,"blocked":0,"misses":1,"swings":1}""" },
        { "Aldwyn", "Hollow Strike", 2, """{"attacker":"Aldwyn","type":"Hollow Strike","swingtype":2,"damage":0,"encdps":0,"dps":0,"critperc":0,"median":0,"minhit":0,"maxhit":0,"hits":1,"crithits":0,"blocked":0,"misses":0,"swings":1}""" },
        { "Brenna", "Stoneguard", 3, """{"attacker":"Brenna","type":"Stoneguard","swingtype":3,"damage":750,"encdps":62.5,"dps":750,"critperc":0,"median":500,"minhit":250,"maxhit":500,"hits":2,"crithits":0,"blocked":0,"misses":0,"swings":2}""" },
        { "Brenna", "Mending Veil", 3, """{"attacker":"Brenna","type":"Mending Veil","swingtype":3,"damage":1000,"encdps":83.33,"dps":1000,"critperc":0,"median":800,"minhit":200,"maxhit":800,"hits":2,"crithits":0,"blocked":0,"misses":0,"swings":2}""" },
        { "Brenna", "Quiet Step", 16, """{"attacker":"Brenna","type":"Quiet Step","swingtype":16,"damage":1800,"encdps":150,"dps":1800,"critperc":0,"median":1800,"minhit":1800,"maxhit":1800,"hits":1,"crithits":0,"blocked":0,"misses":0,"swings":1}""" },
        { "Brenna", "Clarity", 13, """{"attacker":"Brenna","type":"Clarity","swingtype":13,"damage":120,"encdps":10,"dps":120,"critperc":0,"median":120,"minhit":120,"maxhit":120,"hits":1,"crithits":0,"blocked":0,"misses":0,"swings":1}""" },
        { "Lord Vexmoor", "Auto-Attack", 1, """{"attacker":"Lord Vexmoor","type":"Auto-Attack","swingtype":1,"damage":650,"encdps":54.17,"dps":216.67,"critperc":0,"median":650,"minhit":650,"maxhit":650,"hits":1,"crithits":0,"blocked":1,"misses":0,"swings":2}""" },
        { "Lord Vexmoor", "Rending Howl", 2, """{"attacker":"Lord Vexmoor","type":"Rending Howl","swingtype":2,"damage":1100,"encdps":91.67,"dps":1100,"critperc":0,"median":1100,"minhit":1100,"maxhit":1100,"hits":1,"crithits":0,"blocked":0,"misses":0,"swings":1}""" },
    };

    [Theory]
    [MemberData(nameof(UnsplitRowsBefore))]
    public void A_Single_Category_Ability_Keeps_Every_Figure_It_Had(string attacker, string ability, int swingType, string before)
    {
        // Median, crit percentage, hit, miss and blocked counting are the
        // combat model's own: for an ability that never needed splitting,
        // the row is the old row plus the new resist key and nothing else.
        var row = Row(BossPayload(), attacker, ability, (SwingCategory)swingType);
        Assert.Equal(before, Wire(row with { Resist = null }));
    }

    // ── 2. resist ───────────────────────────────────────────────────────────

    [Fact]
    public void Heal_Rows_Carry_Hitpoints_And_Ward_Rows_Absorption()
    {
        var payload = BossPayload();
        Assert.Equal("Hitpoints", Row(payload, "Aldwyn", "Leeching Edge", SwingCategory.Healing).Resist);
        Assert.Equal("Hitpoints", Row(payload, "Brenna", "Sanguine Lash", SwingCategory.Healing).Resist);

        // The value the site's Healed tab tests to mark a ward.
        var ward = Row(payload, "Brenna", "Stoneguard", SwingCategory.Healing);
        Assert.Equal(3, ward.SwingType);
        Assert.Equal(750, ward.Damage);
        Assert.Equal("Absorption", ward.Resist);
        Assert.Contains("\"resist\":\"Absorption\"", Wire(ward));
    }

    [Fact]
    public void A_Heal_Ability_That_Also_Warded_Claims_Neither_Type()
    {
        // One name, one category (both are heals), so one row — the site's
        // key leaves no room for two. Its 1,000 is 800 healed + 200
        // absorbed; "Hitpoints" would be wrong for the ward part and
        // "Absorption" would paint the whole row as a ward, so the key is
        // left out and the site shows a dash.
        var row = Assert.Single(Rows(BossPayload(), "Brenna", "Mending Veil"));
        Assert.Equal(1000, row.Damage);
        Assert.Null(row.Resist);
        Assert.DoesNotContain("resist", Wire(row));
    }

    [Fact]
    public void Power_Replenish_Is_Still_Uploaded_And_Carries_Power()
    {
        var row = Row(BossPayload(), "Brenna", "Clarity", SwingCategory.PowerReplenish);
        Assert.Equal(13, row.SwingType);
        Assert.Equal(120, row.Damage);
        Assert.Equal("Power", row.Resist);
    }

    [Fact]
    public void Damage_Rows_Carry_Their_Damage_Type_Only_When_It_Is_The_Only_One()
    {
        var payload = BossPayload();
        // Misses and the multi attack do not blur it: every LANDED hit of
        // the auto-attack was slashing.
        Assert.Equal("slashing", Row(payload, "Aldwyn", "Auto-Attack", SwingCategory.Melee).Resist);
        Assert.Equal("crushing", Row(payload, "Aldwyn", "Taunting Blow", SwingCategory.NonMelee).Resist);
        Assert.Equal("disease", Row(payload, "Brenna", "Sanguine Lash", SwingCategory.NonMelee).Resist);
        // Nothing landed, so nothing to name: the avoid and no-damage
        // markers the parser files those swings under are not damage types.
        Assert.Null(Row(payload, "Aldwyn", "Feint", SwingCategory.NonMelee).Resist);
        Assert.Null(Row(payload, "Aldwyn", "Hollow Strike", SwingCategory.NonMelee).Resist);

        var mixed = PayloadBuilder.Build(
            Fight(
                (0, "YOU hit Lord Vexmoor for 400 slashing damage."),
                (1, "YOU hit Lord Vexmoor for 300 piercing damage."),
                (2, "You have killed Lord Vexmoor.")),
            "Varsoon");
        var dualWield = Row(mixed, "Aldwyn", "Auto-Attack", SwingCategory.Melee);
        Assert.Equal(700, dualWield.Damage);
        Assert.Null(dualWield.Resist);
    }

    // ── 3. Cures ────────────────────────────────────────────────────────────

    [Fact]
    public void Cure_Rows_Count_One_Effect_Removed_Per_Line()
    {
        // The site's Cures tab reads damage as effects removed, hits as
        // times cast and maxhit as the most at once; ACT books each cure
        // line as an amount of 1. The parser's cure swing carries 0, so the
        // row uploaded as all zeroes.
        var fight = Fight(BossFight);
        var payload = PayloadBuilder.Build(fight, "Varsoon");

        var cure = Assert.Single(Rows(payload, "Brenna", "Purify"));
        Assert.Equal(20, cure.SwingType);
        Assert.Equal(3, cure.Damage);
        Assert.Equal((3, 3), (cure.Hits, cure.Swings));
        Assert.Equal((1, 1, 1), (cure.MinHit, cure.MaxHit, cure.Median));
        Assert.Equal((0, 0, 0), (cure.CritHits, cure.Misses, cure.Blocked));

        // Payload only: the engine's cure swings and the combatant column
        // are what they were.
        var swings = SwingsBy(fight, "Brenna").Where(s => s.Category == SwingCategory.CureDispel).ToList();
        Assert.Equal(3, swings.Count);
        Assert.All(swings, s => Assert.Equal(DamageValue.NoDamage, s.Damage));
        Assert.Equal(3, payload.Combatants.Single(c => c.Name == "Brenna").CureDispels);
    }

    // ── 4. Category 30 ──────────────────────────────────────────────────────

    [Fact]
    public void Status_Effect_Rows_Are_Not_Uploaded_But_Stay_In_The_Encounter()
    {
        var fight = Fight(BossFight);
        var payload = PayloadBuilder.Build(fight, "Varsoon");

        Assert.DoesNotContain(payload.AttackTypes, a => a.SwingType == (int)SwingCategory.StatusEffect);
        Assert.DoesNotContain(payload.AttackTypes, a => a.Type == "stunned");

        // Local history and ability mining read them from the encounter.
        var kept = SwingsBy(fight, Combatant.UnknownName).Where(s => s.Category == SwingCategory.StatusEffect).ToList();
        Assert.Equal(["applied", "released"], kept.Select(s => s.DamageType));
    }

    [Fact]
    public void A_Status_Effect_Sharing_An_Ability_Name_Does_Not_Leak_Into_Its_Row()
    {
        // Rows are split by category first, so a status swing filed under
        // the same name as a real ability is left out without touching the
        // ability's figures.
        var engine = new ParserEngine("log-a", Owner);
        Assert.True(engine.SetEncounter(T0, Owner, "Lord Vexmoor"));
        engine.AddSwing(SwingCategory.NonMelee, false, "None", Owner, "silenced", 500, T0, "Lord Vexmoor", "mental");
        engine.AddSwing(SwingCategory.StatusEffect, false, "None", Owner, "silenced", DamageValue.NoDamage, T0.AddSeconds(1), "Lord Vexmoor", "applied");
        engine.AddSwing(SwingCategory.Melee, false, "None", Owner, Combatant.KillingAbility, DamageValue.Death, T0.AddSeconds(2), "Lord Vexmoor", "death");
        engine.EndCombat();

        var row = Assert.Single(PayloadBuilder.Build(engine.History[^1], "Varsoon").AttackTypes);
        Assert.Equal(("silenced", 2, 500L, 1, 1), (row.Type, row.SwingType, row.Damage, row.Hits, row.Swings));
    }

    // ── 5. Zero-activity rows ───────────────────────────────────────────────

    [Fact]
    public void The_Killing_Row_Is_Dropped_And_Kills_Stay_On_The_Combatant()
    {
        // The ACT plugin's rule: no damage, no hits, no misses, nothing
        // blocked → no row. "Killing" (swingtype 1, all zeroes) sat on the
        // site's Damage tab; kills and deaths are combatant columns there.
        var payload = BossPayload();
        Assert.DoesNotContain(payload.AttackTypes, a => a.Type == Combatant.KillingAbility);
        Assert.Equal(2, payload.Combatants.Single(c => c.Name == Owner).Kills);
        Assert.Equal(1, payload.Combatants.Single(c => c.Name == "Lord Vexmoor").Deaths);
        Assert.Equal(2, payload.Encounter.Kills);
    }

    [Fact]
    public void Rows_With_Any_Activity_Are_Kept_Even_At_Zero_Damage()
    {
        var payload = BossPayload();
        // Only ever missed.
        var missed = Row(payload, Owner, "Feint", SwingCategory.NonMelee);
        Assert.Equal((0L, 0, 1, 0), (missed.Damage, missed.Hits, missed.Misses, missed.Blocked));
        // Landed for nothing.
        var hollow = Row(payload, Owner, "Hollow Strike", SwingCategory.NonMelee);
        Assert.Equal((0L, 1, 0, 0), (hollow.Damage, hollow.Hits, hollow.Misses, hollow.Blocked));

        // Only ever avoided (the site's blocked column).
        var parried = PayloadBuilder.Build(
            Fight(
                (0, "YOU hit Lord Vexmoor for 400 slashing damage."),
                (1, "YOU try to slash Lord Vexmoor with Feint, but Lord Vexmoor parries."),
                (2, "You have killed Lord Vexmoor.")),
            "Varsoon");
        var avoided = Row(parried, Owner, "Feint", SwingCategory.NonMelee);
        Assert.Equal((0L, 0, 0, 1), (avoided.Damage, avoided.Hits, avoided.Misses, avoided.Blocked));
    }
}
