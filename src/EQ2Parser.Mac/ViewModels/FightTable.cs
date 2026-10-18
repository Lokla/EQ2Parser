using System.Globalization;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>One combatant's numbers for the grid.</summary>
public sealed record RowData(
    string Key, string Name, string ClassName, string ColorHex, CombatantKind Kind,
    double Seconds, long Damage, double Dps, double Hps, long Taken, int Deaths);

/// <summary>
/// The grid's numbers, by the WPF app's rule (MainParseViewModel.Grid
/// SnapshotFight): combatants tagged by the classifier, System and
/// Bystander rows left out, rows with no damage, healing or damage taken
/// left out, and every rate over <see cref="IFightView.DisplaySeconds"/>.
/// Call under the manager's lock.
/// </summary>
public static class FightTable
{
    public static (List<RowData> Allies, List<RowData> Enemies) Snapshot(IFightView fight, CombatantClassifier classifier)
    {
        List<RowData> allies = [];
        List<RowData> enemies = [];
        if (fight.ClassificationSource is not { } source)
            return (allies, enemies);
        var tags = classifier.Classify(source);
        var seconds = fight.DisplaySeconds;
        foreach (var (key, combatant) in fight.ViewCombatants)
        {
            if (!tags.TryGetValue(key, out var tag))
                continue;
            if (tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                continue;
            var damage = combatant.Damage;
            var healed = combatant.Healed;
            var taken = combatant.DamageTaken;
            if (damage <= 0 && healed <= 0 && taken <= 0)
                continue;
            var isPet = tag.Kind == CombatantKind.Pet;
            var cls = isPet
                ? (tag.PetOwner is not null ? $"pet of {tag.PetOwner}" : "pet")
                : tag.Class.ClassName ?? "";
            var row = new RowData(key, combatant.Name, cls, isPet ? ClassColors.Neutral : ClassColors.For(tag.Class.ClassName),
                tag.Kind, combatant.Duration.TotalSeconds, damage, damage / seconds, healed / seconds, taken, combatant.Deaths);
            (tag.Kind == CombatantKind.Enemy ? enemies : allies).Add(row);
        }
        allies.Sort((a, b) => b.Damage.CompareTo(a.Damage));
        enemies.Sort((a, b) => b.Damage.CompareTo(a.Damage));
        return (allies, enemies);
    }

    /// <summary>The WPF app's compact number (CombatantRow.Compact).</summary>
    public static string Compact(double value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000:0.##}M",
        >= 10_000 => $"{value / 1_000:0.#}K",
        _ => $"{value:N0}",
    };

    public static string Span(TimeSpan span) =>
        span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
}

/// <summary>The WPF app's archetype colours (ClassColors), as hex.</summary>
public static class ClassColors
{
    public const string Fighter = "#F87171";
    public const string Priest = "#4ADE80";
    public const string Scout = "#FBBF24";
    public const string Mage = "#93B4FF";
    public const string Neutral = "#8B90AB";

    private static readonly Dictionary<string, string> ByClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Guardian"] = Fighter,
        ["Berserker"] = Fighter,
        ["Paladin"] = Fighter,
        ["Shadowknight"] = Fighter,
        ["Monk"] = Fighter,
        ["Bruiser"] = Fighter,
        ["Templar"] = Priest,
        ["Inquisitor"] = Priest,
        ["Warden"] = Priest,
        ["Fury"] = Priest,
        ["Mystic"] = Priest,
        ["Defiler"] = Priest,
        ["Channeler"] = Priest,
        ["Ranger"] = Scout,
        ["Assassin"] = Scout,
        ["Swashbuckler"] = Scout,
        ["Brigand"] = Scout,
        ["Troubador"] = Scout,
        ["Dirge"] = Scout,
        ["Beastlord"] = Scout,
        ["Wizard"] = Mage,
        ["Warlock"] = Mage,
        ["Conjuror"] = Mage,
        ["Necromancer"] = Mage,
        ["Illusionist"] = Mage,
        ["Coercer"] = Mage,
    };

    public static string For(string? className) =>
        className is not null && ByClass.TryGetValue(className, out var hex) ? hex : Neutral;

    public static string ForOutcome(SuccessLevel level) => level switch
    {
        SuccessLevel.Win => "#4ADE80",
        SuccessLevel.Partial => "#FBBF24",
        SuccessLevel.Loss => "#F87171",
        _ => "#E2E4F0",
    };
}
