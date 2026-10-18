using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>A row of the fight list: a fight in progress or a finished one.</summary>
public sealed class FightItem(IFightView fight, bool live)
{
    public IFightView Fight { get; } = fight;
    public bool Live { get; } = live;
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public IBrush TitleBrush { get; set; } = Brushes.White;
}

/// <summary>A grid row, updated in place each tick so only changed cells repaint.</summary>
public sealed partial class CombatantRowVm : ObservableObject
{
    /// <summary>The combatant's dictionary key — what the drill opens on.</summary>
    public string Key { get; set; } = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _className = "";

    [ObservableProperty]
    private IBrush _classBrush = Brushes.Gray;

    [ObservableProperty]
    private string _duration = "";

    [ObservableProperty]
    private string _damage = "";

    [ObservableProperty]
    private string _percent = "";

    [ObservableProperty]
    private string _dps = "";

    [ObservableProperty]
    private string _hps = "";

    [ObservableProperty]
    private string _taken = "";

    [ObservableProperty]
    private string _deaths = "";

    [ObservableProperty]
    private double _bar;
}

/// <summary>
/// The Fights page: the list of fights (in progress first, then the
/// archive, newest first) and the combatant grid of the one shown. By
/// default it follows the action — the fight in progress, else the newest
/// finished one; picking a fight in the list pins it until "Follow live".
/// </summary>
public sealed partial class FightsViewModel(SourceManager manager) : ObservableObject
{
    private const int MaxListed = 300;

    private (long Version, int LiveCount, IFightView? FirstLive) _listSig;
    private bool _settingSelection;
    private IFightView? _shown;

    public ObservableCollection<FightItem> Fights { get; } = [];
    public ObservableCollection<CombatantRowVm> Allies { get; } = [];
    public ObservableCollection<CombatantRowVm> Enemies { get; } = [];

    [ObservableProperty]
    private FightItem? _selectedFight;

    [ObservableProperty]
    private bool _followLive = true;

    [ObservableProperty]
    private string _breadcrumb = "No fights yet — waiting for combat in a watched log.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadShownCommand))]
    private bool _isLive;

    /// <summary>Send every source's view of the fight shown, the WPF fight
    /// tree's "Upload to EQ2Lexicon": the explicit click is the consent, so
    /// it works with auto-upload off. A fight in progress is not offered.</summary>
    [RelayCommand(CanExecute = nameof(CanUploadShown))]
    private void UploadShown()
    {
        List<Encounter> sources;
        lock (manager.Sync)
        {
            sources = _shown switch
            {
                Core.Correlation.CorrelatedEncounter merged => [.. merged.Sources],
                Encounter { Active: false } single => [single],
                _ => [],
            };
        }
        if (sources.Count > 0)
            manager.Uploads.UploadFight(sources);
    }

    private bool CanUploadShown() => !IsLive && _shown is not null;

    // ---- drill-down ----

    public ObservableCollection<AbilityRowVm> AbilityRows { get; } = [];
    public ObservableCollection<SwingRowVm> SwingRows { get; } = [];

    [ObservableProperty]
    private bool _drillOpen;

    [ObservableProperty]
    private bool _drillShowsSwings;

    [ObservableProperty]
    private string _drillTitle = "";

    [ObservableProperty]
    private string _drillHint = "";

    private string? _drillKey;
    private string? _drillBucket;
    private string? _drillAbility;
    private (IFightView? Fight, int Count, long LastTicks) _swingSig;

    /// <summary>Open the drill on a combatant of the fight shown.</summary>
    public void OpenDrill(string key)
    {
        _drillKey = key;
        _drillBucket = null;
        _drillAbility = null;
        DrillOpen = true;
        Tick();
    }

    /// <summary>One level deeper from a table row: bucket → abilities →
    /// swings. Dividers and the deepest level go nowhere.</summary>
    public void DrillInto(AbilityRowVm row)
    {
        if (row.IsDivider || _drillKey is null)
            return;
        if (_drillBucket is null)
            _drillBucket = row.Key;
        else if (_drillAbility is null)
            _drillAbility = row.Key;
        else
            return;
        Tick();
    }

    [RelayCommand]
    private void DrillBack()
    {
        if (_drillAbility is not null)
            _drillAbility = null;
        else if (_drillBucket is not null)
            _drillBucket = null;
        else
        {
            DrillClose();
            return;
        }
        Tick();
    }

    [RelayCommand]
    private void DrillClose()
    {
        DrillOpen = false;
        _drillKey = null;
        AbilityRows.Clear();
        SwingRows.Clear();
    }

    partial void OnSelectedFightChanged(FightItem? value)
    {
        if (_settingSelection || value is null)
            return;
        FollowLive = value.Live;
        Tick();
    }

    [RelayCommand]
    private void Follow()
    {
        FollowLive = true;
        Tick();
    }

    /// <summary>The UI tick: refresh the list when it changed and the grid always.</summary>
    public void Tick()
    {
        List<RowData> allies;
        List<RowData> enemies;
        string breadcrumb;
        bool live;
        DrillData? drill = null;
        lock (manager.Sync)
        {
            var liveFights = manager.Sources
                .Select(s => s.Engine.ActiveEncounter)
                .Where(e => e is { Active: true })
                .Cast<Encounter>()
                .OrderByDescending(e => e.Damage)
                .ToList();
            var history = manager.Correlator.History;
            var sig = (manager.Correlator.Version, liveFights.Count, (IFightView?)liveFights.FirstOrDefault());
            if (sig != _listSig)
            {
                _listSig = sig;
                RebuildList(liveFights, history);
            }

            var fight = FollowLive
                ? (IFightView?)liveFights.FirstOrDefault() ?? (history.Count > 0 ? history[^1] : null)
                : SelectedFight?.Fight;
            if (fight is null)
                return;
            _shown = fight;
            live = fight is Encounter { Active: true };
            (allies, enemies) = FightTable.Snapshot(fight, manager.Classifier);
            drill = DrillOpen && _drillKey is not null
                ? Drill.Snapshot(fight, _drillKey, _drillBucket, _drillAbility, manager.Classifier)
                : null;
            var zone = string.IsNullOrEmpty(fight.Zone) ? "" : $"{fight.Zone}  |  ";
            var title = live && fight.Title == Encounter.PlaceholderTitle ? "Combat in progress" : fight.Title;
            breadcrumb = $"{zone}{title}  —  {FightTable.Span(fight.Duration)},  {FightTable.Compact(fight.EncDps)} enc DPS";
        }
        Breadcrumb = breadcrumb;
        IsLive = live;
        SelectShown();
        Apply(Allies, allies);
        Apply(Enemies, enemies);
        if (DrillOpen)
            ApplyDrill(drill);
    }

    private void ApplyDrill(DrillData? drill)
    {
        if (drill is null)
        {
            DrillTitle = "Not in this fight";
            DrillHint = "The combatant is not part of the fight shown. Back or Close returns to the fight.";
            AbilityRows.Clear();
            SwingRows.Clear();
            return;
        }
        DrillTitle = drill.Title;
        DrillShowsSwings = drill.Swings is not null;
        if (drill.Table is { } table)
        {
            SwingRows.Clear();
            _swingSig = default;
            DrillHint = _drillBucket is null ? "Double-click a row for its abilities." : "Double-click a row for its swings.";
            var top = Math.Max(1.0, table.Where(t => !t.IsDivider && t.Source != "all").Select(t => (double)t.Total).DefaultIfEmpty(1).Max());
            var total = Math.Max(1.0, table.Where(t => !t.IsDivider && t.Source != "all").Sum(t => (double)t.Total));
            for (var i = 0; i < table.Count; i++)
            {
                if (i >= AbilityRows.Count)
                    AbilityRows.Add(new AbilityRowVm());
                AbilityRows[i].Set(table[i], top, _drillBucket is null ? null : total);
            }
            while (AbilityRows.Count > table.Count)
                AbilityRows.RemoveAt(AbilityRows.Count - 1);
        }
        else if (drill.Swings is { } swings)
        {
            AbilityRows.Clear();
            DrillHint = swings.Count >= 2000 ? "The first 2,000 swings." : $"{swings.Count} swing(s).";
            // Swings only ever grow; rebuild when they did.
            var sig = (_shown, swings.Count, swings.Count > 0 ? swings[^1].Time.UtcTicks : 0);
            if (sig == _swingSig)
                return;
            _swingSig = sig;
            SwingRows.Clear();
            foreach (var sw in swings)
                SwingRows.Add(new SwingRowVm(sw));
        }
    }

    private void RebuildList(List<Encounter> liveFights, IReadOnlyList<Core.Correlation.CorrelatedEncounter> history)
    {
        _settingSelection = true;
        try
        {
            Fights.Clear();
            foreach (var e in liveFights)
            {
                Fights.Add(new FightItem(e, live: true)
                {
                    Title = "● " + (e.Title == Encounter.PlaceholderTitle ? "Combat in progress" : e.Title),
                    Detail = $"{e.Zone} · {e.OwnerName}",
                    TitleBrush = new SolidColorBrush(Color.Parse("#4ADE80")),
                });
            }
            for (var i = history.Count - 1; i >= 0 && history.Count - i <= MaxListed; i--)
            {
                var f = history[i];
                Fights.Add(new FightItem(f, live: false)
                {
                    Title = f.Title,
                    Detail = $"{f.StartTime.ToLocalTime():HH:mm} · {FightTable.Span(f.Duration)} · {FightTable.Compact(f.EncDps)} · {f.Zone}",
                    TitleBrush = new SolidColorBrush(Color.Parse(ClassColors.ForOutcome(f.GetSuccessLevel()))),
                });
            }
        }
        finally
        {
            _settingSelection = false;
        }
    }

    /// <summary>Keep the list's highlight on the fight the grid shows.</summary>
    private void SelectShown()
    {
        var item = Fights.FirstOrDefault(f => ReferenceEquals(f.Fight, _shown));
        if (ReferenceEquals(item, SelectedFight))
            return;
        _settingSelection = true;
        try
        {
            SelectedFight = item;
        }
        finally
        {
            _settingSelection = false;
        }
    }

    private static void Apply(ObservableCollection<CombatantRowVm> rows, List<RowData> snapshot)
    {
        var total = Math.Max(1.0, snapshot.Sum(r => (double)r.Damage));
        var top = Math.Max(1.0, snapshot.Count > 0 ? snapshot.Max(r => (double)r.Damage) : 1);
        for (var i = 0; i < snapshot.Count; i++)
        {
            var data = snapshot[i];
            if (i >= rows.Count)
                rows.Add(new CombatantRowVm());
            var row = rows[i];
            row.Key = data.Key;
            row.Name = data.Name;
            row.ClassName = data.ClassName;
            row.ClassBrush = new SolidColorBrush(Color.Parse(data.ColorHex));
            row.Duration = FightTable.Span(TimeSpan.FromSeconds(data.Seconds));
            row.Damage = FightTable.Compact(data.Damage);
            row.Percent = $"{100.0 * data.Damage / total:F0}%";
            row.Dps = FightTable.Compact(data.Dps);
            row.Hps = data.Hps > 0 ? FightTable.Compact(data.Hps) : "";
            row.Taken = data.Taken > 0 ? FightTable.Compact(data.Taken) : "";
            row.Deaths = data.Deaths > 0 ? data.Deaths.ToString() : "";
            row.Bar = data.Damage / top;
        }
        while (rows.Count > snapshot.Count)
            rows.RemoveAt(rows.Count - 1);
    }
}
