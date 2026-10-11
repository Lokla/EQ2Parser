using System.Security.Cryptography;
using System.Text;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Grammar;

namespace EQ2Parser.Core.Upload;

/// <summary>
/// Maps a completed <see cref="Encounter"/> onto the EQ2Lexicon ingest
/// schema. The encid is a stable content hash so re-uploading the same
/// fight dedupes server-side (the site's UNIQUE(world, act_encid) rule).
/// </summary>
public static class PayloadBuilder
{
    public static LexiconPayload Build(
        Encounter encounter, string loggerServer, IReadOnlyList<string>? clientWarnings = null)
    {
        var allies = encounter.GetAllies();
        var allyKeys = new HashSet<string>(allies.Select(a => a.Key), StringComparer.Ordinal);
        var encounterSeconds = encounter.Duration.TotalSeconds;

        var combatants = new List<PayloadCombatant>();
        var damageTypes = new List<PayloadDamageType>();
        var attackTypes = new List<PayloadAttackType>();

        foreach (var combatant in encounter.Combatants.Values.OrderByDescending(c => c.Damage))
        {
            var isAlly = allyKeys.Contains(combatant.Key);
            if (!isAlly && !HasUploadedSwing(combatant))
                continue; // exists only through swings the upload leaves out — see HasUploadedSwing
            var outgoingDamage = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.OutgoingDamage)?.All;
            var healedOut = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.HealedOut)?.All;
            var personalSeconds = combatant.Duration.TotalSeconds;
            // Victim-only combatants (an add that died without acting) have
            // sentinel MaxValue/MinValue windows — clamp to the fight, or
            // the site stores year-9999/year-1 garbage.
            var start = combatant.StartTime == DateTimeOffset.MaxValue ? encounter.StartTime : combatant.StartTime;
            var end = combatant.EndTime == DateTimeOffset.MinValue ? start : combatant.EndTime;

            combatants.Add(new PayloadCombatant
            {
                Name = combatant.Name,
                Ally = isAlly ? "T" : "F",
                StartTime = Ts(start),
                EndTime = Ts(end),
                Duration = (int)personalSeconds,
                Damage = combatant.Damage,
                Kills = combatant.GetKills(isAlly),
                Healed = combatant.Healed,
                CritHeals = healedOut?.CritHits ?? 0,
                Heals = healedOut?.SwingCount ?? 0,
                CureDispels = combatant.CureDispels,
                PowerDrain = combatant.PowerDamage,
                PowerReplenish = combatant.PowerReplenish,
                Dps = Rate(combatant.Damage, personalSeconds),
                EncDps = Rate(combatant.Damage, encounterSeconds),
                EncHps = Rate(combatant.Healed, encounterSeconds),
                Hits = outgoingDamage?.Hits ?? 0,
                CritHits = outgoingDamage?.CritHits ?? 0,
                Blocked = outgoingDamage?.Avoids ?? 0,
                Misses = outgoingDamage?.Misses ?? 0,
                Swings = outgoingDamage?.SwingCount ?? 0,
                HealsTaken = combatant.HealsTaken,
                DamageTaken = combatant.DamageTaken,
                Deaths = combatant.Deaths,
                ToHit = ToHit(outgoingDamage),
            });

            foreach (var (bucketName, bucket) in combatant.OutgoingBuckets)
            {
                if (bucketName == BucketConfig.AllOutgoingRef)
                    continue;
                var all = bucket.All;
                if (all.Swings.Count == 0)
                    continue;
                damageTypes.Add(new PayloadDamageType
                {
                    Combatant = combatant.Name,
                    Type = bucketName,
                    StartTime = Ts(all.StartTime),
                    EndTime = Ts(all.EndTime),
                    Damage = all.Damage,
                    EncDps = Rate(all.Damage, encounterSeconds),
                    Dps = Rate(all.Damage, Math.Max(1, (all.EndTime - all.StartTime).TotalSeconds)),
                    CritPerc = all.Hits > 0 ? Math.Round(100.0 * all.CritHits / all.Hits, 1) : 0,
                    Median = all.Median,
                    MinHit = all.MinHit,
                    MaxHit = all.MaxHit,
                    Hits = all.Hits,
                    CritHits = all.CritHits,
                    Blocked = all.Avoids,
                    Misses = all.Misses,
                    Swings = all.SwingCount,
                });
            }

            // attack_types come from the REFERENCE bucket — every outgoing
            // swing exactly once per ability. Building them per category
            // bucket shipped duplicates (a melee swing feeds Outgoing Damage
            // AND Auto-Attack (Out)) and tripped the site's
            // UNIQUE(combatant_id, swing_type, attack_name) with a 500.
            // That bucket is keyed by ability NAME alone, so each ability
            // is split by swing category here: one row per (name, category)
            // — the site's key, and what ACT and its plugin send.
            if (combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.AllOutgoingRef) is { } reference)
            {
                foreach (var (abilityName, ability) in reference.Abilities)
                {
                    if (abilityName == Bucket.AllAbility)
                        continue;
                    foreach (var (category, stats) in SplitByCategory(ability))
                    {
                        if (AttackRow(combatant.Name, category, stats, encounterSeconds) is { } row)
                            attackTypes.Add(row);
                    }
                }
            }
        }

        var alliedDeaths = allies.Where(a => Swing.LooksLikePlayer(a.Name)).Sum(a => a.Deaths);
        var alliedKills = allies.Sum(a => a.GetKills(isAlly: true));

        return new LexiconPayload
        {
            LoggerName = encounter.OwnerName,
            LoggerServer = loggerServer,
            Encounter = new PayloadEncounter
            {
                EncId = ComputeEncId(encounter),
                Title = encounter.Title,
                Zone = encounter.Zone.Length > 0 ? encounter.Zone : null,
                StartTime = Ts(encounter.StartTime),
                EndTime = Ts(encounter.EndTime),
                Duration = (int)encounterSeconds,
                Damage = encounter.Damage,
                EncDps = encounter.EncDps,
                Kills = alliedKills,
                Deaths = alliedDeaths,
                Success = (int)encounter.GetSuccessLevel(),
            },
            Combatants = combatants,
            DamageTypes = damageTypes,
            AttackTypes = attackTypes,
            ClientWarnings = clientWarnings is { Count: > 0 } ? [.. clientWarnings] : null,
        };
    }

    /// <summary>One <see cref="AbilityStats"/> per swing category the
    /// ability logged in, in order of first appearance. An ability that
    /// damages and heals, or carries a threat component, logs under one
    /// name in several categories; filed as one row under its first
    /// swing's category it put heal and threat amounts on the site's
    /// Damage tab (or took the damage off it). Status-effect swings are
    /// left out — see <see cref="IsUploaded"/>.
    ///
    /// The figures are never re-derived here: a single-category ability
    /// (nearly all of them) is the reference bucket's own stats object, and
    /// a split one is the same <see cref="AbilityStats"/> class fed only
    /// that category's swings, under the same name. Median, hit, crit, miss
    /// and blocked counting therefore cannot drift from the combat
    /// model's.</summary>
    private static List<(SwingCategory Category, AbilityStats Stats)> SplitByCategory(AbilityStats ability)
    {
        List<(SwingCategory Category, AbilityStats Stats)> rows = [];
        var swings = ability.Swings;
        if (swings.Count == 0)
            return rows;

        var first = swings[0].Category;
        var single = true;
        foreach (var swing in swings)
        {
            if (swing.Category != first)
            {
                single = false;
                break;
            }
        }
        if (single && first != SwingCategory.CureDispel)
        {
            if (IsUploaded(first))
                rows.Add((first, ability));
            return rows;
        }

        foreach (var swing in swings)
        {
            if (!IsUploaded(swing.Category))
                continue;
            AbilityStats? stats = null;
            foreach (var (category, existing) in rows)
            {
                if (category == swing.Category)
                {
                    stats = existing;
                    break;
                }
            }
            if (stats is null)
                rows.Add((swing.Category, stats = new AbilityStats(ability.Name)));
            stats.Add(swing.Category == SwingCategory.CureDispel ? AsOneEffectRemoved(swing) : swing);
        }
        return rows;
    }

    /// <summary>Category 30 is the parser's own (control-effect apply and
    /// release lines, amount 0, attacker almost always "Unknown"). No reader
    /// on the site selects it, so its rows only grew the site's largest
    /// table. It stays in the encounter for local history and ability
    /// mining. Every other category is ACT's and is uploaded — 13 (power
    /// replenish) and 16 (threat) included.</summary>
    private static bool IsUploaded(SwingCategory category) => category != SwingCategory.StatusEffect;

    /// <summary>Whether the combatant made or received at least one swing
    /// of a category the upload carries. A combatant comes to exist in one
    /// way only: <see cref="Encounter.AddSwing"/> creates the attacker and
    /// the victim of a swing, and files that swing in the attacker's
    /// outgoing reference bucket and the victim's incoming one. So those
    /// two buckets hold every swing a combatant has, and one that has none
    /// in an uploaded category exists ONLY because of swings that are not
    /// uploaded: the anonymous "Unknown" attacker of "X is stunned!" (every
    /// figure zero on the site's parse page), a mob that was only ever
    /// stunned, a name that only ever silenced something. Its row is left
    /// out of the upload; it stays a combatant of the encounter, so local
    /// history, ability mining and the encid seed (which counts the
    /// encounter's combatants) are untouched.
    ///
    /// Nothing can be orphaned by this: damage_types rows come from the
    /// category-linked buckets and attack rows from uploaded categories, so
    /// a combatant with either has an uploaded swing and keeps its row. A
    /// combatant with zero damage who healed, was healed, was hit, was
    /// attacked and avoided it, died or cured has one too. Allies are never
    /// tested — in practice that is the log owner, who is their own ally
    /// without having interacted with anyone. And a payload cannot end up
    /// with no combatants: only a hostile swing starts an encounter, and
    /// both its ends pass.</summary>
    private static bool HasUploadedSwing(Combatant combatant) =>
        AnyUploaded(combatant.OutgoingBuckets, BucketConfig.AllOutgoingRef)
        || AnyUploaded(combatant.IncomingBuckets, BucketConfig.AllIncomingRef);

    private static bool AnyUploaded(IReadOnlyDictionary<string, Bucket> side, string referenceBucket)
    {
        if (!side.TryGetValue(referenceBucket, out var reference))
            return false;
        foreach (var swing in reference.All.Swings)
        {
            if (IsUploaded(swing.Category))
                return true;
        }
        return false;
    }

    /// <summary>The site's Cures tab reads a cure row's damage as the number
    /// of effects removed, hits as times cast and maxhit as the most at
    /// once, and ACT books each cure line as an amount of 1. The engine's
    /// cure swing carries 0 (a cure has no amount), so the row uploaded as
    /// all zeroes. The amount is set on a COPY for the payload row only —
    /// the encounter's swing, the Cure/Dispel bucket and its damage_types
    /// row keep 0.</summary>
    private static Swing AsOneEffectRemoved(Swing cure) =>
        cure.Damage.Number == DamageValue.NoDamageNumber ? cure with { Damage = OneEffect } : cure;

    private static readonly DamageValue OneEffect = new(1);

    /// <summary>The row for one ability in one category, or null when it
    /// recorded no activity: the ACT plugin's rule (no damage, no hits, no
    /// misses, nothing blocked). In practice that is the "Killing"
    /// bookkeeping row — death-coded swings only — which sat on the site's
    /// Damage tab as a row of zeroes; kills and deaths reach the site as
    /// combatant columns. An ability that only ever missed, was only ever
    /// avoided, or landed for no damage keeps its row.</summary>
    private static PayloadAttackType? AttackRow(
        string attacker, SwingCategory category, AbilityStats stats, double encounterSeconds)
    {
        if (stats.Damage == 0 && stats.Hits == 0 && stats.Misses == 0 && stats.Avoids == 0)
            return null;
        return new PayloadAttackType
        {
            Attacker = attacker,
            Type = stats.Name,
            SwingType = (int)category,
            Damage = stats.Damage,
            EncDps = Rate(stats.Damage, encounterSeconds),
            // ACT's per-attacktype DPS: damage over the type's own
            // first-to-last-swing window, min 1 s (a single-swing
            // window is 0 s and would divide away the damage).
            Dps = Rate(stats.Damage, Math.Max(1, (stats.EndTime - stats.StartTime).TotalSeconds)),
            CritPerc = stats.Hits > 0 ? Math.Round(100.0 * stats.CritHits / stats.Hits, 1) : 0,
            Median = stats.Median,
            MinHit = stats.MinHit,
            MaxHit = stats.MaxHit,
            Resist = ResistOf(category, stats.Swings),
            Hits = stats.Hits,
            CritHits = stats.CritHits,
            Blocked = stats.Avoids,
            Misses = stats.Misses,
            Swings = stats.SwingCount,
        };
    }

    /// <summary>ACT's type text for a row (the wire's <c>resist</c>), or
    /// null when the row's swings do not agree on one. The site keys one
    /// row per (attacker, swingtype, name), so a heal ability that also
    /// warded under the same name is ONE row whose amount mixes both:
    /// "Hitpoints" would mislabel the absorbed part and "Absorption" would
    /// mark the whole row as a ward, so it claims neither and the site
    /// shows a dash. Same rule for a name that both raised and lowered
    /// threat, and for a damage row whose landed hits span damage types
    /// (dual wield, an infused weapon). Avoids, no-damage hits and the kill
    /// marker never name a damage type — the parser files those under its
    /// own markers — so only swings that dealt damage are consulted.</summary>
    private static string? ResistOf(SwingCategory category, IReadOnlyList<Swing> swings)
    {
        string? shared = null;
        foreach (var swing in swings)
        {
            var text = category switch
            {
                SwingCategory.Healing => swing.DamageType == EnglishGrammar.WardAbsorbType ? "Absorption" : "Hitpoints",
                // The amount is positive either way; the grammar's marker
                // is the only thing that tells a reduction from an increase.
                SwingCategory.Threat => swing.DamageType == EnglishGrammar.ThreatReduceType ? "Decrease" : "Increase",
                SwingCategory.PowerReplenish => "Power",
                SwingCategory.Melee or SwingCategory.NonMelee =>
                    swing.Damage.Number > 0 && swing.DamageType.Length > 0 ? swing.DamageType : null,
                _ => null,
            };
            if (text is null)
                continue;
            if (shared is null)
                shared = text;
            else if (!string.Equals(shared, text, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return shared;
    }

    /// <summary>Stable 8-hex content id: same fight → same id → server-side
    /// dedupe on re-upload (UNIQUE(world, act_encid)). The seed reads the
    /// ENCOUNTER only — its combatant count is every combatant of the
    /// fight, including one whose row <see cref="Build"/> leaves out — so
    /// what the payload carries can change without moving the id.</summary>
    public static string ComputeEncId(Encounter encounter)
    {
        var seed = new StringBuilder()
            .Append(encounter.OwnerName).Append('|')
            .Append(encounter.Zone).Append('|')
            .Append(encounter.Title).Append('|')
            .Append(encounter.StartTime.ToUnixTimeSeconds()).Append('|')
            .Append(encounter.Combatants.Count);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString()));
        return Convert.ToHexStringLower(hash)[..8];
    }

    private static string Ts(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static double Rate(long amount, double seconds) =>
        seconds > 0 ? Math.Round(amount / seconds, 2) : 0;

    private static double ToHit(AbilityStats? stats)
    {
        if (stats is null || stats.SwingCount == 0)
            return 0;
        return Math.Round(100.0 * stats.Hits / stats.SwingCount, 2);
    }
}
