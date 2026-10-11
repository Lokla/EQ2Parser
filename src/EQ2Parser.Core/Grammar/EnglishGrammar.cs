using System.Globalization;
using System.Text.RegularExpressions;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.Core.Grammar;

/// <summary>
/// EQ2 English combat-line grammar (cleanroom — patterns derived from real
/// TLE-era logs, see docs/engine-behaviour.md for the compatibility rules).
///
/// Perspective: "YOU"/"YOUR" resolve to the log owner's name at the driver
/// level, not here — the grammar returns the literal "YOU" marker.
///
/// Melee (auto-attack) vs non-melee (skill) follows the ABILITY, never the
/// damage school — infusions can make autoattacks deal any school, and
/// combat arts routinely deal crushing/piercing/slashing.
/// </summary>
public static partial class EnglishGrammar
{
    /// <summary>Marker returned for first-person actors; the driver replaces
    /// it with the log owner's character name.</summary>
    public const string You = "YOU";

    public const string AutoAttackAbility = Swing.AutoAttackAbility;

    // ── Amounts ─────────────────────────────────────────────────────────────
    // 368 · 15,032 · 140.6K · 1.2M · 1.5B
    // The number in every pattern that carries one (the amount group and the
    // ward's remaining group), written once so the shapes cannot drift apart.
    // It starts with a DIGIT: the game never writes an amount behind a
    // comma, and the older [\d,]+ read ",5" as 5 and let "," through to the
    // number parser. Every amount in the committed corpus and in the grammar
    // tests starts with a digit, and a digit-first amount matches exactly as
    // it did (same group, same backtracking order), so no real shape moved.
    // Still loose past the first character ("5,", "1,2,3", non-ASCII
    // digits): TryParseAmount decides what is a number.
    private const string Amount = @"\d[\d,]*(?:\.\d+)?[KMB]?";

    /// <summary>Plausibility ceiling on one parsed amount: ten trillion.
    /// A line's text is not entirely the game's to choose (a custom emote
    /// can be shaped like a damage line), and without a ceiling one forged
    /// amount near <see cref="long.MaxValue"/> made every total it joined
    /// wrap negative, and made any checked sum over the swings throw.
    ///
    /// Why this number. Above anything real: the grammar reads amounts up
    /// to the B suffix, so with a mantissa under a thousand the notation
    /// tops out at 999.9B, about a tenth of this; the largest hit in the
    /// committed raid corpus is 140.6K. Far below overflow: it takes over
    /// 900,000 amounts of exactly this size in ONE running total to pass a
    /// long, where a fight sums a few thousand swings and a forged line
    /// arrives at chat speed. An amount over it is refused like every other
    /// unrepresentable amount: the line is a non-match, never a clamped or
    /// zero hit.</summary>
    public const long MaxAmount = 10_000_000_000_000;

    // ── Damage ──────────────────────────────────────────────────────────────
    // YOUR Divine Strike hits a bog slug for a critical of 15,032 divine damage.
    // YOU hit a lesser shadowbone skeleton for a critical of 5,529 crushing damage.
    // Guard Moor hits a bog slug hatchling for 368 slashing damage.
    // Badbang's Magic Feedback hits Delhagin the Frightful for 14 magic damage.
    // Menludiir's unswerving hammer multi attacks a krait patriarch for a critical of 2,378 crushing damage.

    // Both conjugations of every autoattack verb: third person ("Lirianna
    // multi attacks") AND second person ("YOU multi attack" / "YOU flurry").
    // The second-person forms were missing until 2026-08 — every player's OWN
    // multi/flurry/AoE hits fell through unmatched and vanished from their own
    // parse (while guildmates' logs showed them fine, third-person). Plural
    // before singular so the alternation matches greedily without backtracking.
    private const string DamageVerbs =
        "hits|hit|multi attacks|multi attack|double attacks|double attack|"
        + "flurries|flurry|aoe attacks|aoe attack";

    [GeneratedRegex($@"^YOUR (?<ability>.+?) (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>{Amount}) (?<school>\w+) damage\.$")]
    private static partial Regex YourAbilityDamage();

    [GeneratedRegex($@"^YOU (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>{Amount}) (?<school>\w+) damage\.$")]
    private static partial Regex YouAutoDamage();

    [GeneratedRegex($@"^(?<attacker>.+?)'s? (?<ability>.+?) (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>{Amount}) (?<school>\w+) damage\.$")]
    private static partial Regex PossessiveAbilityDamage();

    [GeneratedRegex($@"^(?<attacker>.+?) (?<verb>{DamageVerbs}) (?<victim>.+?) for (?<crit>a critical of )?(?<amount>{Amount}) (?<school>\w+) damage\.$")]
    private static partial Regex PlainDamage();

    // a creepfern is hit by Acid Spray for 447 poison damage.  (anonymous
    // source — dumbfires/ground effects; attributed to "Unknown". Must be
    // tried before PlainDamage, which would split this into a phantom
    // attacker "a creepfern is" and victim "by Acid Spray".)
    [GeneratedRegex($@"^(?<victim>.+?) is hit by (?<ability>.+?) for (?<crit>a critical of )?(?<amount>{Amount}) (?<school>\w+) damage\.$")]
    private static partial Regex AnonymousHit();

    // ── Avoids ──────────────────────────────────────────────────────────────
    // a krait patriarch tries to pierce YOU, but misses.
    // a krait patriarch tries to crush Menludiir, but Menludiir parries.
    // ... but YOU block. / but YOU resist. / but Badbang ripostes. / but X dodges.

    [GeneratedRegex(@"^(?<attacker>.+?) tries to (?<verb>\w+) (?<victim>.+?), but (?:(?<outcomeactor>.+?) )?(?<outcome>misses|parries|ripostes|blocks|resists|dodges|counters|block|resist|riposte|parry|dodge|counter)\.$")]
    private static partial Regex AvoidLine();

    // ── Heals ───────────────────────────────────────────────────────────────
    // YOUR Reverence heals YOU for 13 hit points.
    // Sofja's Grim Sorcery heals Menludiir for a critical of 1,024 hit points.

    [GeneratedRegex($@"^YOUR (?<ability>.+?) heals (?<victim>.+?) for (?<crit>a critical of )?(?<amount>{Amount}) hit points?\.$")]
    private static partial Regex YourHeal();

    [GeneratedRegex($@"^(?<attacker>.+?)'s? (?<ability>.+?) heals (?<victim>.+?) for (?<crit>a critical of )?(?<amount>{Amount}) hit points?\.$")]
    private static partial Regex PossessiveHeal();

    // ── Zero-damage hits ────────────────────────────────────────────────────
    // Shyoh's Vampiric Requiem hits Shyoh but fails to inflict any damage.
    // YOUR Smite hits a blood colossus but fails to inflict any damage.

    [GeneratedRegex(@"^YOUR (?<ability>.+?) hits (?<victim>.+?) but fails to inflict any damage\.$")]
    private static partial Regex YourNoDamage();

    [GeneratedRegex(@"^(?<attacker>.+?)'s? (?<ability>.+?) hits (?<victim>.+?) but fails to inflict any damage\.$")]
    private static partial Regex PossessiveNoDamage();

    [GeneratedRegex(@"^(?<attacker>.+?) hits (?<victim>.+?) but fails to inflict any damage\.$")]
    private static partial Regex PlainNoDamage();

    // ── First-person avoids ─────────────────────────────────────────────────
    // YOU try to pierce Malkonis D'Morte but miss.

    [GeneratedRegex(@"^YOU try to (?<verb>\w+) (?<victim>.+?),? but (?:(?<outcomeactor>.+?) )?(?<outcome>miss|misses|parries|ripostes|blocks|resists|dodges|counters|block|resist|riposte|parry|dodge|counter)\.$")]
    private static partial Regex YouAvoid();

    // ── Wards ───────────────────────────────────────────────────────────────
    // Alexsian's Abhorrent Shroud absorbs 443 points of damage from being done to Mempayc. (4041 points remaining)
    // ACT convention: a ward absorb is a HEAL with damage type "Ward (Absorb)".

    public const string WardAbsorbType = "Ward (Absorb)";

    [GeneratedRegex($@"^(?<attacker>.+?)'s? (?<ability>.+?) absorbs (?<amount>{Amount}) points? of damage from being done to (?<victim>.+?)\.(?: \((?<remaining>{Amount}) points? remaining\))?$")]
    private static partial Regex WardAbsorb();

    // YOUR Stonewill absorbs 720 points of damage from being done to YOURSELF. (479 points remaining)
    [GeneratedRegex($@"^YOUR (?<ability>.+?) absorbs (?<amount>{Amount}) points? of damage from being done to (?<victim>.+?)\.(?: \((?<remaining>{Amount}) points? remaining\))?$")]
    private static partial Regex YourWardAbsorb();

    // ── Power ───────────────────────────────────────────────────────────────
    // Tsuna's Empower Servant refreshes Tsuna for 59 mana points.

    [GeneratedRegex($@"^(?<attacker>.+?)'s? (?<ability>.+?) refreshes (?<victim>.+?) for (?<amount>{Amount}) (?:mana|power) points?\.$")]
    private static partial Regex PowerRefresh();

    [GeneratedRegex($@"^YOUR (?<ability>.+?) refreshes (?<victim>.+?) for (?<amount>{Amount}) (?:mana|power) points?\.$")]
    private static partial Regex YourPowerRefresh();

    // ── Threat ──────────────────────────────────────────────────────────────
    // Badbang's Insolent Gibe increases THEIR hate with a dragonspawn whelp for 1,234 threat.
    // Noxyi's Dynamism reduces THEIR hate with a blood colossus for 567 threat.
    // The amount is positive either way: the damage-type marker is the only
    // thing that tells a threat increase from a reduction.

    /// <summary>Damage-type marker on a threat INCREASE ("… increases
    /// THEIR hate with …").</summary>
    public const string ThreatIncreaseType = "threat";

    /// <summary>Damage-type marker on a threat REDUCTION ("… reduces THEIR
    /// hate with …").</summary>
    public const string ThreatReduceType = "threat-reduce";

    [GeneratedRegex($@"^(?<attacker>.+?)'s? (?<ability>.+?) (?<direction>increases|reduces) (?:THEIR|YOUR) hate with (?<victim>.+?) for (?<amount>{Amount}) threat\.$")]
    private static partial Regex ThreatChange();

    [GeneratedRegex($@"^YOUR (?<ability>.+?) (?<direction>increases|reduces) YOUR hate with (?<victim>.+?) for (?<amount>{Amount}) threat\.$")]
    private static partial Regex YourThreatChange();

    // ── Cures ───────────────────────────────────────────────────────────────
    // Catofur's Cure Trauma relieves Eviscerate from Badbang.

    [GeneratedRegex(@"^(?<attacker>.+?)'s? (?<ability>.+?) relieves (?<effect>.+?) from (?<victim>.+?)\.$")]
    private static partial Regex CureRelieves();

    [GeneratedRegex(@"^YOUR (?<ability>.+?) relieves (?<effect>.+?) from (?<victim>.+?)\.$")]
    private static partial Regex YourCureRelieves();

    // ── Status effects (control) ────────────────────────────────────────────
    // An Awakened trial servant is stunned!            (apply — flavour varies:
    // "is totally confused!", "is unnerved by a stare!", "is seared by a
    // blast of heat!")
    // An Awakened trial servant is no longer stunned.  (release — uniform)

    [GeneratedRegex(@"^(?<victim>.+?) is (?:totally )?(?<effect>stunned|mesmerized|stupified|confused|unnerved|dazzled|gloomy|afraid|feared|disoriented|seared|dazed|rooted|snared)(?: by .+?)?!$")]
    private static partial Regex StatusApplied();

    // Effect-specific apply flavours (real Wuoshi log shapes):
    // A charred diviner is briefly frozen in place!   → frozen (root)
    // Claw looks terrified!                           → terrified (fear)
    // Slaverjaw is consumed by gloom!                 → gloomy
    [GeneratedRegex(@"^(?<victim>.+?) is briefly frozen in place!$")]
    private static partial Regex StatusFrozen();

    [GeneratedRegex(@"^(?<victim>.+?) looks terrified!$")]
    private static partial Regex StatusTerrified();

    [GeneratedRegex(@"^(?<victim>.+?) is consumed by gloom!$")]
    private static partial Regex StatusGloom();

    // Virustotal silences An animated sentinel's actions.  (named source —
    // the only status line that credits an attacker; symmetric, so a mob
    // silencing a player logs the same way.)
    [GeneratedRegex(@"^(?<attacker>.+?) silences (?<victim>.+?)'s actions\.$")]
    private static partial Regex StatusSilences();

    [GeneratedRegex(@"^(?<victim>.+?) is no longer (?<effect>[a-z]+)\.$")]
    private static partial Regex StatusReleased();

    // Second release family for named effects:
    // Sofja is no longer affected by the darkness.
    [GeneratedRegex(@"^(?<victim>.+?) is no longer affected by (?<effect>.+?)\.$")]
    private static partial Regex StatusAffectedReleased();

    // Generic (unnamed) releases — close the victim's most recent open
    // effect, whatever it was:
    // A muck toad regains their wits.      (stun/daze/fear/confuse/…)
    // A muck toad returns to normal.       (mez-break flavour)
    [GeneratedRegex(@"^(?<victim>.+?) (?:regains (?:[Tt]heir wits|[Hh]is composure|[Hh]er composure|[Ii]ts composure|[Tt]heir composure)|returns to normal)\.$")]
    private static partial Regex StatusGenericReleased();

    // A muck toad no longer appears terrified.  (second terrified phrasing)
    [GeneratedRegex(@"^(?<victim>.+?) no longer appears (?<effect>[a-z]+)\.$")]
    private static partial Regex StatusAppearsReleased();

    // The corruptive symbol fades from Sofja.   (named-effect fade family)
    [GeneratedRegex(@"^The (?<effect>.+?) fades from (?<victim>.+?)\.$")]
    private static partial Regex StatusFadesFrom();

    // ── Deaths ──────────────────────────────────────────────────────────────
    // You have killed a glacial tunneler.
    // Alas, a Thurgadin watcher has died from pain and suffering.
    // <victim> has been slain by <killer>!

    [GeneratedRegex(@"^You have killed (?<victim>.+?)\.$")]
    private static partial Regex YouKilled();

    [GeneratedRegex(@"^Alas, (?<victim>.+?) has died(?: from pain and suffering)?\.$")]
    private static partial Regex AlasDied();

    [GeneratedRegex(@"^(?<victim>.+?) has been slain by (?<killer>.+?)!$")]
    private static partial Regex SlainBy();

    // Mayong Mistmoore dies.  (the generic third-person death — how raid
    // members witness a boss kill; no killer attribution on the line)
    [GeneratedRegex(@"^(?<victim>.+?) dies\.$")]
    private static partial Regex Dies();

    // Wuoshi has killed Lozzy.  (third-person kill attribution)
    [GeneratedRegex(@"^(?<killer>.+?) has killed (?<victim>.+?)\.$")]
    private static partial Regex HasKilled();

    // Wuoshi's has been banished!  (epic-mob death flavor; the stray
    // possessive is a game typo we tolerate)
    [GeneratedRegex(@"^(?<victim>.+?)(?:'s)? has been banished!$")]
    private static partial Regex Banished();

    // ── Zone ────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^You have entered (?<zone>.+?)\.$")]
    private static partial Regex ZoneEntered();

    /// <summary>The zone name when <paramref name="message"/> is a
    /// "You have entered …" line, else null. Exposed for the attach-time
    /// zone look-behind (<see cref="Logs.ZoneLookbehind"/>), which scans
    /// raw history the normal parse pipeline never sees.</summary>
    public static string? TryParseZoneEntered(string message)
    {
        var m = ZoneEntered().Match(message);
        return m.Success ? m.Groups["zone"].Value : null;
    }

    /// <summary>Parse one log message. Null = not a line this grammar knows.</summary>
    public static GrammarEvent? TryParse(string message)
    {
        // Two rules govern this dispatch:
        //   * ORDER matters within a family: possessive forms before the
        //     plain form ("Badbang's Magic Feedback hits …" must not parse
        //     with attacker "Badbang's Magic Feedback"), anonymous first.
        //   * Every family sits behind a LITERAL its patterns provably
        //     require (guards never change what matches — they only skip
        //     regexes that cannot). An unmatched line (chat, emotes, quest
        //     text — most of a real log) used to walk ~20 lazy-dot regexes
        //     and backtrack through each; now it costs a handful of
        //     Contains/EndsWith scans and zero regex work.
        // And one rule governs every branch that carries a number: the
        // builder returns null when the amount cannot be represented (see
        // TryParseAmount), and the branch then FALLS THROUGH exactly as if
        // its pattern had not matched. Never a throw, never a made-up zero.
        Match m;
        SwingEvent? swing;

        if (message.EndsWith(" damage.", StringComparison.Ordinal))
        {
            if (message.EndsWith(" but fails to inflict any damage.", StringComparison.Ordinal))
            {
                if ((m = YourNoDamage().Match(message)).Success)
                    return NoDamageHit(m, You, m.Groups["ability"].Value);
                if ((m = PossessiveNoDamage().Match(message)).Success)
                    return NoDamageHit(m, m.Groups["attacker"].Value, m.Groups["ability"].Value);
                if ((m = PlainNoDamage().Match(message)).Success)
                    return NoDamageHit(m, m.Groups["attacker"].Value, AutoAttackAbility);
            }
            else
            {
                if ((m = YourAbilityDamage().Match(message)).Success
                    && (swing = Damage(m, You, m.Groups["ability"].Value)) is not null)
                    return swing;
                if ((m = YouAutoDamage().Match(message)).Success
                    && (swing = Damage(m, You, AutoAttackAbility)) is not null)
                    return swing;
                // Anonymous before possessive: "Bob's pet is hit by Acid
                // Spray for 447 poison damage." otherwise phantom-parses as
                // attacker "Bob", ability "pet is", victim "by Acid Spray".
                if (message.Contains(" is hit by ", StringComparison.Ordinal)
                    && (m = AnonymousHit().Match(message)).Success
                    && (swing = Damage(m, "Unknown", m.Groups["ability"].Value)) is not null)
                    return swing;
                if ((m = PossessiveAbilityDamage().Match(message)).Success
                    && (swing = Damage(m, m.Groups["attacker"].Value, m.Groups["ability"].Value)) is not null)
                    return swing;
                if ((m = PlainDamage().Match(message)).Success
                    && (swing = Damage(m, m.Groups["attacker"].Value, AutoAttackAbility)) is not null)
                    return swing;
            }
        }
        if (message.Contains(" tries to ", StringComparison.Ordinal)
            && (m = AvoidLine().Match(message)).Success)
            return Avoid(m);
        if (message.Contains(" heals ", StringComparison.Ordinal))
        {
            if ((m = YourHeal().Match(message)).Success
                && (swing = Heal(m, You)) is not null)
                return swing;
            if ((m = PossessiveHeal().Match(message)).Success
                && (swing = Heal(m, m.Groups["attacker"].Value)) is not null)
                return swing;
        }
        if (message.StartsWith("YOU try to ", StringComparison.Ordinal)
            && (m = YouAvoid().Match(message)).Success)
            return AvoidFrom(m, You);
        if (message.Contains(" absorbs ", StringComparison.Ordinal))
        {
            if ((m = YourWardAbsorb().Match(message)).Success
                && (swing = Ward(m, You)) is not null)
                return swing;
            if ((m = WardAbsorb().Match(message)).Success
                && (swing = Ward(m, m.Groups["attacker"].Value)) is not null)
                return swing;
        }
        if (message.Contains(" refreshes ", StringComparison.Ordinal))
        {
            if ((m = YourPowerRefresh().Match(message)).Success
                && (swing = PowerReplenish(m, You)) is not null)
                return swing;
            if ((m = PowerRefresh().Match(message)).Success
                && (swing = PowerReplenish(m, m.Groups["attacker"].Value)) is not null)
                return swing;
        }
        if (message.Contains(" hate with ", StringComparison.Ordinal))
        {
            if ((m = YourThreatChange().Match(message)).Success
                && (swing = Threat(m, You)) is not null)
                return swing;
            if ((m = ThreatChange().Match(message)).Success
                && (swing = Threat(m, m.Groups["attacker"].Value)) is not null)
                return swing;
        }
        if (message.Contains(" relieves ", StringComparison.Ordinal))
        {
            if ((m = YourCureRelieves().Match(message)).Success)
                return Cure(m, You);
            if ((m = CureRelieves().Match(message)).Success)
                return Cure(m, m.Groups["attacker"].Value);
        }
        if (message.EndsWith('!'))
        {
            if ((m = StatusApplied().Match(message)).Success)
                return Status(m.Groups["victim"].Value, m.Groups["effect"].Value, applied: true);
            if ((m = StatusFrozen().Match(message)).Success)
                return Status(m.Groups["victim"].Value, "frozen", applied: true);
            if ((m = StatusTerrified().Match(message)).Success)
                return Status(m.Groups["victim"].Value, "terrified", applied: true);
            if ((m = StatusGloom().Match(message)).Success)
                return Status(m.Groups["victim"].Value, "gloomy", applied: true);
        }
        if (message.Contains(" is no longer ", StringComparison.Ordinal))
        {
            if ((m = StatusReleased().Match(message)).Success)
                return Status(m.Groups["victim"].Value, m.Groups["effect"].Value, applied: false);
            if ((m = StatusAffectedReleased().Match(message)).Success)
                return Status(m.Groups["victim"].Value, m.Groups["effect"].Value, applied: false);
        }
        if (message.Contains(" silences ", StringComparison.Ordinal)
            && (m = StatusSilences().Match(message)).Success)
            return Status(m.Groups["victim"].Value, "silenced", applied: true, m.Groups["attacker"].Value);
        if ((message.Contains(" regains ", StringComparison.Ordinal)
                || message.EndsWith(" returns to normal.", StringComparison.Ordinal))
            && !message.StartsWith("Your ", StringComparison.Ordinal) // "Your health returns to normal."
            && (m = StatusGenericReleased().Match(message)).Success)
            return Status(m.Groups["victim"].Value, "any", applied: false);
        if (message.Contains(" no longer appears ", StringComparison.Ordinal)
            && (m = StatusAppearsReleased().Match(message)).Success)
            return Status(m.Groups["victim"].Value, m.Groups["effect"].Value, applied: false);
        if (message.Contains(" fades from ", StringComparison.Ordinal)
            && (m = StatusFadesFrom().Match(message)).Success)
            return Status(m.Groups["victim"].Value, m.Groups["effect"].Value, applied: false);
        if (message.StartsWith("You have killed ", StringComparison.Ordinal)
            && (m = YouKilled().Match(message)).Success)
            return new DeathEvent(You, m.Groups["victim"].Value);
        if (message.StartsWith("Alas, ", StringComparison.Ordinal)
            && (m = AlasDied().Match(message)).Success)
            return new DeathEvent("Unknown", m.Groups["victim"].Value);
        if (message.Contains(" has been slain by ", StringComparison.Ordinal)
            && (m = SlainBy().Match(message)).Success)
            return new DeathEvent(m.Groups["killer"].Value, m.Groups["victim"].Value);
        if (message.Contains(" has killed ", StringComparison.Ordinal)
            && (m = HasKilled().Match(message)).Success)
            return new DeathEvent(m.Groups["killer"].Value, m.Groups["victim"].Value);
        if (message.EndsWith(" dies.", StringComparison.Ordinal))
        {
            // "Rusk's fae fire sputters and dies." — a dumbfire expiring
            // naturally, not a combat death; must not feed the Dies() shape.
            if (message.EndsWith(" sputters and dies.", StringComparison.Ordinal))
                return null;
            if ((m = Dies().Match(message)).Success)
                return new DeathEvent("Unknown", m.Groups["victim"].Value);
        }
        if (message.EndsWith(" has been banished!", StringComparison.Ordinal)
            && (m = Banished().Match(message)).Success)
            return new DeathEvent("Unknown", m.Groups["victim"].Value);
        if (message.StartsWith("You have entered ", StringComparison.Ordinal)
            && (m = ZoneEntered().Match(message)).Success)
            return new ZoneEvent(m.Groups["zone"].Value);
        if (message.StartsWith("This instance will expire in ", StringComparison.Ordinal)
            && ParseLockoutRemaining(message) is { } remaining)
            return new InstanceLockoutEvent(remaining);

        return null;
    }

    /// <summary>Public form of the instance-lockout parse for the
    /// attach-time look-behind, which scans raw history outside TryParse.</summary>
    public static TimeSpan? TryParseInstanceLockout(string message) =>
        message.StartsWith("This instance will expire in ", StringComparison.Ordinal)
            ? ParseLockoutRemaining(message)
            : null;

    /// <summary>"This instance will expire in 3 days 23 hours." → the
    /// TimeSpan. The client prints whole units largest-first ("7 days",
    /// "3 days 23 hours", "45 minutes"); anything unrecognised yields null
    /// rather than a wrong lockout.</summary>
    private static TimeSpan? ParseLockoutRemaining(string message)
    {
        var body = message["This instance will expire in ".Length..].TrimEnd('.', ' ');
        var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 6 || parts.Length % 2 != 0)
            return null;
        // Summed in whole minutes as a long, never as TimeSpans: the count
        // is whatever the line says, and TimeSpan.FromDays(99999999) THROWS
        // (so did adding two large spans). At most three int-sized counts
        // times 1,440 stays far inside a long, so nothing here can overflow.
        long totalMinutes = 0;
        for (var i = 0; i < parts.Length; i += 2)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return null;
            long minutesPerUnit = parts[i + 1].TrimEnd('s') switch
            {
                "day" => 24 * 60,
                "hour" => 60,
                "minute" => 1,
                _ => 0,
            };
            if (minutesPerUnit == 0)
                return null;
            totalMinutes += n * minutesPerUnit;
        }
        return totalMinutes is > 0 and <= MaxLockoutMinutes ? TimeSpan.FromMinutes(totalMinutes) : null;
    }

    /// <summary>Ceiling on a believable lockout: a year, where the real ones
    /// run to days. Not a statement about the game's longest lockout — it is
    /// there because the caller adds the result to the line's timestamp, and
    /// a span that merely FITS a TimeSpan (millions of days) would throw on
    /// that addition instead. Anything longer is "a wrong lockout": null.</summary>
    private const long MaxLockoutMinutes = 366L * 24 * 60;

    private static SwingEvent? Damage(Match m, string attacker, string ability)
    {
        if (!TryParseAmount(m.Groups["amount"].Value, out var amount))
            return null;
        var school = m.Groups["school"].Value;
        // Auto-attack vs skill follows the ABILITY alone, never the school:
        // named abilities are skills even when they deal
        // crushing/piercing/slashing (an Assassin's whole kit), and plain
        // attacks plus the multi/double/flurry/AoE autoattack verbs are
        // melee even when infused to another school entirely (Asame's
        // autoattack deals disease). Matches ACT's Auto-Attack (Out) /
        // Skill/Ability (Out) split.
        var special = SpecialFromVerb(m.Groups["verb"].Value);
        var category = ability == AutoAttackAbility || special != "None"
            ? SwingCategory.Melee
            : SwingCategory.NonMelee;
        return new SwingEvent(
            category,
            Critical: m.Groups["crit"].Success,
            Special: special,
            Attacker: attacker,
            Ability: ability,
            Damage: amount,
            Victim: m.Groups["victim"].Value,
            DamageType: school);
    }

    private static SwingEvent? Heal(Match m, string attacker) =>
        TryParseAmount(m.Groups["amount"].Value, out var amount)
            ? new(
                SwingCategory.Healing,
                Critical: m.Groups["crit"].Success,
                Special: "None",
                Attacker: attacker,
                Ability: m.Groups["ability"].Value,
                Damage: amount,
                Victim: m.Groups["victim"].Value,
                DamageType: "heal")
            : null;

    private static SwingEvent NoDamageHit(Match m, string attacker, string ability) => new(
        ability == AutoAttackAbility ? SwingCategory.Melee : SwingCategory.NonMelee,
        Critical: false,
        Special: "None",
        Attacker: attacker,
        Ability: ability,
        Damage: DamageValue.NoDamage,
        Victim: m.Groups["victim"].Value,
        DamageType: "none");

    private static SwingEvent? PowerReplenish(Match m, string attacker) =>
        TryParseAmount(m.Groups["amount"].Value, out var amount)
            ? new(
                SwingCategory.PowerReplenish,
                Critical: false,
                Special: "None",
                Attacker: attacker,
                Ability: m.Groups["ability"].Value,
                Damage: amount,
                Victim: m.Groups["victim"].Value,
                DamageType: "power")
            : null;

    private static SwingEvent? Threat(Match m, string attacker) =>
        TryParseAmount(m.Groups["amount"].Value, out var amount)
            ? new(
                SwingCategory.Threat,
                Critical: false,
                Special: "None",
                Attacker: attacker,
                Ability: m.Groups["ability"].Value,
                Damage: amount,
                Victim: m.Groups["victim"].Value,
                DamageType: m.Groups["direction"].Value == "reduces" ? ThreatReduceType : ThreatIncreaseType)
            : null;

    private static SwingEvent Status(string victim, string effect, bool applied, string attacker = "Unknown") => new(
        SwingCategory.StatusEffect,
        Critical: false,
        Special: "None",
        Attacker: attacker, // only the silence line names a source
        Ability: effect,
        Damage: DamageValue.NoDamage,
        Victim: victim,
        DamageType: applied ? "applied" : "released");

    private static SwingEvent Cure(Match m, string attacker) => new(
        SwingCategory.CureDispel,
        Critical: false,
        Special: "None",
        Attacker: attacker,
        Ability: m.Groups["ability"].Value,
        Damage: DamageValue.NoDamage,
        Victim: m.Groups["victim"].Value,
        DamageType: m.Groups["effect"].Value);

    private static SwingEvent? Ward(Match m, string attacker)
    {
        if (!TryParseAmount(m.Groups["amount"].Value, out var absorbed))
            return null;
        // The "(N points remaining)" suffix is optional, but when it is
        // there it has to read as a number too: a line with one good count
        // and one impossible one is not a line the game wrote, so the whole
        // shape is refused rather than kept with a guessed remainder.
        string? extra = null;
        if (m.Groups["remaining"].Success)
        {
            if (!TryParseAmount(m.Groups["remaining"].Value, out var remaining))
                return null;
            extra = $"remaining={remaining.Number}";
        }
        return new(
            SwingCategory.Healing, false, "None",
            attacker, m.Groups["ability"].Value,
            absorbed, m.Groups["victim"].Value, WardAbsorbType,
            Extra: extra);
    }

    private static SwingEvent Avoid(Match m) => AvoidFrom(m, m.Groups["attacker"].Value);

    private static SwingEvent AvoidFrom(Match m, string attacker)
    {
        var outcome = m.Groups["outcome"].Value;
        DamageValue damage = outcome switch
        {
            "misses" or "miss" => DamageValue.Miss,
            "parries" or "parry" => new DamageValue(DamageValue.ParryNumber),
            "ripostes" or "riposte" => new DamageValue(DamageValue.RiposteNumber),
            "blocks" or "block" => new DamageValue(DamageValue.BlockNumber),
            "resists" or "resist" => new DamageValue(DamageValue.ResistNumber),
            "counters" or "counter" => DamageValue.Unknown("Counter"),
            _ => DamageValue.Unknown("Dodge"),
        };

        // "tries to slash Mempayc with Barrage, but Mempayc parries." — a
        // melee special: split the ability off the victim. The outcome actor
        // is the true victim name when present, so prefer that cross-check
        // (it keeps abilities that themselves contain " with " intact);
        // otherwise split at the first " with " ("but misses" carries no
        // actor, and weapon-avoids name the weapon rather than the victim).
        var victim = m.Groups["victim"].Value;
        var ability = AutoAttackAbility;
        var actor = m.Groups["outcomeactor"].Value;
        if (actor.Length > 0 && victim.StartsWith(actor + " with ", StringComparison.Ordinal))
        {
            ability = victim[(actor.Length + " with ".Length)..];
            victim = actor;
        }
        else if (victim.IndexOf(" with ", StringComparison.Ordinal) is var idx and > 0)
        {
            ability = victim[(idx + " with ".Length)..];
            victim = victim[..idx];
        }

        // Preserve WHO defeated the attack when it wasn't the victim
        // themselves — another player covering them, or a parrying weapon
        // ("but Ahuli blocks." / "but Menludiir's unswerving hammer
        // parries."). Feeds the avoidance report's helper attribution.
        string? extra = null;
        if (actor.Length > 0 && !actor.Equals(victim, StringComparison.OrdinalIgnoreCase))
            extra = "by=" + actor;

        return new SwingEvent(
            ability == AutoAttackAbility ? SwingCategory.Melee : SwingCategory.NonMelee,
            Critical: false,
            Special: "None",
            Attacker: attacker,
            Ability: ability,
            Damage: damage,
            Victim: victim,
            DamageType: "avoided",
            Extra: extra);
    }

    private static string SpecialFromVerb(string verb) => verb switch
    {
        "hits" or "hit" => "None",
        "multi attacks" or "multi attack" => "Multi Attack",
        "double attacks" or "double attack" => "Multi Attack", // era rename - same mechanic
        "flurries" or "flurry" => "Flurry",
        "aoe attacks" or "aoe attack" => "AoE Attack",
        _ => "None",
    };

    /// <summary>EQ2 abbreviates six-digit-plus amounts in the log —
    /// "140.6K disease damage", "1.2M" — so a plain integer parse silently
    /// DROPPED every big crit (ACT expands the suffix; so do we).
    ///
    /// TOTAL by contract: false, never an exception, for anything that is
    /// not a believable amount. The amount pattern is deliberately loose (a
    /// digit, then digits and commas, an optional decimal and suffix — see
    /// <see cref="Amount"/>), so it admits text no number parser accepts —
    /// and a line's text is not entirely the game's to choose (a custom
    /// emote can end in " damage."), so this is reachable input, not
    /// theory. What the pattern lets through that is refused here:
    ///   * more digits than a long (or a decimal) holds,
    ///   * an amount that fits until its K/M/B suffix scales it,
    ///   * non-ASCII digits (\d matches every Unicode decimal digit),
    ///   * anything over <see cref="MaxAmount"/>, before or after scaling.
    /// The empty and comma-only checks stay although the pattern no longer
    /// produces either: this method must be total for any text handed to it.
    /// A caller that gets false treats the line as NOT matching its shape;
    /// reporting a zero would invent a hit the log never recorded.</summary>
    private static bool TryParseAmount(string text, out DamageValue amount)
    {
        amount = default;
        if (text.Length == 0)
            return false;
        var multiplier = text[^1] switch
        {
            'K' => 1_000L,
            'M' => 1_000_000L,
            'B' => 1_000_000_000L,
            _ => 1L,
        };
        // The amount regex admits decimals with OR without a suffix
        // ("1234.5") — a plain long.Parse on that throws, and a throw on
        // the line path costs the line (it once cost the whole log's tail
        // loop; LogSource now skips the line instead). decimal keeps full
        // precision for plain integer amounts.
        var digits = multiplier > 1 ? text[..^1] : text;
        if (!decimal.TryParse(digits.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return false;
        // Two ceiling checks, in this order: the first also keeps the
        // multiplication inside decimal's own range (the ceiling times the
        // largest suffix is 1e22, far under decimal's 7.9e28), so neither
        // the product nor the cast below can overflow. The ceiling is well
        // inside a long, so it is the only range check needed.
        if (value > MaxAmount)
            return false;
        var scaled = Math.Round(value * multiplier);
        if (scaled > MaxAmount)
            return false;
        amount = new((long)scaled);
        return true;
    }
}
