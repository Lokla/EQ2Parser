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

/// <summary>A line of the depth-1 chart: a bucket's rate per time slot.</summary>
public sealed record ChartLine(string Name, string ColorHex, double[] Rates);

/// <summary>What the drill shows at its current depth: the table or the
/// swings, plus the chart — lines over time (depth 1) or the abilities'
/// shares (depth 2).</summary>
public sealed record DrillData(
    string Title, List<AbilityData>? Table, List<SwingData>? Swings,
    List<ChartLine>? Lines = null, double SlotSeconds = 0, List<(string Name, double Value)>? Slices = null);

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
        {
            var (lines, slotSeconds) = BucketLines(fight, instances);
            return new DrillData(who, Buckets(instances, seconds), null, lines, slotSeconds);
        }
        var isAuto = bucketName == BucketConfig.AutoAttackOut;
        if (ability is null)
        {
            var table = isAuto ? AutoGroups(instances, seconds) : Abilities(instances, bucketName, seconds);
            return new DrillData($"{who}  ›  {BucketLabel(bucketName)}", table, null, Slices: Slices(table));
        }
        return new DrillData($"{who}  ›  {BucketLabel(bucketName)}  ›  {ability}", null, Swings(instances, bucketName, ability, isAuto));
    }

    /// <summary>The depth-1 chart's buckets and colours (the WPF drill's).</summary>
    private static readonly (string Bucket, string Hex)[] LineBuckets =
    [
        (BucketConfig.AutoAttackOut, "#C8A96E"),
        (BucketConfig.SkillOut, "#93B4FF"),
        (BucketConfig.HealedOut, "#4ADE80"),
        (BucketConfig.PowerDrainOut, "#00E5FF"),
        (BucketConfig.PowerReplenishOut, "#22D3EE"),
        (BucketConfig.CureOut, "#E8BBFF"),
        (BucketConfig.ThreatOut, "#F87171"),
    ];

    /// <summary>Per-bucket rate over the fight: amounts summed into time
    /// slots of 2–30 s (about 60 slots a fight), divided by the slot length
    /// — the WPF drill chart's window and rule.</summary>
    private static (List<ChartLine>? Lines, double SlotSeconds) BucketLines(IFightView fight, List<Combatant> instances)
    {
        var duration = Math.Max(1, (fight.EndTime - fight.StartTime).TotalSeconds);
        var slotSeconds = Math.Clamp(Math.Ceiling(duration / 60), 2, 30);
        var slots = (int)(duration / slotSeconds) + 1;
        List<ChartLine> lines = [];
        foreach (var (bucketName, hex) in LineBuckets)
        {
            var rates = new double[slots];
            foreach (var combatant in instances)
            {
                if (combatant.OutgoingBuckets.GetValueOrDefault(bucketName) is not { } bucket)
                    continue;
                foreach (var swing in bucket.All.Swings)
                {
                    if (swing.Damage.Number <= 0)
                        continue;
                    var slot = (int)((swing.Time - fight.StartTime).TotalSeconds / slotSeconds);
                    if (slot >= 0 && slot < rates.Length)
                        rates[slot] += swing.Damage.Number;
                }
            }
            if (!rates.Any(r => r > 0))
                continue;
            for (var i = 0; i < rates.Length; i++)
                rates[i] /= slotSeconds;
            lines.Add(new ChartLine(BucketLabel(bucketName), hex, rates));
        }
        return (lines.Count > 0 ? lines : null, slotSeconds);
    }

    /// <summary>The abilities' shares: the eleven largest and the rest as
    /// "Other" (the headline "All" row left out).</summary>
    private static List<(string, double)>? Slices(List<AbilityData> table)
    {
        var ranked = table.Where(t => !t.IsDivider && t.Source != "all" && t.Total > 0).OrderByDescending(t => t.Total).ToList();
        List<(string, double)> slices = [.. ranked.Take(11).Select(t => (t.Name, (double)t.Total))];
        var rest = ranked.Skip(11).Sum(t => t.Total);
        if (rest > 0)
            slices.Add(("Other", rest));
        return slices.Count > 1 ? slices : null;
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
