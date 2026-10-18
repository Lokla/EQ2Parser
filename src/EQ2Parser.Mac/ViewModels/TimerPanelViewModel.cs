using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>One running timer bar, updated in place.</summary>
public sealed partial class TimerBarVm : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _combatant = "";

    [ObservableProperty]
    private string _timeLeft = "";

    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private IBrush _fill = Brushes.SteelBlue;

    [ObservableProperty]
    private bool _warning;
}

/// <summary>
/// The running spell timers, soonest first — the WPF timer overlays'
/// content, shown inside the window (both panels in one list). Refreshed
/// on the fast tick.
/// </summary>
public sealed partial class TimerPanelViewModel(SourceManager manager) : ObservableObject
{
    private static readonly IBrush WarningFill = new SolidColorBrush(Color.Parse("#F87171"));
    private readonly Dictionary<int, IBrush> _brushes = [];

    public ObservableCollection<TimerBarVm> Bars { get; } = [];

    [ObservableProperty]
    private bool _isEmpty = true;

    public void Tick(DateTimeOffset now)
    {
        // Panel 1 and 2 in one list; a timer routed to both shows once.
        var bars = manager.SpellTimers.Snapshot(now, 1);
        foreach (var bar in manager.SpellTimers.Snapshot(now, 2))
        {
            if (!bars.Contains(bar))
                bars.Add(bar);
        }
        bars.Sort((a, b) => a.SecondsLeft.CompareTo(b.SecondsLeft));

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            if (i >= Bars.Count)
                Bars.Add(new TimerBarVm());
            var row = Bars[i];
            var left = Math.Max(0, bar.SecondsLeft);
            row.Name = bar.SwipedPercent > 0 ? $"{bar.Name} (+{bar.SwipedPercent}%)" : bar.Name;
            row.Combatant = bar.Combatant;
            row.TimeLeft = left >= 60 ? TimeSpan.FromSeconds(Math.Ceiling(left)).ToString(@"m\:ss") : $"{Math.Ceiling(left):0}";
            row.Fraction = bar.DurationSeconds > 0 ? Math.Clamp(left / bar.DurationSeconds, 0, 1) : 0;
            row.Warning = left <= bar.WarningSeconds;
            row.Fill = row.Warning ? WarningFill : BrushFor(bar.FillColorArgb);
        }
        while (Bars.Count > bars.Count)
            Bars.RemoveAt(Bars.Count - 1);
        IsEmpty = Bars.Count == 0;
    }

    private IBrush BrushFor(int argb)
    {
        if (!_brushes.TryGetValue(argb, out var brush))
        {
            var c = Color.FromUInt32(unchecked((uint)argb));
            brush = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
            _brushes[argb] = brush;
        }
        return brush;
    }
}
