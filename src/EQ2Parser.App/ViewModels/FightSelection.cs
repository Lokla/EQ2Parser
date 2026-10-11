using EQ2Parser.Core.Correlation;

namespace EQ2Parser.App.ViewModels;

/// <summary>The selection rules of the Main page's fight list, kept apart
/// from <see cref="MainParseViewModel"/> because they need no view-model
/// state: which fights a Ctrl/Shift selection stands for, and which node of
/// a rebuilt tree stands for the rollup or zone header that was pinned.</summary>
public static class FightSelection
{
    /// <summary>The combined view of a set of selected tree nodes, or null
    /// when they hold fewer than two fights — the caller then treats the
    /// click as an ordinary single selection. Only fight nodes count:
    /// headers and "All" / "All Bosses" rollups swept into a Shift range are
    /// ignored, and a fight that appears twice counts once. Fights come out
    /// oldest first whatever order they were clicked in; the zone is each
    /// distinct zone (ignoring case) in that order, joined with " / ".
    /// Callers hold the manager lock: StartTime and Zone walk a fight's live
    /// source list.</summary>
    public static AggregateFights? Combine(IReadOnlyList<ParseNode> selected)
    {
        List<CorrelatedEncounter> fights = [.. selected
            .Where(n => n.IsFight)
            .Select(n => n.Fight)
            .OfType<CorrelatedEncounter>()
            .Distinct()
            .OrderBy(f => f.StartTime)];
        if (fights.Count < 2)
            return null;
        var zone = string.Join(" / ", fights.Select(f => f.Zone).Distinct(StringComparer.OrdinalIgnoreCase));
        return new AggregateFights(zone, MainParseViewModel.SelectionLabel, fights, IsSelection: true);
    }

    /// <summary>What a pinned "All" / "All Bosses" rollup or zone header is
    /// in a rebuilt tree: the fresh object its node carries, or null when
    /// the pin is something else or its group is not shown (collapsed,
    /// filtered out, gone). Every rebuild makes new nodes and new rollups,
    /// so the match is on the zone GROUP. A rollup matches on its label and
    /// a shared fight — the zone name would not do, one zone visited twice
    /// is two groups. A zone header matches on its group key, else on its
    /// zone and a shared fight: the rule the zone summary resolves its
    /// fights by, kept the same so the highlight and the summary cannot
    /// disagree. A multi-selection is never matched: it is a fixed set of
    /// fights, not a group. Reads only the nodes' own snapshot lists and
    /// compares references, so it needs no lock.</summary>
    public static object? Counterpart(object? pinned, IReadOnlyList<ParseNode> nodes)
    {
        switch (pinned)
        {
            case AggregateFights { IsSelection: false } rollup:
                foreach (var node in nodes)
                {
                    if (node.Fight is AggregateFights { IsSelection: false } candidate
                        && string.Equals(candidate.Label, rollup.Label, StringComparison.Ordinal)
                        && candidate.Fights.Any(rollup.Fights.Contains))
                        return candidate;
                }
                return null;
            case ZoneFights zone:
                {
                    ZoneFights? sharing = null;
                    foreach (var node in nodes)
                    {
                        if (node.Fight is not ZoneFights candidate)
                            continue;
                        if (string.Equals(candidate.GroupKey, zone.GroupKey, StringComparison.Ordinal))
                            return candidate;
                        // The key embeds the group's first fight, so deleting
                        // that fight changes it. Nodes run newest first and
                        // the summary takes the OLDEST group that shares a
                        // fight, hence the last such node wins.
                        if (string.Equals(candidate.Zone, zone.Zone, StringComparison.OrdinalIgnoreCase)
                            && (node.GroupFights ?? candidate.Snapshot).Any(zone.Snapshot.Contains))
                            sharing = candidate;
                    }
                    return sharing;
                }
            default:
                return null;
        }
    }
}
