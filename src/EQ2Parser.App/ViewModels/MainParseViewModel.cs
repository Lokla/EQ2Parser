using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
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

/// <summary>One entry in the left-hand parse tree: a zone header or a fight.</summary>
public sealed class ParseNode
{
    public bool IsHeader { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Encounter (live), CorrelatedEncounter (history), or
    /// AggregateFights ("All" / "All Bosses" zone rollups).</summary>
    public object? Fight { get; init; }
    /// <summary>Zone-group identity for collapse toggling (headers only).</summary>
    public string? GroupKey { get; init; }
    /// <summary>Win green / Partial amber / Loss red; gold for headers.</summary>
    public System.Windows.Media.Brush TitleBrush { get; init; } = ClassColors.TreeText;

    /// <summary>Collapse-state glyph ("▸"/"▾"), headers only — its own
    /// clickable element so the header body is free to select the zone.</summary>
    public string Arrow { get; init; } = "";

    // Context-menu discriminators: each node kind only offers what applies.
    public bool IsFight { get; init; }
    public bool IsDeletable { get; init; }
    /// <summary>Every fight of a zone group (headers only, for zone delete/copy).</summary>
    public IReadOnlyList<CorrelatedEncounter>? GroupFights { get; init; }
}

/// <summary>A zone rollup selection: combined stats over several fights.
/// Implements <see cref="IFightView"/> so shared fight maths (durations,
/// display rates, combatant instances) needs no per-shape switch; rollup
/// EncDPS uses the COMBINED duration (over the merged fight).</summary>
public sealed record AggregateFights(string Zone, string Label, IReadOnlyList<CorrelatedEncounter> Fights) : IFightView
{
    public string Title => Label;
    public DateTimeOffset StartTime => Fights.Count > 0 ? Fights.Min(f => f.StartTime) : default;
    public DateTimeOffset EndTime => Fights.Count > 0 ? Fights.Max(f => f.EndTime) : default;

    /// <summary>Combined fight time — the sum of member durations, not the
    /// wall-clock span (idle time between pulls never counts).</summary>
    public TimeSpan Duration
    {
        get
        {
            var total = TimeSpan.Zero;
            foreach (var fight in Fights)
                total += fight.Duration;
            return total;
        }
    }

    public long Damage => Fights.Sum(f => f.Damage);

    public double EncDps
    {
        get
        {
            var seconds = Duration.TotalSeconds;
            return seconds > 0 ? Damage / seconds : 0;
        }
    }

    /// <summary>A rollup spans wins and losses — no single outcome.</summary>
    public SuccessLevel GetSuccessLevel() => SuccessLevel.Indeterminate;

    /// <summary>No single source — consumers classify per member fight.</summary>
    Encounter? IFightView.ClassificationSource => null;

    IEnumerable<Encounter> IFightView.ClassificationSources => Fights.Select(f => f.Primary);

    IEnumerable<KeyValuePair<string, Combatant>> IFightView.AllyCombatants
    {
        get
        {
            foreach (var fight in Fights)
                foreach (var pair in ((IFightView)fight).AllyCombatants)
                    yield return pair;
        }
    }

    IEnumerable<KeyValuePair<string, Combatant>> IFightView.ViewCombatants
    {
        get
        {
            foreach (var fight in Fights)
                foreach (var pair in ((IFightView)fight).ViewCombatants)
                    yield return pair;
        }
    }

    public bool ContainsCombatant(string key) => Fights.Any(f => f.ContainsCombatant(key));

    public IReadOnlyList<Combatant> InstancesOf(string key)
    {
        List<Combatant> instances = [];
        foreach (var fight in Fights)
            instances.AddRange(fight.InstancesOf(key));
        return instances;
    }
}

/// <summary>A zone-header selection: the encounter-list summary view. The
/// group is re-resolved from history each refresh (so a live zone gains new
/// fights); Snapshot is the click-time fallback if the group re-shapes.</summary>
public sealed record ZoneFights(string Zone, string GroupKey, IReadOnlyList<CorrelatedEncounter> Snapshot);

/// <summary>One encounter row of the zone-summary table.</summary>
public sealed record ZoneFightRow(
    CorrelatedEncounter Fight, string Title, System.Windows.Media.Brush TitleBrush,
    string Start, string Duration, string Damage, string Dps,
    string Kills, string Deaths, double BarFraction);

/// <summary>Tree-node sentinel: "follow the live fight" selection.</summary>
public sealed class LiveFollow
{
    public static readonly LiveFollow Instance = new();
    private LiveFollow() { }
}

/// <summary>One entry in the perspective dropdown: a specific log source's
/// view of the resolved fight, or the merged view (SourceId null). Record
/// equality lets rebuilt option lists preserve the user's selection.</summary>
public sealed record PerspectiveOption(string Label, string? SourceId);

/// <summary>One row of a drill-down table (ability or attacker breakdown).</summary>
public sealed partial class AbilityRow : ObservableObject
{
    public required string Key { get; set; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _source = "";

    [ObservableProperty]
    private System.Windows.Media.Brush _sourceBrush = ClassColors.Neutral;

    [ObservableProperty]
    private string _casts = "";

    [ObservableProperty]
    private string _hits = "";

    [ObservableProperty]
    private string _critPct = "";

    [ObservableProperty]
    private string _freq = "";

    [ObservableProperty]
    private string _avg = "";

    [ObservableProperty]
    private string _max = "";

    [ObservableProperty]
    private string _total = "";

    [ObservableProperty]
    private string _percent = "";

    [ObservableProperty]
    private string _types = "";

    [ObservableProperty]
    private string _dps = "";

    [ObservableProperty]
    private double _barFraction;

    /// <summary>True for the OUTGOING/INCOMING divider rows in the bucket
    /// list — styled as labels, not drillable.</summary>
    [ObservableProperty]
    private bool _isGroupLabel;
}

/// <summary>One swing of the deepest drill level. Epoch/SourcePath/Amount/
/// Ability identify the swing so clicking it can open the raw log view.</summary>
public sealed record SwingRow(
    string Time, string Result, string Crit, string Special, string Type, string Other,
    long Epoch, string SourcePath, long Amount, string Ability);

/// <summary>One rendered raw-log line (colour segments + focus flag).</summary>
public sealed record LogRow(IReadOnlyList<LogSegment> Segments, bool IsFocus);

/// <summary>
/// The Main page: zone/fight tree on the left, sortable combatant
/// grid on the right with allies and enemies in separate sections. "Follow
/// live" keeps the grid on the active fight; clicking a tree node pins it.
/// </summary>
public sealed partial class MainParseViewModel : ObservableObject
{
    // Plain field (named like the old primary-ctor parameter so every
    // member reads unchanged) — primary-ctor params are only in scope in
    // the declaring part, and this class is split across partial files.
    private readonly SourceManager manager;

    public MainParseViewModel(SourceManager manager)
    {
        this.manager = manager;
        Columns = ColumnSets.Encounter(manager);
        DrillColumns = ColumnSets.Drill(manager);
        SwingColumns = ColumnSets.Swing(manager);
    }

    /// <summary>Exposed for view-owned windows (the Archive).</summary>
    public SourceManager Manager => manager;

    /// <summary>Configurable columns: encounter grid + drill table.</summary>
    public ColumnSetVm Columns { get; }

    public ColumnSetVm DrillColumns { get; }

    public ColumnSetVm SwingColumns { get; }

    private object? _pinnedFight;
    private (long Version, bool AnyActive) _treeSignature = (-1, false);

    // ── Perspective (which log's view of the resolved fight) ────────────────

    public ObservableCollection<PerspectiveOption> Perspectives { get; } = [];

    [ObservableProperty]
    private PerspectiveOption? _selectedPerspective;

    [ObservableProperty]
    private bool _perspectiveVisible;

    /// <summary>The reconciled choice for THIS tick — ResolveFight reads it
    /// under the sync lock; the ObservableCollection/SelectedPerspective sync
    /// happens after the lock (see ApplyPerspectives).</summary>
    private PerspectiveOption? _effectivePerspective;

    private bool _applyingPerspectives;
    private string _perspectiveSig = "";

    partial void OnSelectedPerspectiveChanged(PerspectiveOption? value)
    {
        if (_applyingPerspectives || value is null)
            return;
        _effectivePerspective = value;
        // A live pick is sticky: boxers always want their main — persist the
        // owner so the next session's live view starts there too.
        if (FollowLive && value.SourceId is { } sourceId)
        {
            var owner = manager.Sources.FirstOrDefault(s => s.Path == sourceId)?.Owner;
            if (owner is { Length: > 0 } && owner != manager.Settings.LivePerspectiveOwner)
            {
                manager.Settings = manager.Settings with { LivePerspectiveOwner = owner };
                manager.Settings.Save();
            }
        }
        RefreshGrid();
    }

    public BulkObservableCollection<ParseNode> TreeNodes { get; } = [];
    public ObservableCollection<CombatantRow> AllyRows { get; } = [];
    public ObservableCollection<CombatantRow> PetRows { get; } = [];
    public ObservableCollection<CombatantRow> EnemyRows { get; } = [];

    // ── Zone summary (encounter list for a selected zone header) ────────────

    public BulkObservableCollection<ZoneFightRow> ZoneSummaryRows { get; } = [];

    [ObservableProperty]
    private bool _zoneSummaryOpen;

    /// <summary>Rows only rebuild when the group's identity or fight count
    /// changes — walking every fight's combatants at 10 Hz would not fly.</summary>
    private (string GroupKey, int Count, long Version) _zoneSummarySig;

    /// <summary>Drill-table rebuild gate: fight identity + drill position +
    /// view level + a cheap data version. Without it the bucket/ability
    /// tables re-derived from raw swings every 100ms tick, even for ended
    /// fights whose data can never change again.</summary>
    private (object? Fight, string? Key, string? Bucket, string? Ability, bool Swing, bool Log, long Version) _detailSig;

    private static long DetailVersion(object fight) => fight switch
    {
        // Live fights: total observed swings (O(combatants) now).
        Encounter e => e.Combatants.Values.Sum(c => (long)c.ObservedSwingCount),
        // Correlated sources are completed and immutable — only a merge
        // (new source) changes the data.
        CorrelatedEncounter m => m.Sources.Count,
        _ => 0,
    };

    /// <summary>Summary row click: jump to that fight's combatant grid.</summary>
    [RelayCommand]
    private void OpenZoneFight(ZoneFightRow? row)
    {
        if (row is null)
            return;
        _pinnedFight = row.Fight;
        FollowLive = false;
        RefreshGrid();
    }

    [ObservableProperty]
    private string _petHeader = Loc.Format("MainVm_PetsHeader", 0);

    [ObservableProperty]
    private string _enemyHeader = Loc.Format("MainVm_EnemiesHeader", 0);

    // ── Chart (encounter-summary bars + average line) ───────────────────────

    [ObservableProperty]
    private bool _chartVisible;

    [ObservableProperty]
    private ISeries[] _chartSeries = [];

    [ObservableProperty]
    private Axis[] _chartXAxes = [new Axis()]; // never empty: LiveCharts throws on measure with zero axes

    [ObservableProperty]
    private Axis[] _chartYAxes = [new Axis()]; // never empty: LiveCharts throws on measure with zero axes

    private (object? Fight, string Metric) _chartKey;
    private long _chartVersion;
    private long _lastChartBuildMs;

    // ── Drill charts (per depth: bucket lines / ability doughnut / heat) ────

    [ObservableProperty]
    private bool _drillChartVisible;

    [ObservableProperty]
    private bool _drillCartesianVisible;

    [ObservableProperty]
    private bool _drillDonutVisible;

    [ObservableProperty]
    private ISeries[] _drillCartesianSeries = [];

    [ObservableProperty]
    private Axis[] _drillXAxes = [new Axis()]; // never empty: LiveCharts throws on measure with zero axes

    [ObservableProperty]
    private Axis[] _drillYAxes = [new Axis()]; // never empty: LiveCharts throws on measure with zero axes

    [ObservableProperty]
    private ISeries[] _drillDonutSeries = [];

    public SolidColorPaint DrillLegendPaint { get; } = new(new SKColor(0xB0, 0xB4, 0xC8));
    public SolidColorPaint DonutLegendPaint { get; } = new(new SKColor(0xB0, 0xB4, 0xC8));
    public SolidColorPaint ReportLegendPaint { get; } = new(new SKColor(0xB0, 0xB4, 0xC8));
    public SolidColorPaint BreakdownLegendPaint { get; } = new(new SKColor(0xB0, 0xB4, 0xC8));

    private (string?, string?, string?) _drillChartKey = ("\0", null, null);
    private long _drillChartVersion;
    private long _drillChartMs;

    // ── Drill-down state (combatant → bucket → ability → swings) ───────────

    [ObservableProperty]
    private bool _detailOpen;

    [ObservableProperty]
    private string _detailTitle = "";

    [ObservableProperty]
    private bool _swingLevel;

    [ObservableProperty]
    private string _drillNameHeader = Loc.Get("MainVm_DrillHeaderBucket");

    private string? _detailKey;
    private string? _detailBucket;
    private string? _detailAbility;
    private (string, string, string, int) _swingSignature;

    [ObservableProperty]
    private bool _logLevel;

    [ObservableProperty]
    private bool _reportLevel;

    private string _reportText = "";

    /// <summary>The swing table shows at swing depth unless a log/report view is open.</summary>
    public bool SwingTableVisible => SwingLevel && !LogLevel && !ReportLevel;

    /// <summary>The bucket/ability drill table is on screen — the drill
    /// COLUMNS picker binds here, not to !SwingLevel, which left a stray
    /// no-op button on report and raw-log views.</summary>
    public bool DrillTableVisible => !SwingLevel && !LogLevel && !ReportLevel;

    partial void OnSwingLevelChanged(bool value)
    {
        OnPropertyChanged(nameof(SwingTableVisible));
        OnPropertyChanged(nameof(DrillTableVisible));
    }

    partial void OnLogLevelChanged(bool value)
    {
        OnPropertyChanged(nameof(SwingTableVisible));
        OnPropertyChanged(nameof(DrillTableVisible));
    }

    partial void OnReportLevelChanged(bool value)
    {
        OnPropertyChanged(nameof(SwingTableVisible));
        OnPropertyChanged(nameof(DrillTableVisible));
    }

    public ObservableCollection<AbilityRow> DrillRows { get; } = [];
    public BulkObservableCollection<SwingRow> SwingRows { get; } = [];
    public BulkObservableCollection<LogRow> LogRows { get; } = [];

    /// <summary>Deepest drill: click a swing to see the raw log around it,
    /// with the matching line highlighted and tokens colourised.</summary>
    [RelayCommand]
    private void OpenSwingLog(SwingRow? row)
    {
        if (row is null || string.IsNullOrEmpty(row.SourcePath))
            return;
        List<LogRow> logRows = [];
        var focusFound = false;
        foreach (var raw in LogWindowReader.Read(row.SourcePath, row.Epoch, beforeSeconds: 5, afterSeconds: 5))
        {
            var isFocus = false;
            if (!focusFound && LineEpoch(raw) == row.Epoch
                && (row.Amount <= 0 || LogLineHighlighter.ContainsAmount(raw, row.Amount))
                && (row.Ability == Core.Grammar.EnglishGrammar.AutoAttackAbility
                    || raw.Contains(row.Ability, StringComparison.Ordinal)))
            {
                isFocus = true;
                focusFound = true;
            }
            logRows.Add(new LogRow(LogLineHighlighter.Build(raw), isFocus));
        }
        LogRows.ReplaceAll(logRows);
        LogLevel = true;
        DrillChartVisible = false;
        ReportLevel = false;
        ReportChartVisible = false;
        _reportScope = 0;
    }

    private static long LineEpoch(string raw)
    {
        var close = raw.IndexOf(')');
        return close > 1 && long.TryParse(raw.AsSpan(1, close - 1), out var epoch) ? epoch : -1;
    }

    // ── Tree ────────────────────────────────────────────────────────────────

    private void RebuildTreeIfChanged()
    {
        long version;
        bool anyActive;
        lock (manager.Sync)
        {
            // Correlator.Version covers create/merge/delete/restore — a
            // History.Count signature was blind to in-place merges, leaving
            // stale tree labels after a second log joined a fight.
            version = manager.Correlator.Version;
            anyActive = manager.Sources.Any(s => s.Engine.InCombat);
        }
        if ((version, anyActive) == _treeSignature)
            return;
        _treeSignature = (version, anyActive);
        RebuildTree();
    }

    private static System.Windows.Media.SolidColorBrush OutcomeBrush(CorrelatedEncounter fight) =>
        fight.GetSuccessLevel() switch
        {
            SuccessLevel.Win => ClassColors.OutcomeWin,
            SuccessLevel.Partial => ClassColors.OutcomePartial,
            SuccessLevel.Loss => ClassColors.OutcomeLoss,
            _ => ClassColors.TreeText,
        };

    private void RebuildTree()
    {
        List<ParseNode> nodes = [];
        lock (manager.Sync)
        {
            // Active combat: a Live node at the very top — clicking it
            // resumes following the current fight after browsing history.
            if (manager.Sources.Any(s => s.Engine.InCombat))
            {
                nodes.Add(new ParseNode
                {
                    Title = Loc.Get("MainVm_LiveCombat"),
                    Fight = LiveFollow.Instance,
                    TitleBrush = ClassColors.OutcomeWin,
                });
            }
            // Newest first: group consecutive same-zone fights, sidebar
            // style ("The Emerald Halls - [25] 18:57:04") with per-zone
            // "All" / "All Bosses" rollup nodes. The arrow collapses; the
            // header body selects the zone's encounter summary. The
            // Bosses-only filter trims trash fights.
            var groups = GroupHistoryZones();
            var today = DateTime.Now.Date;
            DateTime? currentSection = null;
            var sectionCollapsed = false;
            for (var g = groups.Count - 1; g >= 0; g--)
            {
                var (zone, items) = groups[g];
                var zoneName = string.IsNullOrEmpty(zone) ? Loc.Get("MainVm_UnknownZone") : zone;
                var shown = BossesOnly ? items.Where(f => IsBossTitle(f.Title)).ToList() : items;
                if (shown.Count == 0)
                    continue;

                // Date sections: one header per local calendar day, by the
                // group's LATEST activity — resuming a persisted instance
                // days later surfaces the whole group under Today instead
                // of leaving it buried in a collapsed old date. Groups are
                // consecutive runs, so last-activity dates stay ordered.
                // Every non-today section starts collapsed; toggles stick
                // for the session.
                var day = items[^1].StartTime.ToLocalTime().Date;
                if (currentSection != day)
                {
                    currentSection = day;
                    var dayKey = $"day|{day:yyyy-MM-dd}";
                    sectionCollapsed = _dateExpandOverrides.TryGetValue(dayKey, out var expand)
                        ? !expand
                        : day != today;
                    nodes.Add(new ParseNode
                    {
                        IsHeader = true,
                        GroupKey = dayKey,
                        Arrow = sectionCollapsed ? "▸" : "▾",
                        Title = day == today
                            ? Loc.Get("MainVm_Today")
                            : day.ToString("dddd d MMMM", System.Globalization.CultureInfo.CurrentCulture),
                        TitleBrush = ClassColors.TreeText,
                    });
                }
                if (sectionCollapsed)
                    continue;

                var groupKey = $"{zoneName}|{items[0].StartTime.Ticks}";
                var collapsed = _collapsedZones.Contains(groupKey);
                nodes.Add(new ParseNode
                {
                    IsHeader = true,
                    GroupKey = groupKey,
                    Arrow = collapsed ? "▸" : "▾",
                    // Latest-activity time, matching the date sectioning —
                    // under a "Today" header, a resumed group's Saturday
                    // start time would read as today's.
                    Title = $"{zoneName} - [{shown.Count}] {items[^1].StartTime.ToLocalTime():HH:mm:ss}",
                    TitleBrush = ClassColors.TreeHeader,
                    IsDeletable = true,
                    GroupFights = [.. items],
                    Fight = new ZoneFights(zoneName, groupKey, [.. shown]),
                });
                if (collapsed)
                    continue;
                if (shown.Count > 1)
                {
                    if (!BossesOnly)
                    {
                        var all = items.ToArray();
                        nodes.Add(new ParseNode
                        {
                            Title = Loc.Format("MainVm_AllRollup", FmtSpan(SumDuration(all))),
                            // Label stays English: it feeds clipboard/report
                            // output, which is deliberately not localized.
                            Fight = new AggregateFights(zoneName, "All", all),
                        });
                    }
                    var bosses = items.Where(f => IsBossTitle(f.Title)).ToArray();
                    if (bosses.Length > 0)
                    {
                        nodes.Add(new ParseNode
                        {
                            Title = Loc.Format("MainVm_AllBossesRollup", bosses.Length, FmtSpan(SumDuration(bosses))),
                            Fight = new AggregateFights(zoneName, "All Bosses", bosses),
                        });
                    }
                }
                for (var i = shown.Count - 1; i >= 0; i--)
                {
                    var fight = shown[i];
                    var sources = fight.Sources.Count > 1 ? Loc.Format("MainVm_SourcesSuffix", fight.Sources.Count) : "";
                    nodes.Add(new ParseNode
                    {
                        Title = $"{fight.Title} - [{FmtSpan(fight.Duration)}] {fight.StartTime.ToLocalTime():HH:mm:ss}{sources}",
                        Fight = fight,
                        TitleBrush = OutcomeBrush(fight),
                        IsFight = true,
                        IsDeletable = true,
                    });
                }
            }
        }

        TreeNodes.ReplaceAll(nodes);
        TreeRebuilt?.Invoke();
    }

    /// <summary>Raised after every tree rebuild. Nodes are fresh objects each
    /// time, so the list loses its selection — the view re-selects every
    /// node <see cref="IsTreeSelected"/> accepts.</summary>
    public event Action? TreeRebuilt;

    /// <summary>Trash mobs are articled ("a bloom custodian"); named bosses
    /// are not. Placeholder-titled scraps are never bosses.</summary>
    private static bool IsBossTitle(string title) => Encounter.IsBossTitle(title);

    /// <summary>Consecutive same-zone runs of history, oldest first — the
    /// shape behind both the tree and the zone-summary view. Callers hold
    /// the manager lock.</summary>
    private List<(string Zone, List<CorrelatedEncounter> Items)> GroupHistoryZones()
    {
        List<(string Zone, List<CorrelatedEncounter> Items)> groups = [];
        DateTimeOffset? groupExpiry = null;
        foreach (var fight in manager.Correlator.History)
        {
            var newGroup = groups.Count == 0
                || !string.Equals(groups[^1].Zone, fight.Zone, StringComparison.OrdinalIgnoreCase)
                // A fresh INSTANCE of the same zone (zone out, reset, zone
                // back in with nothing between) is its own group: the
                // lockout expiry identifies the instance. The client
                // truncates the remaining time to hours, so two sightings
                // of one instance can disagree by up to an hour — split
                // only past a 90-minute gap. Null (non-instanced zone or a
                // source that missed the lockout line) matches anything.
                || (groupExpiry is { } known && fight.ZoneInstanceExpiry is { } seen
                    && (seen - known).Duration() > TimeSpan.FromMinutes(90));
            if (newGroup)
            {
                groups.Add((fight.Zone, []));
                groupExpiry = null;
            }
            groups[^1].Items.Add(fight);
            groupExpiry ??= fight.ZoneInstanceExpiry;
        }
        return groups;
    }

    /// <summary>The zone group's current fights (respecting Bosses only) —
    /// re-resolved so a live zone's summary gains new fights as they end.
    /// Exact key match first; else membership (the key embeds the FIRST
    /// fight's start time, so deleting that fight drifts the key while the
    /// group lives on). A fully-deleted group resolves EMPTY — the old
    /// snapshot fallback resurrected deleted fights.</summary>
    private List<CorrelatedEncounter> ResolveZoneFights(ZoneFights zone)
    {
        List<CorrelatedEncounter>? match = null;
        foreach (var (z, items) in GroupHistoryZones())
        {
            var zoneName = string.IsNullOrEmpty(z) ? Loc.Get("MainVm_UnknownZone") : z;
            if (string.Equals($"{zoneName}|{items[0].StartTime.Ticks}", zone.GroupKey, StringComparison.Ordinal))
            {
                match = items;
                break;
            }
            if (match is null
                && string.Equals(zoneName, zone.Zone, StringComparison.OrdinalIgnoreCase)
                && items.Any(zone.Snapshot.Contains))
                match = items;
        }
        if (match is null)
            return [];
        return BossesOnly ? [.. match.Where(f => IsBossTitle(f.Title))] : match;
    }

    /// <summary>One row per encounter: ally damage + deaths, enemy-side
    /// deaths as kills, bar scaled to the biggest fight's damage.</summary>
    private static List<ZoneFightRow> BuildZoneSummary(List<CorrelatedEncounter> fights)
    {
        List<(CorrelatedEncounter Fight, long Damage, int Kills, int Deaths)> stats = [];
        long top = 1;
        foreach (var fight in fights)
        {
            long damage = 0;
            int deaths = 0, kills = 0;
            foreach (var (key, entry) in fight.MergedCombatants)
            {
                if (fight.MergedAllyKeys.Contains(key))
                {
                    damage += entry.Combatant.Damage;
                    deaths += entry.Combatant.Deaths;
                }
                else
                {
                    kills += entry.Combatant.Deaths;
                }
            }
            stats.Add((fight, damage, kills, deaths));
            top = Math.Max(top, damage);
        }
        List<ZoneFightRow> rows = [];
        foreach (var (fight, damage, kills, deaths) in stats)
        {
            rows.Add(new ZoneFightRow(
                fight, fight.Title, OutcomeBrush(fight),
                fight.StartTime.ToLocalTime().ToString("HH:mm:ss"),
                FmtSpan(fight.Duration),
                CombatantRow.Compact(damage),
                CombatantRow.Compact(fight.EncDps),
                kills > 0 ? kills.ToString() : "",
                deaths > 0 ? deaths.ToString() : "",
                (double)damage / top));
        }
        return rows;
    }

    /// <summary>Every per-fight instance of one combatant, any fight shape
    /// (an aggregate yields one instance per member fight).</summary>
    private static IReadOnlyList<Combatant> FightCombatantInstances(object fight, string key) =>
        fight is IFightView view ? view.InstancesOf(key) : [];

    /// <summary>Canonical fight duration in seconds (≥1) for display rate
    /// maths — <see cref="IFightView.DisplaySeconds"/>, shape-free.</summary>
    private static double FightSeconds(object? fight) =>
        fight is IFightView view ? view.DisplaySeconds : 1.0;

    private static TimeSpan SumDuration(IReadOnlyList<CorrelatedEncounter> fights)
    {
        var total = TimeSpan.Zero;
        foreach (var fight in fights)
            total += fight.Duration;
        return total;
    }

    private static string FmtSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");

    // ── Selection, sort, and the shell tick ─────────────────────────────────
    // The page's master state. Lived in the .Reports partial for a while,
    // which made the report file the de-facto owner of selection — moved
    // here so the main file owns what drives every other partial.

    [ObservableProperty]
    private string _breadcrumb = "No encounters yet — add a log under Sources.";

    [ObservableProperty]
    private bool _inCombat;

    [ObservableProperty]
    private bool _followLive = true;

    [ObservableProperty]
    private string _sortColumn = "Damage";

    [ObservableProperty]
    private bool _sortDescending = true;

    private readonly HashSet<string> _collapsedZones = new(StringComparer.Ordinal);

    /// <summary>Per-day expand/collapse choices for the date sections
    /// (session-scoped). Absent = the default: expanded for today,
    /// collapsed for every earlier day.</summary>
    private readonly Dictionary<string, bool> _dateExpandOverrides = new(StringComparer.Ordinal);

    [ObservableProperty]
    private bool _bossesOnly;

    partial void OnBossesOnlyChanged(bool value) => RebuildTree();

    /// <summary><see cref="AggregateFights.Label"/> of a Ctrl/Shift
    /// multi-selection. Stored English like the other rollup labels.</summary>
    public const string SelectionLabel = "Selected";

    /// <summary>The tree's selection changed (Explorer-style Ctrl/Shift
    /// extended selection). Two or more fights pin a combined rollup of
    /// exactly those fights; otherwise <paramref name="focus"/> (the node
    /// just clicked) drives the view as a single selection. Headers and
    /// rollup nodes swept into a range never join the combined view. An
    /// empty selection (the list resetting on rebuild) keeps the view.</summary>
    public void SelectTreeNodes(IReadOnlyList<ParseNode> selected, ParseNode? focus)
    {
        if (selected.Count == 0)
            return;
        // StartTime/Zone walk the fight's live source list, which the pump
        // appends to when another log merges in — snapshot under the lock.
        List<CorrelatedEncounter> fights;
        string zone;
        lock (manager.Sync)
        {
            fights = [.. selected
                .Where(n => n.IsFight)
                .Select(n => n.Fight)
                .OfType<CorrelatedEncounter>()
                .Distinct()
                .OrderBy(f => f.StartTime)];
            zone = string.Join(" / ", fights.Select(f => f.Zone).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        if (fights.Count < 2)
        {
            SelectNode(focus ?? selected[^1]);
            return;
        }
        _pinnedFight = new AggregateFights(zone, SelectionLabel, fights);
        FollowLive = false;
        FollowSelectionInOverlay();
        RefreshGrid();
    }

    /// <summary>Whether a freshly rebuilt tree node should show selected:
    /// the pinned fight itself, or a member of the pinned multi-selection.</summary>
    public bool IsTreeSelected(ParseNode node)
    {
        if (FollowLive || node.Fight is not CorrelatedEncounter fight)
            return false;
        return ReferenceEquals(_pinnedFight, fight)
            || (_pinnedFight is AggregateFights { Label: SelectionLabel } selection && selection.Fights.Contains(fight));
    }

    /// <summary>Context-menu target: a right-clicked fight that belongs to
    /// the current multi-selection stands for the whole selection (Explorer
    /// semantics — delete/copy/upload act on everything selected).</summary>
    private ParseNode? WidenToSelection(ParseNode? node)
    {
        if (FollowLive
            || node?.Fight is not CorrelatedEncounter fight
            || _pinnedFight is not AggregateFights { Label: SelectionLabel } selection
            || !selection.Fights.Contains(fight))
            return node;
        return new ParseNode
        {
            Title = $"{selection.Zone} — {selection.Label}",
            Fight = selection,
            GroupFights = selection.Fights,
            IsDeletable = true,
        };
    }

    private void SelectNode(ParseNode? value)
    {
        if (value?.Fight is null)
            return;
        if (value.Fight is LiveFollow)
        {
            // The "⚔ Live" tree node: resume following the active fight.
            FollowLive = true;
            _pinnedFight = null;
        }
        else
        {
            _pinnedFight = value.Fight;
            FollowLive = false;
            // Zone summary: a fight-scoped drill or report makes no sense
            // zone-wide — close whatever overlays the grid.
            if (value.Fight is ZoneFights)
                CloseOverlay();
        }
        FollowSelectionInOverlay();
        RefreshGrid();
    }

    /// <summary>Arrow click on a zone header: collapse/expand only — the
    /// header body selects the zone summary instead.</summary>
    [RelayCommand]
    private void ToggleZone(ParseNode? node)
    {
        if (node?.GroupKey is not { } groupKey)
            return;
        if (groupKey.StartsWith("day|", StringComparison.Ordinal))
        {
            var day = DateTime.TryParse(groupKey[4..], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : DateTime.Now.Date;
            var expandedNow = !_dateExpandOverrides.TryGetValue(groupKey, out var expand)
                ? day == DateTime.Now.Date
                : expand;
            _dateExpandOverrides[groupKey] = !expandedNow;
        }
        else if (!_collapsedZones.Remove(groupKey))
        {
            _collapsedZones.Add(groupKey);
        }
        RebuildTree();
    }

    /// <summary>Keep any open report/log view coherent with the newly
    /// selected fight: combatant reports re-run for the same character
    /// (closing if they aren't in the fight); fight-scoped reports and
    /// standalone log views close back to the summary.</summary>
    private void FollowSelectionInOverlay()
    {
        if (LogLevel && _detailKey is null)
        {
            CloseOverlay();
            return;
        }
        if (!ReportLevel)
        {
            // A plain drill follows the new fight — but when that fight
            // doesn't contain the drilled combatant at all, the panel used
            // to freeze on the previous fight's data. Close it instead.
            if (DetailOpen && _detailKey is not null)
            {
                bool inFight;
                lock (manager.Sync)
                {
                    inFight = FightContains(ResolveFight(), _detailKey);
                }
                if (!inFight)
                    CloseOverlay();
            }
            return;
        }
        if (_reportScope == -1)
        {
            CloseOverlay();
            return;
        }
        if (_reportScope <= 0)
            return; // lookup — history-wide, fight-independent
        bool present;
        lock (manager.Sync)
        {
            present = FightContains(ResolveFight(), _reportKey!);
        }
        if (!present)
        {
            CloseOverlay();
            return;
        }
        var proxy = new CombatantRow { Key = _reportKey! };
        proxy.Name = _reportName ?? _reportKey!;
        switch (_reportScope)
        {
            case 1: DeathReport(proxy); break;
            case 2: AvoidanceReport(proxy); break;
            case 3: SpecialReport(proxy); break;
            case 4: SourcesReport(proxy); break;
        }
    }

    private static bool FightContains(object? fight, string key) =>
        fight is IFightView view && view.ContainsCombatant(key);

    private void CloseOverlay()
    {
        _detailSig = default;
        DetailOpen = false;
        ReportLevel = false;
        LogLevel = false;
        SwingLevel = false;
        ReportChartVisible = false;
        DrillChartVisible = false;
        _detailKey = null;
        _reportScope = 0;
    }

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (column is null)
            return;
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = column != "Name" && column != "Class";
        }
        RefreshGrid();
    }

    private DateTimeOffset _lastBusyRefresh;

    /// <summary>Shell tick (~100ms on the UI thread).</summary>
    public void Refresh()
    {
        // During a bulk import every tick would rebuild a churning tree and
        // re-classify a churning "current" fight, starving the pump of the
        // sync lock — throttle the whole page to ~0.5Hz until caught up.
        if (manager.ImportBusy)
        {
            var now = DateTimeOffset.Now;
            if (now - _lastBusyRefresh < TimeSpan.FromSeconds(2))
                return;
            _lastBusyRefresh = now;
        }
        RebuildTreeIfChanged();
        RefreshGrid();
    }

}
