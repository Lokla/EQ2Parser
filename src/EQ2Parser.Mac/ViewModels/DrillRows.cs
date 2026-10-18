using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>A row of the drill's ability table, updated in place.</summary>
public sealed partial class AbilityRowVm : ObservableObject
{
    private static readonly IBrush Normal = Brushes.White;
    private static readonly IBrush Header = new SolidColorBrush(Color.Parse("#C8A96E"));

    /// <summary>The bucket or ability name the engine knows it by — what a
    /// deeper level looks up. <see cref="Name"/> is only its label.</summary>
    public string Key { get; private set; } = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _isDivider;

    [ObservableProperty]
    private IBrush _nameBrush = Normal;

    [ObservableProperty]
    private string _hits = "";

    [ObservableProperty]
    private string _critPct = "";

    [ObservableProperty]
    private string _max = "";

    [ObservableProperty]
    private string _average = "";

    [ObservableProperty]
    private string _total = "";

    [ObservableProperty]
    private string _dps = "";

    [ObservableProperty]
    private string _share = "";

    [ObservableProperty]
    private string _every = "";

    [ObservableProperty]
    private string _types = "";

    [ObservableProperty]
    private double _bar;

    /// <summary><paramref name="shareOf"/>: the table's total when a
    /// share column makes sense (abilities in one bucket), else null.</summary>
    public void Set(AbilityData a, double top, double? shareOf)
    {
        Key = a.Name;
        Name = a.IsDivider ? a.Name : EQ2Parser.Mac.ViewModels.Drill.BucketLabel(a.Name);
        IsDivider = a.IsDivider;
        NameBrush = a.IsDivider || a.Source == "all" ? Header : Normal;
        if (a.IsDivider)
        {
            Hits = CritPct = Max = Average = Total = Dps = Share = Every = Types = "";
            Bar = 0;
            return;
        }
        Hits = a.Hits == a.Swings ? a.Hits.ToString("N0", CultureInfo.CurrentCulture) : $"{a.Hits:N0}/{a.Swings:N0}";
        CritPct = a.Hits > 0 ? $"{100.0 * a.Crits / a.Hits:0}%" : "";
        Max = a.Max > 0 ? FightTable.Compact(a.Max) : "";
        Average = a.Hits > 0 && a.Total > 0 ? FightTable.Compact((double)a.Total / a.Hits) : "";
        Total = a.Total > 0 ? FightTable.Compact(a.Total) : "";
        Dps = a.Dps > 0 ? FightTable.Compact(a.Dps) : "";
        Share = shareOf is { } of && a.Source != "all" && a.Total > 0 ? $"{100.0 * a.Total / of:0.0}%" : "";
        Every = a.FreqSeconds is { } f ? $"{f:0.0} s" : "";
        Types = a.Types;
        Bar = a.Source == "all" ? 0 : a.Total / top;
    }
}

/// <summary>A swing at the deepest drill level.</summary>
public sealed class SwingRowVm(SwingData s)
{
    public string Time { get; } = s.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string Attacker { get; } = s.Attacker;
    public string Victim { get; } = s.Victim;
    public string Ability { get; } = s.Ability;
    public string Damage { get; } = s.Number >= 0 ? FightTable.Compact(s.Number) : s.Damage;
    public string Crit { get; } = s.Critical ? "crit" : "";
    public string Type { get; } = s.Type;
    public string Special { get; } = s.Special;
}
