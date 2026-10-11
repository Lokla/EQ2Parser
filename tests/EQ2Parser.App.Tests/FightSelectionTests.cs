using EQ2Parser.App.ViewModels;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Correlation;
using EQ2Parser.Core.Engine;

namespace EQ2Parser.App.Tests;

/// <summary>The fight list's selection rules, tested without a view-model:
/// which fights a Ctrl/Shift selection becomes (and when it falls back to a
/// single selection), and which node of a rebuilt tree stands for a pinned
/// rollup or zone header.</summary>
public sealed class FightSelectionTests
{
    private const string Reach = "The Hollow Reach";
    private const string Deeps = "Glimmer Deeps";

    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_775_000_000);

    private readonly EncounterCorrelator _correlator = new();

    /// <summary>One log's finished view of a ten-second fight.</summary>
    private static Encounter Seen(string owner, string zone, string enemy, int startSeconds)
    {
        var engine = new ParserEngine($@"C:\EQ2\logs\Testserver\eq2log_{owner}.txt", owner);
        engine.ChangeZone(zone);
        var start = T0.AddSeconds(startSeconds);
        Assert.True(engine.SetEncounter(start, owner, enemy));
        engine.AddSwing(SwingCategory.Melee, false, "None", owner, "Strike", 1000, start, enemy, "crushing");
        engine.AddSwing(SwingCategory.Melee, false, "None", owner, Combatant.KillingAbility, DamageValue.Death, start.AddSeconds(10), enemy, "death");
        engine.EndCombat();
        return engine.History[^1];
    }

    /// <summary>A fight in history, witnessed by one log. Every call uses
    /// the same log, and a log never merges with itself, so each call is its
    /// own fight however close the times are.</summary>
    private CorrelatedEncounter Fight(string zone, string enemy, int startSeconds)
    {
        _correlator.Accept(Seen("Tamsin", zone, enemy, startSeconds));
        return _correlator.History[^1];
    }

    private static ParseNode FightNode(CorrelatedEncounter fight) =>
        new() { Title = fight.Title, Fight = fight, IsFight = true, IsDeletable = true };

    private static ParseNode RollupNode(string zone, string label, params CorrelatedEncounter[] fights) =>
        new() { Title = label, Fight = new AggregateFights(zone, label, fights) };

    /// <summary>A zone header as the tree builds it: the whole group for
    /// delete/copy, and the fights the current filter shows for the summary.</summary>
    private static ParseNode HeaderNode(string zone, CorrelatedEncounter[] group, CorrelatedEncounter[]? shown = null)
    {
        var key = $"{zone}|{group[0].StartTime.Ticks}";
        return new ParseNode
        {
            IsHeader = true,
            Title = zone,
            GroupKey = key,
            IsDeletable = true,
            GroupFights = group,
            Fight = new ZoneFights(zone, key, shown ?? group),
        };
    }

    private static ParseNode DayNode() =>
        new() { IsHeader = true, Title = "Today", GroupKey = "day|2026-04-01" };

    // ── Combine: which fights a selection becomes ───────────────────────────

    [Fact]
    public void Selected_Fights_Combine_Oldest_First_Whatever_The_Click_Order()
    {
        var first = Fight(Reach, "a cavern lurker", 0);
        var second = Fight(Reach, "Warden Oskar", 100);
        var third = Fight(Reach, "a moss shambler", 200);

        var selection = FightSelection.Combine([FightNode(third), FightNode(first), FightNode(second)]);

        Assert.NotNull(selection);
        Assert.Equal([first, second, third], selection.Fights);
        Assert.True(selection.IsSelection);
        Assert.Equal(MainParseViewModel.SelectionLabel, selection.Label);
    }

    [Fact]
    public void A_Merged_Fight_Is_Ordered_By_Its_Earliest_Witness()
    {
        // The boss pull started at +100 in one log; a second log saw it
        // from +90. The trash fight between them started at +95.
        var boss = Fight(Reach, "Warden Oskar", 100);
        _correlator.Accept(Seen("Corvin", Reach, "Warden Oskar", 90));
        Assert.Equal(2, boss.Sources.Count);
        var trash = Fight(Reach, "a cavern lurker", 95);

        var selection = FightSelection.Combine([FightNode(trash), FightNode(boss)]);

        Assert.NotNull(selection);
        Assert.Equal([boss, trash], selection.Fights);
    }

    [Fact]
    public void A_Fight_Selected_Twice_Counts_Once()
    {
        var first = Fight(Reach, "a cavern lurker", 0);
        var second = Fight(Reach, "Warden Oskar", 100);

        var selection = FightSelection.Combine([FightNode(second), FightNode(first), FightNode(second)]);

        Assert.NotNull(selection);
        Assert.Equal([first, second], selection.Fights);
        // Two nodes, one fight: still a single selection.
        Assert.Null(FightSelection.Combine([FightNode(first), FightNode(first)]));
    }

    [Fact]
    public void Headers_And_Rollups_Swept_Into_A_Range_Are_Ignored()
    {
        var first = Fight(Reach, "a cavern lurker", 0);
        var second = Fight(Reach, "Warden Oskar", 100);
        var third = Fight(Reach, "a moss shambler", 200);
        CorrelatedEncounter[] group = [first, second, third];

        var selection = FightSelection.Combine(
        [
            DayNode(),
            HeaderNode(Reach, group),
            RollupNode(Reach, "All", group),
            RollupNode(Reach, "All Bosses", second),
            FightNode(third),
            FightNode(second),
        ]);

        // The header and the rollups hold all three fights; only the two
        // fight nodes were selected.
        Assert.NotNull(selection);
        Assert.Equal([second, third], selection.Fights);
    }

    [Fact]
    public void Fewer_Than_Two_Fights_Falls_Back_To_A_Single_Selection()
    {
        var first = Fight(Reach, "a cavern lurker", 0);
        var second = Fight(Reach, "Warden Oskar", 100);
        CorrelatedEncounter[] group = [first, second];

        Assert.Null(FightSelection.Combine([]));
        Assert.Null(FightSelection.Combine([FightNode(first)]));
        // No fight node at all, although the nodes carry two fights.
        Assert.Null(FightSelection.Combine([HeaderNode(Reach, group), RollupNode(Reach, "All", group)]));
        // One fight plus rows that are not fights.
        Assert.Null(FightSelection.Combine([DayNode(), HeaderNode(Reach, group), FightNode(second)]));
    }

    [Fact]
    public void Zone_Is_Each_Distinct_Zone_In_Fight_Order()
    {
        var deepsEarly = Fight(Deeps, "a silt crawler", 0);
        var reach = Fight(Reach, "Warden Oskar", 100);
        var deepsLate = Fight(Deeps, "Matron Ysolde", 200);

        var across = FightSelection.Combine([FightNode(reach), FightNode(deepsLate), FightNode(deepsEarly)]);
        var within = FightSelection.Combine([FightNode(deepsLate), FightNode(deepsEarly)]);

        Assert.Equal($"{Deeps} / {Reach}", across?.Zone);
        Assert.Equal(Deeps, within?.Zone);
    }

    [Fact]
    public void Zones_That_Differ_Only_In_Case_Are_One_Zone()
    {
        var first = Fight(Reach, "a cavern lurker", 0);
        var second = Fight(Reach.ToUpperInvariant(), "Warden Oskar", 100);
        var third = Fight(Deeps, "a silt crawler", 200);

        var selection = FightSelection.Combine([FightNode(third), FightNode(second), FightNode(first)]);

        // The spelling of the earliest fight is the one kept.
        Assert.Equal($"{Reach} / {Deeps}", selection?.Zone);
    }

    // ── Counterpart: the pinned rollup or header in a rebuilt tree ──────────

    [Fact]
    public void A_Pinned_Rollup_Is_Found_By_Its_Label_And_A_Shared_Fight()
    {
        var first = Fight(Reach, "a cavern lurker", 0);
        var boss = Fight(Reach, "Warden Oskar", 100);
        var pinnedAll = new AggregateFights(Reach, "All", [first, boss]);
        var pinnedBosses = new AggregateFights(Reach, "All Bosses", [boss]);

        // Another fight ended; the tree was rebuilt with fresh rollups.
        var third = Fight(Reach, "a moss shambler", 200);
        CorrelatedEncounter[] group = [first, boss, third];
        var all = RollupNode(Reach, "All", group);
        var bosses = RollupNode(Reach, "All Bosses", boss);
        List<ParseNode> rebuilt =
        [
            DayNode(), HeaderNode(Reach, group), all, bosses,
            FightNode(third), FightNode(boss), FightNode(first),
        ];

        Assert.Same(all.Fight, FightSelection.Counterpart(pinnedAll, rebuilt));
        Assert.Same(bosses.Fight, FightSelection.Counterpart(pinnedBosses, rebuilt));
    }

    [Fact]
    public void One_Zone_Visited_Twice_Is_Two_Groups()
    {
        var earlier = Fight(Reach, "a cavern lurker", 0);
        var earlierBoss = Fight(Reach, "Warden Oskar", 100);
        var between = Fight(Deeps, "a silt crawler", 1000);
        var later = Fight(Reach, "a moss shambler", 2000);
        var laterBoss = Fight(Reach, "Warden Oskar", 2100);
        var pinned = new AggregateFights(Reach, "All", [earlier, earlierBoss]);

        // Newest first, as the tree lists them: the later visit's "All" has
        // the same zone and label and comes first, but shares no fight.
        var laterAll = RollupNode(Reach, "All", later, laterBoss);
        var earlierAll = RollupNode(Reach, "All", earlier, earlierBoss);
        List<ParseNode> rebuilt = [laterAll, FightNode(between), earlierAll];

        Assert.Same(earlierAll.Fight, FightSelection.Counterpart(pinned, rebuilt));
        // With the earlier visit collapsed its rollup is not in the tree,
        // and the later visit's must not stand in for it.
        Assert.Null(FightSelection.Counterpart(pinned, [laterAll, FightNode(between)]));
    }

    [Fact]
    public void Only_A_Rollup_Or_A_Zone_Header_Has_A_Counterpart()
    {
        var first = Fight(Reach, "a cavern lurker", 0);
        var second = Fight(Reach, "Warden Oskar", 100);
        CorrelatedEncounter[] group = [first, second];
        List<ParseNode> rebuilt =
        [
            HeaderNode(Reach, group), RollupNode(Reach, "All", group), FightNode(second), FightNode(first),
        ];
        var selection = FightSelection.Combine([FightNode(first), FightNode(second)]);

        // A multi-selection is a fixed set of fights, not a zone group: the
        // "All" rollup over the very same fights is not it.
        Assert.NotNull(selection);
        Assert.Null(FightSelection.Counterpart(selection, rebuilt));
        // A single fight is the same object in every tree; nothing pinned.
        Assert.Null(FightSelection.Counterpart(first, rebuilt));
        Assert.Null(FightSelection.Counterpart(null, rebuilt));
    }

    [Fact]
    public void A_Pinned_Zone_Header_Is_Found_By_Its_Group_Key()
    {
        var first = Fight(Reach, "a cavern lurker", 0);
        var second = Fight(Reach, "Warden Oskar", 100);
        var pinned = (ZoneFights)HeaderNode(Reach, [first, second]).Fight!;

        var third = Fight(Reach, "a moss shambler", 200);
        var header = HeaderNode(Reach, [first, second, third]);
        List<ParseNode> rebuilt = [DayNode(), header, FightNode(third), FightNode(second), FightNode(first)];

        Assert.NotSame(pinned, header.Fight);
        Assert.Same(header.Fight, FightSelection.Counterpart(pinned, rebuilt));
    }

    [Fact]
    public void A_Zone_Header_Whose_Key_Drifted_Is_Found_By_A_Shared_Fight()
    {
        var first = Fight(Reach, "a cavern lurker", 0);
        var second = Fight(Reach, "a moss shambler", 100);
        var pinned = (ZoneFights)HeaderNode(Reach, [first, second]).Fight!;

        // Since the pin: the boss died, and the group's first fight was
        // deleted, so the key (which embeds that fight's start) changed.
        // "Bosses only" is on, so the header's own snapshot is the boss
        // alone and the one fight still shared is only in its whole group.
        // A later visit to the same zone is a different group and comes
        // first in the tree.
        var boss = Fight(Reach, "Warden Oskar", 200);
        var elsewhere = Fight(Deeps, "a silt crawler", 1000);
        var later = Fight(Reach, "a cavern lurker", 2000);
        var laterVisit = HeaderNode(Reach, [later]);
        var header = HeaderNode(Reach, [second, boss], shown: [boss]);
        List<ParseNode> rebuilt = [laterVisit, HeaderNode(Deeps, [elsewhere]), header];

        Assert.Same(header.Fight, FightSelection.Counterpart(pinned, rebuilt));
        // Nothing of the pinned group left in the tree: no stand-in.
        Assert.Null(FightSelection.Counterpart(pinned, [laterVisit, HeaderNode(Deeps, [elsewhere])]));
    }
}
