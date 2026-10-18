using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>One row of an ability table. A divider row has Swings = -1.</summary>
public sealed record AbilityData(
    string Name, string Source, string Types, int Swings, int Hits, int Crits, long Max, long Total, double Dps,
    double? FreqSeconds = null)
{
    public bool IsDivider => Swings < 0;
}

/// <summary>One swing at the deepest level.</summary>
public sealed record SwingData(
    DateTimeOffset Time, string Attacker, string Victim, string Ability, string Damage, long Number,
    bool Critical, string Type, string Special);

/// <summary>What the drill shows at its current depth.</summary>
public sealed record DrillData(string Title, List<AbilityData>? Table, List<SwingData>? Swings);

/// <summary>
/// The combatant drill-down, by the WPF app's rules
/// (MainParseViewModel.Drill): depth 1 the combatant's buckets (outgoing,
/// then incoming), depth 2 the abilities in one bucket (the Auto-Attack
/// bucket grouped by attack kind instead), depth 3 the swings. Hits, crits,
/// max, totals and the use frequency are counted exactly as there; rates
/// are over the fight's <see cref="IFightView.DisplaySeconds"/>. Call under
/// the manager's lock.
/// </summary>
public static class Drill
{
    /// <summary>The canonical bucket order, outgoing then incoming.</summary>
    private static readonly string[] BucketOrder =
    [
        BucketConfig.AutoAttackOut, BucketConfig.SkillOut, BucketConfig.OutgoingDamage,
        BucketConfig.HealedOut, BucketConfig.PowerDrainOut, BucketConfig.PowerReplenishOut,
        BucketConfig.CureOut, BucketConfig.ThreatOut, BucketConfig.AllOutgoingRef,
        BucketConfig.IncomingDamage, BucketConfig.HealedInc, BucketConfig.PowerDrainInc,
        BucketConfig.PowerReplenishInc, BucketConfig.CureInc, BucketConfig.ThreatInc,
        BucketConfig.AllIncomingRef,
    ];

    /// <summary>Special-based grouping for the Auto-Attack bucket.</summary>
    private static readonly string[] AutoAttackGroups = ["All", "Normal", "Multi Attack", "Flurry", "AoE Attack"];

    private const int MaxSwingRows = 2000;

    public static DrillData? Snapshot(IFightView fight, string key, string? bucketName, string? ability, CombatantClassifier classifier)
    {
        List<Combatant> instances = fight switch
        {
            Encounter e => e.Combatants.TryGetValue(key, out var c) ? [c] : [],
            CorrelatedEncounter m => m.MergedCombatants.TryGetValue(key, out var mc) ? [mc.Combatant] : [],
            _ => [],
        };
        if (instances.Count == 0)
            return null;
        var detection = classifier.Identifier.Detect(instances[0]);
        var who = instances[0].Name + (detection.ClassName is { } cls ? $" · {cls}" : "");
        var seconds = fight.DisplaySeconds;

        if (bucketName is null)
            return new DrillData(who, Buckets(instances, seconds), null);
        var isAuto = bucketName == BucketConfig.AutoAttackOut;
        if (ability is null)
        {
            return new DrillData($"{who}  ›  {BucketLabel(bucketName)}",
                isAuto ? AutoGroups(instances, seconds) : Abilities(instances, bucketName, seconds), null);
        }
        return new DrillData($"{who}  ›  {BucketLabel(bucketName)}  ›  {ability}", null, Swings(instances, bucketName, ability, isAuto));
    }

    public static string BucketLabel(string bucket) => bucket.Replace(" (Out)", "", StringComparison.Ordinal);

    private static List<AbilityData> Buckets(List<Combatant> instances, double seconds)
    {
        List<AbilityData> table = [];
        string? divider = "OUTGOING";
        foreach (var name in BucketOrder)
        {
            if (name == BucketConfig.IncomingDamage)
                divider = "INCOMING";
            var acc = new AbilityAcc();
            foreach (var combatant in instances)
            {
                if (FindBucket(combatant, name) is not { } bucket)
                    continue;
                var all = bucket.All;
                acc.Swings += all.SwingCount;
                acc.Hits += all.Hits;
                acc.Crits += all.CritHits;
                acc.Max = Math.Max(acc.Max, all.MaxHit);
                acc.Total += all.Damage;
            }
            if (acc.Swings <= 0)
                continue;
            if (divider is not null)
            {
                table.Add(new AbilityData(divider, "divider", "", -1, 0, 0, 0, 0, 0));
                divider = null;
            }
            table.Add(acc.ToData(name, "", seconds));
        }
        return table;
    }

    private static List<AbilityData> AutoGroups(List<Combatant> instances, double seconds)
    {
        List<AbilityData> table = [];
        foreach (var group in AutoAttackGroups)
        {
            var acc = new AbilityAcc();
            foreach (var combatant in instances)
            {
                if (FindBucket(combatant, BucketConfig.AutoAttackOut) is not { } bucket)
                    continue;
                foreach (var swing in bucket.All.Swings)
                {
                    if (swing.Ability != Combatant.KillingAbility && InAutoGroup(swing, group))
                        acc.AddSwing(swing);
                }
            }
            if (acc.Swings > 0)
                table.Add(acc.ToData(group, group == "All" ? "all" : "", seconds));
        }
        return table;
    }

    private static List<AbilityData> Abilities(List<Combatant> instances, string bucketName, double seconds)
    {
        var abilities = new Dictionary<string, AbilityAcc>(StringComparer.Ordinal);
        foreach (var combatant in instances)
        {
            if (FindBucket(combatant, bucketName) is not { } bucket)
                continue;
            foreach (var (name, stats) in bucket.Abilities)
            {
                if (name is Bucket.AllAbility or Combatant.KillingAbility)
                    continue;
                if (!abilities.TryGetValue(name, out var acc))
                    abilities[name] = acc = new AbilityAcc();
                foreach (var swing in stats.Swings)
                    acc.AddSwing(swing);
            }
        }
        List<AbilityData> table = [.. abilities
            .Select(kv => kv.Value.ToData(kv.Key, "", seconds))
            .OrderByDescending(a => a.Total)
            .ThenByDescending(a => a.Swings)];
        // Headline row: the whole bucket at a glance; it drills into every swing.
        if (table.Count > 1)
        {
            table.Insert(0, new AbilityData(
                Bucket.AllAbility, "all", "",
                table.Sum(t => t.Swings), table.Sum(t => t.Hits), table.Sum(t => t.Crits),
                table.Max(t => t.Max), table.Sum(t => t.Total), table.Sum(t => t.Total) / seconds));
        }
        return table;
    }

    private static List<SwingData> Swings(List<Combatant> instances, string bucketName, string ability, bool isAuto)
    {
        List<Swing> collected = [];
        foreach (var combatant in instances)
        {
            if (FindBucket(combatant, bucketName) is not { } bucket)
                continue;
            if (isAuto)
            {
                collected.AddRange(bucket.All.Swings.Where(s => s.Ability != Combatant.KillingAbility && InAutoGroup(s, ability)));
            }
            else if (bucket.Abilities.TryGetValue(ability, out var stats))
            {
                collected.AddRange(stats.Swings);
            }
        }
        return [.. collected
            .OrderBy(s => s.Time).ThenBy(s => s.TimeSorter)
            .Take(MaxSwingRows)
            .Select(s => new SwingData(s.Time, s.Attacker, s.Victim, s.Ability, s.Damage.ToString(), s.Damage.Number,
                s.Critical, s.DamageType, s.Special == "None" ? "" : s.Special))];
    }

    private static bool InAutoGroup(Swing swing, string group) => group switch
    {
        "All" => true,
        "Normal" => swing.Special == "None",
        _ => swing.Special == group,
    };

    private static bool IsIncoming(string name) =>
        name is BucketConfig.IncomingDamage or BucketConfig.AllIncomingRef || name.EndsWith("(Inc)", StringComparison.Ordinal);

    private static Bucket? FindBucket(Combatant combatant, string name) =>
        IsIncoming(name)
            ? combatant.IncomingBuckets.GetValueOrDefault(name)
            : combatant.OutgoingBuckets.GetValueOrDefault(name);

    /// <summary>The WPF drill's accumulator: hits, crits, max and total from
    /// the swings, and the mean seconds between uses.</summary>
    private sealed class AbilityAcc
    {
        public int Swings;
        public int Hits;
        public int Crits;
        public long Max;
        public long Total;
        private readonly SortedSet<string> _types = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(double Time, string Victim)> _uses = [];

        public void AddSwing(Swing swing)
        {
            Swings++;
            if (swing.Damage.Number >= 0)
            {
                Hits++;
                if (swing.Critical)
                    Crits++;
                Max = Math.Max(Max, swing.Damage.Number);
                Total += Math.Max(0, swing.Damage.Number);
            }
            if (swing.DamageType is not ("" or "avoided" or "death" or "none" or "heal"))
                _types.Add(swing.DamageType);
            _uses.Add((swing.Time.ToUnixTimeMilliseconds() / 1000.0, swing.Victim));
        }

        /// <summary>Mean seconds between USES: hits on distinct victims
        /// within 2 s are one use (an AoE), the same victim again is a new
        /// one; gaps over 5 minutes are left out. Null below two uses.</summary>
        private double? Frequency()
        {
            if (_uses.Count < 2)
                return null;
            _uses.Sort((a, b) => a.Time.CompareTo(b.Time));
            List<double> gaps = [];
            var volleyStart = _uses[0].Time;
            HashSet<string> victims = new(StringComparer.OrdinalIgnoreCase) { _uses[0].Victim };
            foreach (var (time, victim) in _uses.Skip(1))
            {
                if (time - volleyStart <= 2 && victims.Add(victim))
                    continue;
                var gap = time - volleyStart;
                if (gap is > 0 and <= 300)
                    gaps.Add(gap);
                volleyStart = time;
                victims.Clear();
                victims.Add(victim);
            }
            return gaps.Count > 0 ? gaps.Average() : null;
        }

        public AbilityData ToData(string label, string source, double seconds) =>
            new(label, source, string.Join(", ", _types), Swings, Hits, Crits, Max, Total, Total / seconds, Frequency());
    }
}
