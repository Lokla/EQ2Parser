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
    private bool _isLive;

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
            var zone = string.IsNullOrEmpty(fight.Zone) ? "" : $"{fight.Zone}  |  ";
            var title = live && fight.Title == Encounter.PlaceholderTitle ? "Combat in progress" : fight.Title;
            breadcrumb = $"{zone}{title}  —  {FightTable.Span(fight.Duration)},  {FightTable.Compact(fight.EncDps)} enc DPS";
        }
        Breadcrumb = breadcrumb;
        IsLive = live;
        SelectShown();
        Apply(Allies, allies);
        Apply(Enemies, enemies);
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
