using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Analysis;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Logs;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace EQ2Parser.App.ViewModels;

/// <summary>Context-menu commands: copy/export, view log, death and
/// avoidance entry points, combatant lookup.</summary>
public sealed partial class MainParseViewModel
{
    // ── Context-menu commands ───────────────────────────────────────────────

    private static void SetClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // Clipboard briefly owned by another process — non-fatal.
        }
    }

    /// <summary>Chat-friendly summary of a fight / rollup / zone / live combat.</summary>
    [RelayCommand]
    private void CopyNode(ParseNode? node)
    {
        node = WidenToSelection(node);
        if (node is null)
            return;
        string? text;
        lock (manager.Sync)
        {
            text = node switch
            {
                { GroupFights: { Count: > 0 } group } => AggregateSummary(node.Title, group),
                { Fight: CorrelatedEncounter fight } => FightSummary(fight),
                { Fight: AggregateFights aggregate } => AggregateSummary($"{aggregate.Zone} — {aggregate.Label}", aggregate.Fights),
                { Fight: LiveFollow } => LiveSummary(),
                _ => null,
            };
        }
        SetClipboard(text);
    }

    /// <summary>One summary for every fight shape (the live and merged
    /// copies had drifted: different DPS divisors, and the live one
    /// included damaging pets). Players only, display-rate maths.</summary>
    private string FightSummary(IFightView fight)
    {
        var seconds = fight.DisplaySeconds;
        var tags = fight.ClassificationSource is { } source
            ? manager.Classifier.Classify(source)
            : new Dictionary<string, CombatantTag>(StringComparer.Ordinal);
        var parts = fight.AllyCombatants
            .Where(kv => tags.TryGetValue(kv.Key, out var tag) && tag.Kind == CombatantKind.Player
                && kv.Value.Damage > 0)
            .Select(kv => (kv.Value.Name, Dps: kv.Value.Damage / seconds))
            .OrderByDescending(t => t.Dps)
            .Select(t => $"{t.Name} {CombatantRow.Compact(t.Dps)}");
        return $"{fight.Title} ({FmtSpan(fight.Duration)}, raid {CombatantRow.Compact(fight.EncDps)} dps): {string.Join(", ", parts)}";
    }

    private string AggregateSummary(string label, IReadOnlyList<CorrelatedEncounter> fights)
    {
        var lines = fights.Select(f => FightSummary(f));
        return $"{label} — {fights.Count} fights, {FmtSpan(SumDuration(fights))}\n{string.Join("\n", lines)}";
    }

    private string? LiveSummary()
    {
        foreach (var source in manager.Sources)
        {
            if (source.Engine.ActiveEncounter is { } encounter)
                return FightSummary(encounter);
        }
        return null;
    }

    /// <summary>Delete a fight (a whole zone group, or every fight of a
    /// multi-selection) from history — the
    /// fights stay in the archive, and Ctrl+Z brings them straight back.</summary>
    [RelayCommand]
    private void DeleteNode(ParseNode? node)
    {
        node = WidenToSelection(node);
        if (node is null)
            return;
        List<CorrelatedEncounter> deleted = [];
        lock (manager.Sync)
        {
            if (node.GroupFights is { } group)
            {
                foreach (var fight in group)
                {
                    if (manager.Correlator.Remove(fight))
                        deleted.Add(fight);
                    manager.History.MarkUnloaded(fight);
                    if (ReferenceEquals(_pinnedFight, fight))
                        _pinnedFight = null;
                }
            }
            else if (node.Fight is CorrelatedEncounter fight)
            {
                if (manager.Correlator.Remove(fight))
                    deleted.Add(fight);
                manager.History.MarkUnloaded(fight);
                if (ReferenceEquals(_pinnedFight, fight))
                    _pinnedFight = null;
            }
            // A pinned rollup/multi-selection must not keep showing fights
            // that just left history.
            if (_pinnedFight is AggregateFights aggregate && deleted.Any(aggregate.Fights.Contains))
                _pinnedFight = null;
        }
        if (deleted.Count > 0)
        {
            manager.Undo.Push(() =>
            {
                lock (manager.Sync)
                {
                    foreach (var fight in deleted)
                    {
                        manager.Correlator.Restore(fight);
                        manager.History.MarkLoaded(fight);
                    }
                }
                _treeSignature = (-1, false);
                Refresh();
            });
        }
        if (_pinnedFight is null)
            FollowLive = true;
        _treeSignature = (-1, false);
        Refresh();
    }

    /// <summary>Mouse-back / Esc navigation: pop one drill level if an
    /// overlay is open. False when there is nothing to go back from.</summary>
    public bool TryNavigateBack()
    {
        if (!DetailOpen)
            return false;
        CloseDetail();
        return true;
    }

    /// <summary>Fight context menu: upload every source's view of the fight
    /// to EQ2Lexicon — the same payloads auto-upload would have sent (the
    /// site mirror-groups them and keeps the longest as primary). Results
    /// surface on the Settings → Parse uploads status line. A multi-selection
    /// uploads every selected fight.</summary>
    [RelayCommand]
    private void UploadNode(ParseNode? node)
    {
        node = WidenToSelection(node);
        IReadOnlyList<CorrelatedEncounter> fights = node switch
        {
            { GroupFights: { Count: > 0 } group } => group,
            { Fight: CorrelatedEncounter fight } => [fight],
            _ => [],
        };
        List<List<Encounter>> uploads = [];
        lock (manager.Sync)
        {
            foreach (var fight in fights)
                uploads.Add([.. fight.Sources]);
        }
        foreach (var sources in uploads)
            manager.Uploads.UploadFight(sources);
    }

    /// <summary>Fight context menu: open the raw log at the fight's start.</summary>
    [RelayCommand]
    private void ViewFightLog(ParseNode? node)
    {
        if (node?.Fight is not CorrelatedEncounter fight)
            return;
        LogRows.ReplaceAll(LogWindowReader.Read(
                fight.Primary.SourceId, fight.StartTime.ToUnixTimeSeconds(), beforeSeconds: 2, afterSeconds: 30)
            .Select(raw => new LogRow(LogLineHighlighter.Build(raw), IsFocus: false)));
        _detailKey = null;
        _detailBucket = null;
        _detailAbility = null;
        DetailTitle = $"{fight.Title} › log";
        SwingLevel = true;
        LogLevel = true;
        // A report may be open underneath — clear its panel and chart.
        ReportLevel = false;
        ReportChartVisible = false;
        _reportScope = 0;
        DrillChartVisible = false;
        DetailOpen = true;
    }

    [RelayCommand]
    private static void CopyCombatant(CombatantRow? row)
    {
        if (row is null)
            return;
        var cls = string.IsNullOrEmpty(row.ClassName) ? "" : $" ({row.ClassName})";
        List<string> parts = [$"{row.Damage} dmg", $"{row.Dps} dps"];
        if (row.Hps.Length > 0)
            parts.Add($"{row.Hps} hps");
        if (row.Taken.Length > 0)
            parts.Add($"{row.Taken} taken");
        if (row.Deaths.Length > 0)
            parts.Add($"{row.Deaths} deaths");
        SetClipboard($"{row.Name}{cls}: {string.Join(", ", parts)}");
    }

    /// <summary>Per-ability damage breakdown of one combatant as text.</summary>
    [RelayCommand]
    private void CopyBreakdown(CombatantRow? row)
    {
        if (row is null)
            return;
        string? text = null;
        lock (manager.Sync)
        {
            if (ResolveFight() is not { } fight)
                return;
            var instances = FightCombatantInstances(fight, row.Key);
            if (instances.Count == 0)
                return;
            var abilities = new Dictionary<string, AbilityAcc>(StringComparer.Ordinal);
            long total = 0;
            foreach (var combatant in instances)
            {
                total += combatant.Damage;
                if (!combatant.OutgoingBuckets.TryGetValue(BucketConfig.OutgoingDamage, out var bucket))
                    continue;
                foreach (var (abilityName, stats) in bucket.Abilities)
                {
                    if (abilityName is Bucket.AllAbility or Combatant.KillingAbility)
                        continue;
                    var acc = GetOrAdd(abilities, abilityName);
                    foreach (var swing in stats.Swings)
                        acc.AddSwing(swing);
                }
            }
            var cls = string.IsNullOrEmpty(row.ClassName) ? "" : $" · {row.ClassName}";
            var lines = abilities
                .OrderByDescending(kv => kv.Value.Total)
                .Where(kv => kv.Value.Total > 0)
                .Select(kv => $"  {kv.Key} — {CombatantRow.Compact(kv.Value.Total)} ({100.0 * kv.Value.Total / Math.Max(1, total):F0}%), {kv.Value.Hits} hits, {(kv.Value.Hits > 0 ? 100.0 * kv.Value.Crits / kv.Value.Hits : 0):F0}% crit, max {CombatantRow.Compact(kv.Value.Max)}");
            text = $"{row.Name}{cls} — {CombatantRow.Compact(total)} dmg\n{string.Join("\n", lines)}";
        }
        SetClipboard(text);
    }

    [RelayCommand]
    private static void CopyAbility(AbilityRow? row)
    {
        if (row is null || row.IsGroupLabel)
            return;
        SetClipboard($"{row.Name}: {row.Total} total ({row.Percent}), {row.Dps} encdps, {row.Casts} swings, {row.Hits} hits, {row.CritPct} crit, avg {row.Avg}, max {row.Max}{(row.Types.Length > 0 ? $" [{row.Types}]" : "")}");
    }

    [RelayCommand]
    private static void CopySwing(SwingRow? row)
    {
        if (row is null)
            return;
        SetClipboard($"[{row.Time}] {row.Ability} {row.Result}{(row.Crit.Length > 0 ? " crit" : "")}{(row.Special.Length > 0 ? $" {row.Special}" : "")} {row.Type} → {row.Other}");
    }

}
