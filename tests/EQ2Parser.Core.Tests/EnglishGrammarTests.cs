using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Grammar;

namespace EQ2Parser.Core.Tests;

/// <summary>Every literal here is a real line from a Varsoon TLE log.</summary>
public class EnglishGrammarTests
{
    private static SwingEvent Swing(string message)
    {
        var parsed = EnglishGrammar.TryParse(message);
        return Assert.IsType<SwingEvent>(parsed);
    }

    [Fact]
    public void Your_Ability_Crit()
    {
        var s = Swing("YOUR Divine Strike hits a bog slug for a critical of 15,032 divine damage.");
        Assert.Equal(("YOU", "Divine Strike", "a bog slug"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(15032, s.Damage.Number);
        Assert.True(s.Critical);
        Assert.Equal(SwingCategory.NonMelee, s.Category);
        Assert.Equal("divine", s.DamageType);
    }

    [Fact]
    public void Abbreviated_Amounts_Expand()
    {
        // EQ2 logs six-digit-plus amounts K/M-abbreviated — these lines were
        // silently DROPPED before (the Assassinate regression: two 100k+
        // crits missing from the Malkonis fight while ACT counted them).
        var s = Swing("Asame's Assassinate hits Malkonis D'Morte for a critical of 140.6K disease damage.");
        Assert.Equal(("Asame", "Assassinate", "Malkonis D'Morte"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(140_600, s.Damage.Number);
        Assert.True(s.Critical);

        var heal = Swing("Sofja's Grim Sorcery heals Menludiir for a critical of 1.2M hit points.");
        Assert.Equal(1_200_000, heal.Damage.Number);

        var whole = Swing("Asame's Assassinate hits Malkonis D'Morte for 250K disease damage.");
        Assert.Equal(250_000, whole.Damage.Number);
    }

    [Fact]
    public void Suffixless_Decimal_Amount_Parses_Instead_Of_Throwing()
    {
        // The amount pattern admits "1234.6" (decimal, no K/M/B). A plain
        // long.Parse threw FormatException here — and at the time any
        // parse exception permanently killed that log's tail loop.
        var s = Swing("Asame's Assassinate hits Malkonis D'Morte for 1,234.6 disease damage.");
        Assert.Equal(1_235, s.Damage.Number);
    }

    [Fact]
    public void Anonymous_Hit_On_Possessively_Named_Victim_Is_Not_A_Phantom_Row()
    {
        // "Bob's pet is hit by …" used to phantom-parse via the possessive
        // pattern as attacker "Bob", ability "pet is", victim "by Acid Spray".
        var s = Swing("Bob's pet is hit by Acid Spray for 447 poison damage.");
        Assert.Equal(("Unknown", "Acid Spray", "Bob's pet"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(447, s.Damage.Number);
    }

    [Fact]
    public void FirstPerson_Counter_Is_Parsed()
    {
        // Third-person avoids include counters; the first-person pattern
        // omitted them, so YOUR countered attacks went unparsed.
        var s = Swing("YOU try to slash Malkonis D'Morte, but Malkonis D'Morte counters.");
        Assert.Equal(("YOU", "Malkonis D'Morte"), (s.Attacker, s.Victim));
        Assert.True(s.Damage.Number < 0);
    }

    [Fact]
    public void You_AutoAttack_Is_Melee()
    {
        var s = Swing("YOU hit a lesser shadowbone skeleton for a critical of 5,529 crushing damage.");
        Assert.Equal(("YOU", EnglishGrammar.AutoAttackAbility), (s.Attacker, s.Ability));
        Assert.Equal(SwingCategory.Melee, s.Category);
        Assert.Equal(5529, s.Damage.Number);
    }

    [Fact]
    public void ThirdPerson_Plain_AutoAttack()
    {
        var s = Swing("Guard Moor hits a bog slug hatchling for 368 slashing damage.");
        Assert.Equal(("Guard Moor", EnglishGrammar.AutoAttackAbility, "a bog slug hatchling"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(SwingCategory.Melee, s.Category);
        Assert.False(s.Critical);
    }

    [Fact]
    public void ThirdPerson_Possessive_Ability()
    {
        var s = Swing("Badbang's Magic Feedback hits Delhagin the Frightful for 14 magic damage.");
        Assert.Equal(("Badbang", "Magic Feedback", "Delhagin the Frightful"), (s.Attacker, s.Ability, s.Victim));
        Assert.Equal(SwingCategory.NonMelee, s.Category);
    }

    [Fact]
    public void MultiAttack_Verb_Becomes_Special()
    {
        var s = Swing("Menludiir's unswerving hammer multi attacks a krait patriarch for a critical of 2,378 crushing damage.");
        Assert.Equal("Menludiir", s.Attacker);
        Assert.Equal("unswerving hammer", s.Ability);
        Assert.Equal("Multi Attack", s.Special);
        // The verb is the swing's Special, not its category: this is the
        // hammer's attack, a named thing, so it is a skill of its owner.
        Assert.Equal(SwingCategory.NonMelee, s.Category);
        Assert.True(s.Critical);
    }

    // A pet, a dumbfire or a spell logged as "Owner's <name>" makes plain
    // hits AND multi attacks / flurries. Both are that named thing's, so
    // both are skills: the melee verb must not move half of a pet's damage
    // into its owner's auto-attack bucket (it did, and Auto-Attack (Out)
    // then read higher than the Auto-Attack row beside it, and than ACT).
    [Theory]
    [InlineData("Tester's thug hits a gnoll for 300 slashing damage.", "None")]
    [InlineData("Tester's thug multi attacks a gnoll for a critical of 450 slashing damage.", "Multi Attack")]
    [InlineData("Tester's thug flurries a gnoll for 200 piercing damage.", "Flurry")]
    [InlineData("Tester's Whirling Blades aoe attacks a gnoll for 120 slashing damage.", "AoE Attack")]
    [InlineData("Tester's Fiery Annihilation multi attacks a gnoll for 9,000 heat damage.", "Multi Attack")]
    [InlineData("YOUR thug multi attacks a gnoll for 450 slashing damage.", "Multi Attack")]
    [InlineData("YOUR thug double attacks a gnoll for 450 slashing damage.", "Multi Attack")]
    public void A_Named_Things_Swings_Are_Skills_Whatever_The_Verb(string line, string special)
    {
        var s = Swing(line);
        Assert.Equal(SwingCategory.NonMelee, s.Category);
        Assert.Equal(special, s.Special);
        Assert.NotEqual(EnglishGrammar.AutoAttackAbility, s.Ability);
    }

    // The combatant's OWN swings are auto-attacks whatever the verb.
    [Theory]
    [InlineData("YOU multi attack a gnoll for 120 crushing damage.", "Multi Attack")]
    [InlineData("YOU flurry a gnoll for 90 crushing damage.", "Flurry")]
    [InlineData("YOU aoe attack a gnoll for 80 crushing damage.", "AoE Attack")]
    [InlineData("Tester multi attacks a gnoll for 120 crushing damage.", "Multi Attack")]
    [InlineData("Tester flurries a gnoll for 90 crushing damage.", "Flurry")]
    public void A_Combatants_Own_Swings_Are_Auto_Attacks_Whatever_The_Verb(string line, string special)
    {
        var s = Swing(line);
        Assert.Equal(SwingCategory.Melee, s.Category);
        Assert.Equal(special, s.Special);
        Assert.Equal(EnglishGrammar.AutoAttackAbility, s.Ability);
    }

    // Second-person autoattack verbs are conjugated WITHOUT the s ("YOU multi
    // attack", "YOU flurry"). These went unmatched until 2026-08, silently
    // dropping every player's own multi/flurry/AoE hits from their own parse
    // (guildmates' logs showed them fine — third person parses). Real lines
    // from eq2log_Bridgerton Zylphax 2026-08-23.
    [Theory]
    [InlineData("YOU multi attack Zylphax the Shredder for a critical of 1,423 piercing damage.", "Multi Attack", 1423, true)]
    [InlineData("YOU multi attack Zylphax the Shredder for 882 piercing damage.", "Multi Attack", 882, false)]
    [InlineData("YOU flurry Zylphax the Shredder for 1,102 slashing damage.", "Flurry", 1102, false)]
    [InlineData("YOU aoe attack a training dummy for 501 crushing damage.", "AoE Attack", 501, false)]
    public void SecondPerson_Multi_Verbs_Parse_As_AutoAttack(string line, string special, long damage, bool crit)
    {
        var s = Swing(line);
        Assert.Equal(("YOU", EnglishGrammar.AutoAttackAbility), (s.Attacker, s.Ability));
        Assert.Equal(special, s.Special);
        Assert.Equal(SwingCategory.Melee, s.Category);
        Assert.Equal(damage, s.Damage.Number);
        Assert.Equal(crit, s.Critical);
    }

    [Theory]
    [InlineData("a krait patriarch tries to pierce YOU, but misses.", DamageValue.MissNumber)]
    [InlineData("a krait patriarch tries to crush Menludiir, but Menludiir parries.", DamageValue.ParryNumber)]
    [InlineData("a Rime advancer tries to slash YOU, but YOU riposte.", DamageValue.RiposteNumber)]
    [InlineData("a Rime advancer tries to crush YOU, but YOU block.", DamageValue.BlockNumber)]
    [InlineData("a Thurgadin esper tries to smash YOU, but YOU resist.", DamageValue.ResistNumber)]
    public void Avoid_Outcomes_Map_To_Sentinel_Codes(string line, long expected)
    {
        var s = Swing(line);
        Assert.Equal(expected, s.Damage.Number);
        Assert.NotEqual("", s.Victim);
        Assert.Equal(EnglishGrammar.AutoAttackAbility, s.Ability);
    }

    [Fact]
    public void Weapon_Can_Be_The_Avoiding_Actor()
    {
        // Real line: the victim's weapon parries; the victim is still Menludiir.
        var s = Swing("a krait patriarch tries to crush Menludiir, but Menludiir's unswerving hammer parries.");
        Assert.Equal("a krait patriarch", s.Attacker);
        Assert.Equal("Menludiir", s.Victim);
        Assert.Equal(DamageValue.ParryNumber, s.Damage.Number);
        // The defeating actor is preserved for helper attribution.
        Assert.Equal("by=Menludiir's unswerving hammer", s.Extra);

        // A helper covering the victim ("intercept" style) is preserved too;
        // self-avoids carry no actor.
        var helper = Swing("a hedon sentinel tries to crush Sihtric, but Ahuli blocks.");
        Assert.Equal(("Sihtric", "by=Ahuli"), (helper.Victim, helper.Extra));
        var self = Swing("a hedon sentinel tries to crush Sihtric, but Sihtric blocks.");
        Assert.Null(self.Extra);

        var counter = Swing("a hedon sentinel tries to crush Ahuli, but Ahuli counters.");
        Assert.Equal("Counter", counter.Damage.ToString());
    }

    [Theory]
    // Avoided melee specials (Ariadneh EoF raid log): the ability rides on
    // the victim as "with <ability>" and must be split off — the victim gets
    // the incoming swing, the attacker's swing is the named special.
    [InlineData("Ancient Grovebeast tries to slash Mempayc with Barrage, but Mempayc parries.",
        "Ancient Grovebeast", "Barrage", "Mempayc", DamageValue.ParryNumber)]
    [InlineData("a gladehoof protector tries to crush YOU with Barrage, but YOU parry.",
        "a gladehoof protector", "Barrage", "YOU", DamageValue.ParryNumber)]
    [InlineData("Musician tries to slash Wuoshi with Demoralizing Processional, but Wuoshi parries.",
        "Musician", "Demoralizing Processional", "Wuoshi", DamageValue.ParryNumber)]
    [InlineData("Ancient Grovebeast tries to slash Mempayc with Barrage, but misses.",
        "Ancient Grovebeast", "Barrage", "Mempayc", DamageValue.MissNumber)]
    public void Avoided_Melee_Special_Splits_The_Ability(
        string line, string attacker, string ability, string victim, long sentinel)
    {
        var s = Swing(line);
        Assert.Equal((attacker, ability, victim, sentinel), (s.Attacker, s.Ability, s.Victim, s.Damage.Number));
    }

    [Fact]
    public void Anonymous_Hit_Attributes_To_Unknown()
    {
        var s = Swing("a creepfern is hit by Acid Spray for 447 poison damage.");
        Assert.Equal(("Unknown", "Acid Spray", "a creepfern", 447L), (s.Attacker, s.Ability, s.Victim, s.Damage.Number));
        Assert.Equal(SwingCategory.NonMelee, s.Category);
        Assert.Equal("poison", s.DamageType);
        // The no-damage flavor variant is not a combat line.
        Assert.Null(EnglishGrammar.TryParse("A creepfern is hit by celestial fire!"));
    }

    [Fact]
    public void S_Ending_Names_Possessivise_Without_The_Extra_S()
    {
        // "Duress' Thunderous Overture …" — the possessive shapes must fold
        // these under the owner, not spawn a phantom combatant per ability.
        var s = Swing("Duress' Thunderous Overture hits a training dummy for 1,234 mental damage.");
        Assert.Equal(("Duress", "Thunderous Overture", "a training dummy"), (s.Attacker, s.Ability, s.Victim));

        var heal = Swing("Duress' Bria's Inspiring Ballad heals Duress for 88 hit points.");
        Assert.Equal(("Duress", "Bria's Inspiring Ballad"), (heal.Attacker, heal.Ability));

        // Apostrophes inside plain names still parse as plain attacks.
        var plain = Swing("Malkonis D'Morte hits Menludiir for 500 crushing damage.");
        Assert.Equal(("Malkonis D'Morte", EnglishGrammar.AutoAttackAbility), (plain.Attacker, plain.Ability));
    }

    [Theory]
    // Auto-attack vs skill follows the ABILITY, not the damage school:
    // a named combat art dealing slashing is a SKILL (ACT's split), while
    // plain hits and multi/double/flurry autoattack verbs stay melee.
    [InlineData("Asame's Impale hits a bloom custodian for 4,000 piercing damage.", SwingCategory.NonMelee)]
    [InlineData("Asame hits a bloom custodian for 1,200 slashing damage.", SwingCategory.Melee)]
    // Infused autoattacks keep their melee category whatever the school.
    [InlineData("Asame hits Sarik the Fang for 4,540 disease damage.", SwingCategory.Melee)]
    [InlineData("Asame multi attacks Sarik the Fang for a critical of 3,831 disease damage.", SwingCategory.Melee)]
    [InlineData("Menludiir's unswerving hammer multi attacks a krait patriarch for a critical of 2,378 crushing damage.", SwingCategory.NonMelee)]
    [InlineData("YOUR Divine Strike hits a bog slug for a critical of 15,032 divine damage.", SwingCategory.NonMelee)]
    public void AutoAttack_Versus_Skill_Follows_The_Ability(string line, SwingCategory expected) =>
        Assert.Equal(expected, Swing(line).Category);

    [Fact]
    public void Your_Ward_Absorb_On_Yourself()
    {
        // Real line (Menludiir log): first-person wards were unparsed and
        // third-person wards on YOU created a phantom "YOURSELF" combatant.
        var s = Swing("YOUR Stonewill absorbs 720 points of damage from being done to YOURSELF. (479 points remaining)");
        Assert.Equal((SwingCategory.Healing, "YOU", "Stonewill", "YOURSELF"), (s.Category, s.Attacker, s.Ability, s.Victim));
        Assert.Equal(720, s.Damage.Number);
        Assert.Equal(EnglishGrammar.WardAbsorbType, s.DamageType);
        Assert.Equal("remaining=479", s.Extra);
    }

    [Fact]
    public void Yourself_Resolves_To_The_Log_Owner()
    {
        var engine = new Engine.ParserEngine("log", "Menludiir");
        var processor = new Engine.LogLineProcessor(engine);
        foreach (var raw in new[]
        {
            "(1779270720)[Wed May 20 10:52:00 2026] a gnoll hits YOU for 500 crushing damage.",
            "(1779270721)[Wed May 20 10:52:01 2026] Mexxy's Ward of Faith absorbs 300 points of damage from being done to YOURSELF.",
        })
        {
            Assert.True(Logs.LogLine.TryParse(raw, out var line));
            processor.Process(line);
        }
        engine.EndCombat();
        var encounter = engine.History[^1];
        Assert.False(encounter.Combatants.ContainsKey("YOURSELF"));
        Assert.True(encounter.Combatants.ContainsKey("MENLUDIIR"));
        Assert.Equal(300, encounter.Combatants["MEXXY"].Healed);
    }

    [Fact]
    public void Dumbfire_Expiry_Is_Not_A_Death()
    {
        // "sputters and dies" is a dumbfire pet expiring, not a combat death;
        // it must not feed the "X dies." shape as victim "…sputters and".
        Assert.Null(EnglishGrammar.TryParse("Rusk's fae fire sputters and dies."));
        Assert.IsType<DeathEvent>(EnglishGrammar.TryParse("Mayong Mistmoore dies."));
    }

    [Fact]
    public void Heals()
    {
        var mine = Swing("YOUR Reverence heals YOU for 13 hit points.");
        Assert.Equal((SwingCategory.Healing, "YOU", "Reverence", "YOU", 13L), (mine.Category, mine.Attacker, mine.Ability, mine.Victim, mine.Damage.Number));

        var theirs = Swing("Sofja's Grim Sorcery heals Menludiir for a critical of 1,024 hit points.");
        Assert.Equal(("Sofja", "Grim Sorcery", "Menludiir"), (theirs.Attacker, theirs.Ability, theirs.Victim));
        Assert.True(theirs.Critical);
    }

    [Fact]
    public void Death_Lines()
    {
        var killed = Assert.IsType<DeathEvent>(EnglishGrammar.TryParse("You have killed a glacial tunneler."));
        Assert.Equal(("YOU", "a glacial tunneler"), (killed.Killer, killed.Victim));

        var alas = Assert.IsType<DeathEvent>(EnglishGrammar.TryParse("Alas, a Thurgadin watcher has died from pain and suffering."));
        Assert.Equal(("Unknown", "a Thurgadin watcher"), (alas.Killer, alas.Victim));

        var slain = Assert.IsType<DeathEvent>(EnglishGrammar.TryParse("Delhagin the Frightful has been slain by Menludiir!"));
        Assert.Equal(("Menludiir", "Delhagin the Frightful"), (slain.Killer, slain.Victim));
    }

    [Fact]
    public void Zone_Line()
    {
        var zone = Assert.IsType<ZoneEvent>(EnglishGrammar.TryParse("You have entered Qeynos Province District."));
        Assert.Equal("Qeynos Province District", zone.ZoneName);
    }

    [Theory]
    [InlineData("\\aPC -1 Obeax:Obeax\\/a tells General (2), \"hits for days\"")]
    [InlineData("You say, \"Hail, Stissa, Emissary of the Speaker\"")]
    [InlineData("Logging to 'logs/Varsoon/eq2log_Menludiir.txt' is now *ON*")]
    public void Chat_And_System_Lines_Do_Not_Parse(string message)
    {
        Assert.Null(EnglishGrammar.TryParse(message));
    }
}
