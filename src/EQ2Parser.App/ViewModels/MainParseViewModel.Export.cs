using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.App.Localization;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;
using EQ2Parser.Core.Export;

namespace EQ2Parser.App.ViewModels;

/// <summary>Discord-friendly clipboard export: a fenced monospace table
/// (summary line, header row, aligned data rows) with a user-configurable
/// column set. "Copy for Discord" uses the saved columns; "Custom export…"
/// opens the picker with a live preview.</summary>
public sealed partial class MainParseViewModel
{
    /// <summary>One selectable export column: settings key, checkbox label,
    /// table header, alignment, and the cell renderer.</summary>
    public sealed record ExportColumnDef(
        string Key, string Label, string Header, bool Right, Func<ExportRow, string> Value);

    /// <summary>Raw per-ally values, computed straight from the fight —
    /// independent of which grid columns happen to be visible.</summary>
    public sealed record ExportRow(
        string Name, string Cls, double Seconds, long Damage, double Share,
        double Dps, double Hps, long Taken, int Deaths,
        long Healed, int CritHeals, int Cures, long PowerDrain, long PowerRep,
        int Swings, int Hits, int Crits, int Misses, int Avoids, long HealsTaken);

    /// <summary>Every offerable column, in display order. Name is always
    /// the first column and not part of the selectable set.</summary>
    public static IReadOnlyList<ExportColumnDef> ExportColumnDefs { get; } =
    [
        new("Class", Loc.Get("Cols_Class"), Loc.Get("Main_ColCLASS"), Right: false, r => r.Cls),
        new("Time", Loc.Get("Cols_Time"), Loc.Get("Main_ColTIME"), Right: true, r => FmtSpan(TimeSpan.FromSeconds(r.Seconds))),
        new("Damage", Loc.Get("Cols_Damage"), Loc.Get("Main_ColDAMAGE"), Right: true, r => CombatantRow.Compact(r.Damage)),
        new("Percent", Loc.Get("Cols_Percent"), "%", Right: true, r => $"{r.Share:P1}"),
        new("Dps", Loc.Get("Cols_Dps"), Loc.Get("Main_ColENCDPS"), Right: true, r => CombatantRow.Compact(r.Dps)),
        new("Hps", Loc.Get("Cols_Hps"), Loc.Get("Main_ColENCHPS"), Right: true, r => CombatantRow.Compact(r.Hps)),
        new("Heals", Loc.Get("Cols_Heals"), Loc.Get("Main_ColHEALS"), Right: true, r => CombatantRow.Compact(r.Healed)),
        new("CritHeals", Loc.Get("Cols_CritHeals"), Loc.Get("Main_ColCRITHEAL"), Right: true, r => r.CritHeals.ToString()),
        new("Cures", Loc.Get("Cols_Cures"), Loc.Get("Main_ColCURES"), Right: true, r => r.Cures.ToString()),
        new("PowerDrain", Loc.Get("Cols_PowerDrain"), Loc.Get("Main_ColPWRDRN"), Right: true, r => CombatantRow.Compact(r.PowerDrain)),
        new("PowerRep", Loc.Get("Cols_PowerRep"), Loc.Get("Main_ColPWRREP"), Right: true, r => CombatantRow.Compact(r.PowerRep)),
        new("Swings", Loc.Get("Cols_Swings"), Loc.Get("Main_ColSWINGS"), Right: true, r => r.Swings.ToString()),
        new("Hits", Loc.Get("Cols_Hits"), Loc.Get("Main_ColHITS"), Right: true, r => r.Hits.ToString()),
        new("Crits", Loc.Get("Cols_Crits"), Loc.Get("Main_ColCRITS"), Right: true, r => r.Crits.ToString()),
        new("Misses", Loc.Get("Cols_Misses"), Loc.Get("Main_ColMISS"), Right: true, r => r.Misses.ToString()),
        new("Avoids", Loc.Get("Cols_Avoids"), Loc.Get("Main_ColAVOID"), Right: true, r => r.Avoids.ToString()),
        new("ToHit", Loc.Get("Cols_ToHit"), Loc.Get("Main_ColTOHITPCT"), Right: true, r => r.Swings > 0 ? $"{(double)r.Hits / r.Swings:P0}" : ""),
        new("CritPct", Loc.Get("Cols_CritPct"), Loc.Get("Main_ColCRITPCT"), Right: true, r => r.Hits > 0 ? $"{(double)r.Crits / r.Hits:P0}" : ""),
        new("Taken", Loc.Get("Cols_Taken"), Loc.Get("Main_ColTAKEN"), Right: true, r => CombatantRow.Compact(r.Taken)),
        new("HealsTaken", Loc.Get("Cols_HealsTaken"), Loc.Get("Main_ColHTAKEN"), Right: true, r => CombatantRow.Compact(r.HealsTaken)),
        new("Deaths", Loc.Get("Cols_Deaths"), Loc.Get("Main_ColDEATHS"), Right: true, r => r.Deaths.ToString()),
    ];

    private static readonly string[] DefaultExportKeys = ["Class", "Dps", "Hps", "Percent", "Deaths"];

    /// <summary>One export configuration: column keys, sort column (null =
    /// Damage), included archetypes (null/all-four = everyone, unknown
    /// classes included).</summary>
    public sealed record ExportSpec(
        IReadOnlyList<string> Columns, string? SortKey, IReadOnlyList<string>? Archetypes);

    /// <summary>The persisted working configuration — what "Copy for
    /// Discord" uses (unknown saved keys dropped).</summary>
    public ExportSpec CurrentExportSpec => new(
        (manager.Settings.ExportColumns ?? [.. DefaultExportKeys])
            .Where(k => ExportColumnDefs.Any(d => d.Key == k)).ToArray(),
        manager.Settings.ExportSortKey,
        manager.Settings.ExportArchetypes);

    public void SaveExportSpec(ExportSpec spec)
    {
        manager.Settings = manager.Settings with
        {
            ExportColumns = [.. spec.Columns],
            ExportSortKey = spec.SortKey,
            ExportArchetypes = spec.Archetypes is null ? null : [.. spec.Archetypes],
        };
        manager.Settings.Save();
    }

    // ── Presets ─────────────────────────────────────────────────────────────

    public IReadOnlyList<ExportPreset> ExportPresets => manager.Settings.ExportPresets ?? [];

    public void SaveExportPreset(string name, ExportSpec spec)
    {
        List<ExportPreset> presets = [.. ExportPresets.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))];
        presets.Add(new ExportPreset(name, [.. spec.Columns], spec.SortKey,
            spec.Archetypes is null ? null : [.. spec.Archetypes]));
        presets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        manager.Settings = manager.Settings with { ExportPresets = presets };
        manager.Settings.Save();
    }

    public void DeleteExportPreset(string name)
    {
        List<ExportPreset> presets = [.. ExportPresets.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))];
        manager.Settings = manager.Settings with { ExportPresets = presets.Count == 0 ? null : presets };
        manager.Settings.Save();
    }

    public static ExportSpec SpecOf(ExportPreset preset) => new(
        preset.Columns.Where(k => ExportColumnDefs.Any(d => d.Key == k)).ToArray(),
        preset.SortKey, preset.Archetypes);

    /// <summary>Quick-picker copy: a preset by name straight from the
    /// context menu, without opening the export window.</summary>
    public void CopyDiscordPreset(ParseNode? node, string presetName)
    {
        var preset = ExportPresets.FirstOrDefault(p =>
            string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            return;
        SetClipboard(BuildDiscordExport(WidenToSelection(node), SpecOf(preset)));
    }

    /// <summary>Sort order per column key; unknown/null = Damage.
    /// Name/Class ascend, numbers descend.</summary>
    private static IEnumerable<ExportRow> SortRows(IEnumerable<ExportRow> rows, string? sortKey) => sortKey switch
    {
        "Name" => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        "Class" => rows.OrderBy(r => r.Cls, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.Damage),
        "Time" => rows.OrderByDescending(r => r.Seconds),
        "Percent" => rows.OrderByDescending(r => r.Share),
        "Dps" => rows.OrderByDescending(r => r.Dps),
        "Hps" => rows.OrderByDescending(r => r.Hps),
        "Heals" => rows.OrderByDescending(r => r.Healed),
        "CritHeals" => rows.OrderByDescending(r => r.CritHeals),
        "Cures" => rows.OrderByDescending(r => r.Cures),
        "PowerDrain" => rows.OrderByDescending(r => r.PowerDrain),
        "PowerRep" => rows.OrderByDescending(r => r.PowerRep),
        "Swings" => rows.OrderByDescending(r => r.Swings),
        "Hits" => rows.OrderByDescending(r => r.Hits),
        "Crits" => rows.OrderByDescending(r => r.Crits),
        "Misses" => rows.OrderByDescending(r => r.Misses),
        "Avoids" => rows.OrderByDescending(r => r.Avoids),
        "ToHit" => rows.OrderByDescending(r => r.Swings > 0 ? (double)r.Hits / r.Swings : 0),
        "CritPct" => rows.OrderByDescending(r => r.Hits > 0 ? (double)r.Crits / r.Hits : 0),
        "Taken" => rows.OrderByDescending(r => r.Taken),
        "HealsTaken" => rows.OrderByDescending(r => r.HealsTaken),
        "Deaths" => rows.OrderByDescending(r => r.Deaths),
        _ => rows.OrderByDescending(r => r.Damage),
    };

    /// <summary>Null or all-four archetypes = no filter (unknown classes
    /// pass); an active filter admits only mapped, selected archetypes.</summary>
    private static bool PassesArchetypes(ExportRow row, IReadOnlyList<string>? archetypes)
    {
        if (archetypes is null || archetypes.Count == 0 || archetypes.Count >= Archetype.All.Count)
            return true;
        return Archetype.Of(row.Cls) is { } a && archetypes.Contains(a);
    }

    /// <summary>Build the Discord table for a tree node with the given
    /// column selection. Null when the node has nothing to export.</summary>
    public string? BuildDiscordExport(ParseNode? node, ExportSpec spec)
    {
        if (node is null)
            return null;
        lock (manager.Sync)
        {
            return node switch
            {
                { GroupFights: { Count: > 0 } group } => FightsTable(node.Title, group),
                { Fight: CorrelatedEncounter fight } => CombatantTable(fight, spec),
                { Fight: AggregateFights aggregate } => FightsTable($"{aggregate.Zone} — {aggregate.Label}", aggregate.Fights),
                { Fight: LiveFollow } => LiveTable(spec),
                _ => null,
            };
        }
    }

    [RelayCommand]
    private void CopyDiscord(ParseNode? node) =>
        SetClipboard(BuildDiscordExport(WidenToSelection(node), CurrentExportSpec));

    [RelayCommand]
    private void OpenExport(ParseNode? node)
    {
        node = WidenToSelection(node);
        if (node is null)
            return;
        new Views.ExportWindow(this, node) { Owner = System.Windows.Application.Current?.MainWindow }
            .ShowDialog();
    }

    /// <summary>One export table for every fight shape (the live and merged
    /// copies had drifted: the live one skipped the players-only filter and
    /// both hand-rolled the seconds clamp).</summary>
    private string CombatantTable(IFightView fight, ExportSpec spec)
    {
        if (fight.ClassificationSource is not { } source)
            return "";
        var tags = manager.Classifier.Classify(source);
        var seconds = fight.DisplaySeconds;
        var allies = fight.AllyCombatants
            .Where(kv => tags.TryGetValue(kv.Key, out var tag) && tag.Kind == CombatantKind.Player)
            .Select(kv => (Combatant: kv.Value, Cls: tags[kv.Key].Class.ClassName ?? ""));
        var summary = Loc.Format("Export_FightSummary",
            fight.Title, FmtSpan(fight.Duration), CombatantRow.Compact(fight.EncDps));
        return RenderTable(summary, allies, seconds, spec);
    }

    private string? LiveTable(ExportSpec spec)
    {
        foreach (var source in manager.Sources)
        {
            if (source.Engine.ActiveEncounter is { } encounter)
                return CombatantTable(encounter, spec);
        }
        return null;
    }

    private static string RenderTable(
        string summary, IEnumerable<(Combatant Combatant, string Cls)> allies,
        double seconds, ExportSpec spec)
    {
        List<ExportRow> rows = [];
        long total = 0;
        foreach (var (combatant, cls) in allies)
        {
            if (combatant.Damage <= 0 && combatant.Healed <= 0 && combatant.DamageTaken <= 0)
                continue;
            total += combatant.Damage;
            var damage = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.OutgoingDamage)
                ?.Abilities.GetValueOrDefault(Bucket.AllAbility);
            var heals = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.HealedOut)
                ?.Abilities.GetValueOrDefault(Bucket.AllAbility);
            rows.Add(new ExportRow(
                combatant.Name, cls, combatant.Duration.TotalSeconds,
                combatant.Damage, Share: 0,
                combatant.Damage / seconds, combatant.Healed / seconds,
                combatant.DamageTaken, combatant.Deaths,
                combatant.Healed, heals?.CritHits ?? 0, combatant.CureDispels,
                combatant.PowerDamage, combatant.PowerReplenish,
                damage?.SwingCount ?? 0, damage?.Hits ?? 0, damage?.CritHits ?? 0,
                damage?.Misses ?? 0, damage?.Avoids ?? 0, combatant.HealsTaken));
        }
        var ordered = SortRows(rows
                .Select(r => r with { Share = total > 0 ? (double)r.Damage / total : 0 })
                .Where(r => PassesArchetypes(r, spec.Archetypes)), spec.SortKey)
            .ToList();

        var defs = spec.Columns.Select(k => ExportColumnDefs.First(d => d.Key == k)).ToList();
        List<ExportColumn> columns = [new(Loc.Get("Main_ColNAME"), RightAlign: false)];
        columns.AddRange(defs.Select(d => new ExportColumn(d.Header, d.Right)));
        var cells = ordered
            .Select(r => new[] { r.Name }.Concat(defs.Select(d => d.Value(r))).ToArray())
            .ToList();
        return TableExport.BuildDiscord(summary, columns, cells, Loc.Get("Export_MoreRows"));
    }

    /// <summary>Zone/rollup nodes: one row per fight instead of combatants.</summary>
    private static string FightsTable(string label, IReadOnlyList<CorrelatedEncounter> fights)
    {
        List<ExportColumn> columns =
        [
            new(Loc.Get("Main_ColENCOUNTER"), RightAlign: false),
            new(Loc.Get("Main_ColTIME"), RightAlign: true),
            new(Loc.Get("Main_ColENCDPS"), RightAlign: true),
        ];
        var rows = fights
            .Select(f => new[] { f.Title, FmtSpan(f.Duration), CombatantRow.Compact(f.EncDps) })
            .ToList();
        var summary = Loc.Format("Export_ZoneSummary", label, fights.Count, FmtSpan(SumDuration(fights)));
        return TableExport.BuildDiscord(summary, columns, rows, Loc.Get("Export_MoreRows"));
    }
}
