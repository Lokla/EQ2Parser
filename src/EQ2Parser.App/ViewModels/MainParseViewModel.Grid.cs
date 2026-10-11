using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace EQ2Parser.App.ViewModels;

/// <summary>The combatant grid: per-tick snapshot of the resolved
/// fight into ally/pet/enemy rows, sorting, and in-place row apply.</summary>
public sealed partial class MainParseViewModel
{
    // ── Grid ────────────────────────────────────────────────────────────────

    private sealed record RowData(
        string Key, string Name, string Cls, System.Windows.Media.Brush Brush, bool IsPet,
        double Seconds, long Damage, double Dps, double Hps, long Taken, int Deaths,
        ExtStats? Ext = null);

    /// <summary>Stats behind the opt-in columns — computed from the swing
    /// buckets only while one of those columns is visible.</summary>
    private sealed record ExtStats(
        long Healed, int CritHeals, int Cures, long PowerDrain, long PowerRep,
        int Swings, int Hits, int Crits, int Misses, int Avoids, long HealsTaken);

    private ExtStats? ExtOf(Combatant combatant)
    {
        if (!Columns.Extended)
            return null;
        var damage = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.OutgoingDamage)
            ?.Abilities.GetValueOrDefault(Bucket.AllAbility);
        var heals = combatant.OutgoingBuckets.GetValueOrDefault(BucketConfig.HealedOut)
            ?.Abilities.GetValueOrDefault(Bucket.AllAbility);
        return new ExtStats(
            combatant.Healed, heals?.CritHits ?? 0, combatant.CureDispels,
            combatant.PowerDamage, combatant.PowerReplenish,
            damage?.SwingCount ?? 0, damage?.Hits ?? 0, damage?.CritHits ?? 0,
            damage?.Misses ?? 0, damage?.Avoids ?? 0, combatant.HealsTaken);
    }

    private static ExtStats? SumExt(ExtStats? a, ExtStats? b) =>
        a is null ? b : b is null ? a : new ExtStats(
            a.Healed + b.Healed, a.CritHeals + b.CritHeals, a.Cures + b.Cures,
            a.PowerDrain + b.PowerDrain, a.PowerRep + b.PowerRep,
            a.Swings + b.Swings, a.Hits + b.Hits, a.Crits + b.Crits,
            a.Misses + b.Misses, a.Avoids + b.Avoids, a.HealsTaken + b.HealsTaken);

    private static double ToHitOf(RowData row) =>
        row.Ext is { Swings: > 0 } ext ? (double)ext.Hits / ext.Swings : 0;

    private static double CritPctOf(RowData row) =>
        row.Ext is { Hits: > 0 } ext ? (double)ext.Crits / ext.Hits : 0;

    private void RefreshGrid()
    {
        List<RowData> allies = [];
        List<RowData> pets = [];
        List<RowData> enemies = [];
        string breadcrumb;
        var live = false;
        DetailData? detail = null;
        ChartData? chart = null;
        List<ZoneFightRow>? zoneRows = null;
        object? resolvedFight;
        List<PerspectiveOption> perspectiveOptions;
        PerspectiveOption? perspectiveChosen;

        lock (manager.Sync)
        {
            (perspectiveOptions, perspectiveChosen) = ComputePerspectives();
            _effectivePerspective = perspectiveChosen;
            var fight = ResolveFight();
            if (fight is null)
            {
                ApplyPerspectives(perspectiveOptions, perspectiveChosen);
                return;
            }
            resolvedFight = fight;
            // While a report overlays the drill, the drill must not keep
            // driving the view (it was overwriting the report's title and
            // un-hiding its tables every tick).
            if (DetailOpen && _detailKey is not null && !ReportLevel && fight is not ZoneFights)
            {
                var detailSig = (fight, _detailKey, _detailBucket, _detailAbility, SwingLevel, LogLevel, DetailVersion(fight));
                if (detailSig != _detailSig)
                {
                    _detailSig = detailSig;
                    detail = SnapshotDetail(fight, _detailKey);
                }
            }

            switch (fight)
            {
                case Encounter encounter:
                    live = encounter.Active;
                    breadcrumb = Describe(encounter.Zone, encounter.Title, encounter.Duration, encounter.EncDps, live);
                    SnapshotFight(encounter, allies, pets, enemies);
                    break;
                case CorrelatedEncounter merged:
                    breadcrumb = Describe(merged.Zone, merged.Title, merged.Duration, merged.EncDps, live: false);
                    SnapshotFight(merged, allies, pets, enemies);
                    break;
                case AggregateFights aggregate:
                    {
                        SnapshotAggregate(aggregate, allies, pets, enemies);
                        var allyDamage = allies.Sum(r => r.Damage) + pets.Sum(r => r.Damage);
                        breadcrumb = Describe(
                            aggregate.Zone,
                            Loc.Format("MainVm_AggregateTitle", LocalizeAggregateLabel(aggregate.Label), aggregate.Fights.Count),
                            aggregate.Duration, allyDamage / ((IFightView)aggregate).DisplaySeconds, live: false);
                        break;
                    }
                case ZoneFights zone:
                    {
                        var zoneFights = ResolveZoneFights(zone);
                        breadcrumb = Loc.Format("MainVm_ZoneBreadcrumb", zone.Zone, zoneFights.Count, FmtSpan(SumDuration(zoneFights)));
                        // Correlator.Version catches in-place merges; Count still
                        // matters because the Bosses-only filter changes the list
                        // without touching the correlator.
                        var sig = (zone.GroupKey, zoneFights.Count, manager.Correlator.Version);
                        if (sig != _zoneSummarySig)
                        {
                            _zoneSummarySig = sig;
                            zoneRows = BuildZoneSummary(zoneFights);
                        }
                        break;
                    }
                default:
                    return;
            }

            if (fight is not ZoneFights)
                chart = MaybeSnapshotChart(resolvedFight, allies);
        }

        ApplyPerspectives(perspectiveOptions, perspectiveChosen);
        Breadcrumb = breadcrumb;
        InCombat = live;
        ZoneSummaryOpen = resolvedFight is ZoneFights;
        if (zoneRows is not null)
            ZoneSummaryRows.ReplaceAll(zoneRows);
        PetHeader = Loc.Format("MainVm_PetsHeader", pets.Count);
        EnemyHeader = Loc.Format("MainVm_EnemiesHeader", enemies.Count);
        if (chart is not null)
            ApplyChart(chart);
        Apply(AllyRows, Sort(allies));
        Apply(PetRows, Sort(pets));
        Apply(EnemyRows, Sort(enemies));

        if (detail is not null)
        {
            DetailTitle = LogLevel ? Loc.Format("MainVm_DetailTitleLog", detail.Title) : detail.Title;
            DrillNameHeader = detail.NameHeader;
            SwingLevel = detail.IsSwingLevel;
            if (detail.Table is not null)
                ApplyAbilityRows(DrillRows, detail.Table, sort: detail.SortTable, bars: detail.Bars);
            // Swings == null at swing level means "unchanged — keep rows".
            if (detail.Swings is not null)
                SwingRows.ReplaceAll(detail.Swings);
            if (detail.Chart is { } drillChart && !LogLevel)
                ApplyDrillChart(drillChart);
        }
    }

    private object? ResolveFight()
    {
        if (!FollowLive && _pinnedFight is not null)
        {
            // A specific witness of a merged fight is selected — render that
            // mirror's own view instead of the combined one.
            if (_pinnedFight is CorrelatedEncounter merged
                && _effectivePerspective?.SourceId is { } mirrorId
                && merged.Sources.FirstOrDefault(s => s.SourceId == mirrorId) is { } mirror)
                return mirror;
            return _pinnedFight;
        }
        // Live: the chosen perspective's fight first, else first in combat.
        if (_effectivePerspective?.SourceId is { } liveId)
        {
            foreach (var source in manager.Sources)
            {
                if (source.Engine.ActiveEncounter is { } chosen && chosen.SourceId == liveId)
                    return chosen;
            }
        }
        foreach (var source in manager.Sources)
        {
            if (source.Engine.ActiveEncounter is { } active)
                return active;
        }
        if (manager.Correlator.History.Count > 0)
        {
            var last = manager.Correlator.History[^1];
            // The follow-live fallback honours a witness pick too.
            if (_effectivePerspective?.SourceId is { } lastId
                && last.Sources.FirstOrDefault(s => s.SourceId == lastId) is { } lastMirror)
                return lastMirror;
            return last;
        }
        return null;
    }

    // ── Perspective plumbing ────────────────────────────────────────────────

    /// <summary>Options for the CURRENT context (live vs pinned merged
    /// fight) + the effective choice for this tick. Runs under the sync
    /// lock. Selection preference: the user's current pick when still
    /// valid, else the persisted live owner (live only), else the default
    /// (first live source / Combined).</summary>
    private (List<PerspectiveOption> Options, PerspectiveOption? Chosen) ComputePerspectives()
    {
        if (FollowLive || _pinnedFight is null)
        {
            List<Encounter> active = [];
            foreach (var source in manager.Sources)
            {
                if (source.Engine.ActiveEncounter is { } encounter)
                    active.Add(encounter);
            }
            // Nothing live: follow-live falls back to the last finished
            // fight — keep the witness dropdown available for it.
            if (active.Count == 0
                && manager.Correlator.History.Count > 0
                && manager.Correlator.History[^1] is { Sources.Count: > 1 } last)
                return MergedOptions(last);
            if (active.Count < 2)
                return ([], null);
            var options = OptionsFor(active);
            PerspectiveOption? chosen = null;
            if (SelectedPerspective?.SourceId is { } currentId)
                chosen = options.FirstOrDefault(o => o.SourceId == currentId);
            if (chosen is null && manager.Settings.LivePerspectiveOwner is { Length: > 0 } preferred)
            {
                var match = active.FirstOrDefault(e =>
                    string.Equals(e.OwnerName, preferred, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    chosen = options.First(o => o.SourceId == match.SourceId);
            }
            return (options, chosen ?? options[0]);
        }
        if (_pinnedFight is CorrelatedEncounter merged && merged.Sources.Count > 1)
            return MergedOptions(merged);
        return ([], null);
    }

    /// <summary>Combined + one option per witness, keeping the user's pick
    /// when it still applies (a live pick carries over to the same fight's
    /// finished view — the SourceIds match).</summary>
    private (List<PerspectiveOption> Options, PerspectiveOption? Chosen) MergedOptions(CorrelatedEncounter merged)
    {
        List<PerspectiveOption> options = [new(Loc.Get("MainVm_PerspectiveCombined"), null), .. OptionsFor(merged.Sources)];
        var chosen = SelectedPerspective is { } current && options.Contains(current)
            ? current
            : options[0];
        return (options, chosen);
    }

    /// <summary>Owner-name labels, disambiguated with the log's server
    /// folder when two sources share a character name.</summary>
    private static List<PerspectiveOption> OptionsFor(IReadOnlyList<Encounter> mirrors)
    {
        List<PerspectiveOption> options = new(mirrors.Count);
        foreach (var mirror in mirrors)
        {
            var label = mirror.OwnerName;
            if (mirrors.Count(m => string.Equals(m.OwnerName, mirror.OwnerName, StringComparison.OrdinalIgnoreCase)) > 1)
            {
                var server = Core.Upload.LogPaths.ParseServerName(mirror.SourceId);
                if (server.Length > 0)
                    label = $"{mirror.OwnerName} — {server}";
            }
            options.Add(new PerspectiveOption(label, mirror.SourceId));
        }
        return options;
    }

    /// <summary>Sync the dropdown to this tick's computed options/choice —
    /// only touching the collection when the option SET actually changed,
    /// so an open dropdown isn't yanked shut at 10 Hz.</summary>
    private void ApplyPerspectives(List<PerspectiveOption> options, PerspectiveOption? chosen)
    {
        _applyingPerspectives = true;
        try
        {
            var sig = string.Join("", options.Select(o => $"{o.Label}{o.SourceId}"));
            if (sig != _perspectiveSig)
            {
                _perspectiveSig = sig;
                Perspectives.Clear();
                foreach (var option in options)
                    Perspectives.Add(option);
                PerspectiveVisible = options.Count > 1;
            }
            if (!Equals(SelectedPerspective, chosen))
                SelectedPerspective = chosen;
        }
        finally
        {
            _applyingPerspectives = false;
        }
    }

    /// <summary>Rollup labels ("All" / "All Bosses") are stored English —
    /// they feed clipboard/report output, which deliberately stays English —
    /// so the on-screen breadcrumb localizes them at display time.</summary>
    private static string LocalizeAggregateLabel(string label) => label switch
    {
        "All" => Loc.Get("MainVm_LabelAll"),
        "All Bosses" => Loc.Get("MainVm_LabelAllBosses"),
        SelectionLabel => Loc.Get("MainVm_LabelSelected"),
        _ => label,
    };

    private static string Describe(string zone, string title, TimeSpan duration, double dps, bool live)
    {
        var shownTitle = title == Encounter.PlaceholderTitle && live ? Loc.Get("MainVm_CombatInProgress") : title;
        var zonePart = string.IsNullOrEmpty(zone) ? "" : $"{zone}  |  ";
        return Loc.Format("MainVm_FightBreadcrumb", zonePart, shownTitle, duration.TotalSeconds, CombatantRow.Compact(dps));
    }

    /// <summary>One snapshot for both single-log and merged fights (the two
    /// copies had drifted: the live path divided by REAL seconds while the
    /// merged path clamped to ≥1, showing different DPS rules on one screen
    /// for zero-duration fights). <see cref="IFightView.DisplaySeconds"/> is
    /// now the single display rule.</summary>
    private void SnapshotFight(IFightView fight, List<RowData> allies, List<RowData> pets, List<RowData> enemies)
    {
        if (fight.ClassificationSource is not { } source)
            return;
        var tags = manager.Classifier.Classify(source);
        // One seconds derivation per tick, not one per combatant.
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
            var row = BuildRow(
                key, combatant.Name, tag,
                combatant.Duration.TotalSeconds, damage,
                damage / seconds, healed / seconds,
                taken, combatant.Deaths, ExtOf(combatant));
            BucketRow(tag, row, allies, pets, enemies);
        }
    }

    /// <summary>Combined stats over a zone rollup — sums per combatant, with
    /// EncDPS/EncHPS over the COMBINED fight duration.
    /// The class/kind tag is taken from the fight with the strongest class
    /// evidence for that combatant.</summary>
    private void SnapshotAggregate(AggregateFights aggregate, List<RowData> allies, List<RowData> pets, List<RowData> enemies)
    {
        var totalSeconds = ((IFightView)aggregate).DisplaySeconds;
        var acc = new Dictionary<string, (string Name, CombatantTag Tag, double Seconds, long Damage, long Healed, long Taken, int Deaths, ExtStats? Ext)>(StringComparer.Ordinal);

        foreach (var fight in aggregate.Fights)
        {
            var tags = manager.Classifier.Classify(fight.Primary);
            foreach (var (key, entry) in fight.MergedCombatants)
            {
                var combatant = entry.Combatant;
                if (!tags.TryGetValue(key, out var tag))
                    continue;
                if (tag.Kind is CombatantKind.System or CombatantKind.Bystander)
                    continue;
                if (combatant.Damage <= 0 && combatant.Healed <= 0 && combatant.DamageTaken <= 0)
                    continue;
                if (acc.TryGetValue(key, out var existing))
                {
                    var bestTag = tag.Class.MappedAbilities > existing.Tag.Class.MappedAbilities ? tag : existing.Tag;
                    acc[key] = (existing.Name, bestTag,
                        existing.Seconds + combatant.Duration.TotalSeconds,
                        existing.Damage + combatant.Damage,
                        existing.Healed + combatant.Healed,
                        existing.Taken + combatant.DamageTaken,
                        existing.Deaths + combatant.Deaths,
                        SumExt(existing.Ext, ExtOf(combatant)));
                }
                else
                {
                    acc[key] = (combatant.Name, tag,
                        combatant.Duration.TotalSeconds, combatant.Damage,
                        combatant.Healed, combatant.DamageTaken, combatant.Deaths,
                        ExtOf(combatant));
                }
            }
        }

        foreach (var (key, entry) in acc)
        {
            var row = BuildRow(
                key, entry.Name, entry.Tag,
                entry.Seconds, entry.Damage,
                entry.Damage / totalSeconds, entry.Healed / totalSeconds,
                entry.Taken, entry.Deaths, entry.Ext);
            BucketRow(entry.Tag, row, allies, pets, enemies);
        }
    }

    private static void BucketRow(CombatantTag tag, RowData row, List<RowData> allies, List<RowData> pets, List<RowData> enemies)
    {
        var target = tag.Kind switch
        {
            CombatantKind.Enemy => enemies,
            CombatantKind.Pet => pets,
            _ => allies,
        };
        target.Add(row);
    }

    private static RowData BuildRow(
        string key, string name, CombatantTag tag,
        double seconds, long damage, double dps, double hps, long taken, int deaths,
        ExtStats? ext = null)
    {
        var isPet = tag.Kind == CombatantKind.Pet;
        var cls = isPet
            ? (tag.PetOwner is not null ? Loc.Format("MainVm_PetClassOwned", tag.PetOwner) : Loc.Get("MainVm_PetClass"))
            : tag.Class.ClassName ?? "";
        var brush = isPet ? ClassColors.Neutral : ClassColors.For(tag.Class.ClassName);
        return new RowData(key, name, cls, brush, isPet, seconds, damage, dps, hps, taken, deaths, ext);
    }

    private List<RowData> Sort(List<RowData> rows)
    {
        Comparison<RowData> compare = SortColumn switch
        {
            "Name" => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            "Class" => (a, b) => string.Compare(a.Cls, b.Cls, StringComparison.OrdinalIgnoreCase),
            "Duration" => (a, b) => a.Seconds.CompareTo(b.Seconds),
            "Dps" => (a, b) => a.Dps.CompareTo(b.Dps),
            "Hps" => (a, b) => a.Hps.CompareTo(b.Hps),
            "Taken" => (a, b) => a.Taken.CompareTo(b.Taken),
            "Deaths" => (a, b) => a.Deaths.CompareTo(b.Deaths),
            "Heals" => (a, b) => (a.Ext?.Healed ?? 0).CompareTo(b.Ext?.Healed ?? 0),
            "CritHeals" => (a, b) => (a.Ext?.CritHeals ?? 0).CompareTo(b.Ext?.CritHeals ?? 0),
            "Cures" => (a, b) => (a.Ext?.Cures ?? 0).CompareTo(b.Ext?.Cures ?? 0),
            "PowerDrain" => (a, b) => (a.Ext?.PowerDrain ?? 0).CompareTo(b.Ext?.PowerDrain ?? 0),
            "PowerRep" => (a, b) => (a.Ext?.PowerRep ?? 0).CompareTo(b.Ext?.PowerRep ?? 0),
            "Swings" => (a, b) => (a.Ext?.Swings ?? 0).CompareTo(b.Ext?.Swings ?? 0),
            "Hits" => (a, b) => (a.Ext?.Hits ?? 0).CompareTo(b.Ext?.Hits ?? 0),
            "Crits" => (a, b) => (a.Ext?.Crits ?? 0).CompareTo(b.Ext?.Crits ?? 0),
            "Misses" => (a, b) => (a.Ext?.Misses ?? 0).CompareTo(b.Ext?.Misses ?? 0),
            "Avoids" => (a, b) => (a.Ext?.Avoids ?? 0).CompareTo(b.Ext?.Avoids ?? 0),
            "ToHit" => (a, b) => ToHitOf(a).CompareTo(ToHitOf(b)),
            "CritPct" => (a, b) => CritPctOf(a).CompareTo(CritPctOf(b)),
            "HealsTaken" => (a, b) => (a.Ext?.HealsTaken ?? 0).CompareTo(b.Ext?.HealsTaken ?? 0),
            _ => (a, b) => a.Damage.CompareTo(b.Damage),
        };
        rows.Sort(SortDescending ? (a, b) => compare(b, a) : compare);
        return rows;
    }

    private void Apply(ObservableCollection<CombatantRow> rows, List<RowData> snapshot)
    {
        // Row bars + % follow the sorted metric (HPS/Taken when sorted so;
        // damage otherwise), so re-sorting re-shapes the meter.
        var metric = ChartMetric;
        var top = snapshot.Count > 0 ? Math.Max(1.0, snapshot.Max(r => MetricOf(r, metric))) : 1;
        var total = Math.Max(1.0, snapshot.Sum(r => MetricOf(r, metric)));
        for (var i = 0; i < snapshot.Count; i++)
        {
            var data = snapshot[i];
            CombatantRow row;
            if (i < rows.Count)
            {
                row = rows[i];
            }
            else
            {
                row = new CombatantRow { Key = data.Key };
                rows.Add(row);
            }
            row.Key = data.Key;
            row.Name = data.Name;
            row.ClassName = data.Cls;
            row.ClassBrush = data.Brush;
            row.IsPet = data.IsPet;
            row.Duration = FmtSpan(TimeSpan.FromSeconds(data.Seconds));
            row.Damage = CombatantRow.Compact(data.Damage);
            row.Percent = $"{100.0 * MetricOf(data, metric) / total:F0}%";
            row.Dps = CombatantRow.Compact(data.Dps);
            row.Hps = data.Hps > 0 ? CombatantRow.Compact(data.Hps) : "";
            row.Taken = data.Taken > 0 ? CombatantRow.Compact(data.Taken) : "";
            row.Deaths = data.Deaths > 0 ? data.Deaths.ToString() : "";
            var ext = data.Ext;
            row.Heals = ext is { Healed: > 0 } ? CombatantRow.Compact(ext.Healed) : "";
            row.CritHeals = ext is { CritHeals: > 0 } ? ext.CritHeals.ToString("N0") : "";
            row.Cures = ext is { Cures: > 0 } ? ext.Cures.ToString("N0") : "";
            row.PowerDrain = ext is { PowerDrain: > 0 } ? CombatantRow.Compact(ext.PowerDrain) : "";
            row.PowerRep = ext is { PowerRep: > 0 } ? CombatantRow.Compact(ext.PowerRep) : "";
            row.Swings = ext is { Swings: > 0 } ? ext.Swings.ToString("N0") : "";
            row.Hits = ext is { Hits: > 0 } ? ext.Hits.ToString("N0") : "";
            row.Crits = ext is { Crits: > 0 } ? ext.Crits.ToString("N0") : "";
            row.Misses = ext is { Misses: > 0 } ? ext.Misses.ToString("N0") : "";
            row.Avoids = ext is { Avoids: > 0 } ? ext.Avoids.ToString("N0") : "";
            row.ToHit = ext is { Swings: > 0 } ? $"{100.0 * ext.Hits / ext.Swings:F1}%" : "";
            row.CritPct = ext is { Hits: > 0 } ? $"{100.0 * ext.Crits / ext.Hits:F1}%" : "";
            row.HealsTaken = ext is { HealsTaken: > 0 } ? CombatantRow.Compact(ext.HealsTaken) : "";
            row.BarFraction = MetricOf(data, metric) / top;
        }
        while (rows.Count > snapshot.Count)
            rows.RemoveAt(rows.Count - 1);
    }
}
