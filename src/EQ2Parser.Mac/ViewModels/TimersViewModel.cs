using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>A timer definition as listed. Lexicon-pack timers can be
/// switched on and off and tested, not edited — the pack owns them.</summary>
public sealed partial class TimerRow(TimerDefinition definition, TimersViewModel owner) : ObservableObject
{
    public TimerDefinition Definition { get; } = definition;
    public string Key => Definition.Key;
    public string Name => Definition.Name;
    public string Scope => Definition.Zone.Length > 0 ? $"{Definition.Zone} / {Definition.Category}" : Definition.Category;
    public string Timing => $"{Definition.DurationSeconds} s, warn {Definition.WarningSeconds} s";
    public string Sounds => string.Join(" · ", new[] { Definition.StartSoundData, Definition.WarningSoundData }.Where(s => s.Length > 0));
    public string Origin => Definition.Source.Length > 0 ? "Lexicon" : "Mine";

    [ObservableProperty]
    private bool _enabled = definition.Enabled;

    partial void OnEnabledChanged(bool value)
    {
        if (value != Definition.Enabled)
            owner.SetEnabled(this, value);
    }
}

/// <summary>
/// The Timers page: every spell timer (yours and the Lexicon pack's), on/off,
/// Test (starts the bar the way a match would), and an editor for your own
/// timers. A timer starts when its name is cast — or from a trigger that
/// starts it.
/// </summary>
public sealed partial class TimersViewModel : ObservableObject
{
    private readonly SourceManager _manager;

    public ObservableCollection<TimerRow> Timers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private TimerRow? _selected;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _category = "";

    [ObservableProperty]
    private string _zone = "";

    [ObservableProperty]
    private string _duration = "30";

    [ObservableProperty]
    private string _warning = "10";

    [ObservableProperty]
    private string _startSound = "";

    [ObservableProperty]
    private string _warningSound = "tts";

    [ObservableProperty]
    private string _status = "";

    public bool CanEdit => Selected is null || Selected.Definition.Source.Length == 0;

    public TimersViewModel(SourceManager manager)
    {
        _manager = manager;
        manager.SpellTimers.DefinitionsChanged += () => Dispatcher.UIThread.Post(Reload);
        Reload();
    }

    partial void OnFilterChanged(string value) => Reload();

    partial void OnSelectedChanged(TimerRow? value)
    {
        if (value is null)
            return;
        var d = value.Definition;
        Name = d.Name;
        Category = d.Category;
        Zone = d.Zone;
        Duration = d.DurationSeconds.ToString(CultureInfo.InvariantCulture);
        Warning = d.WarningSeconds.ToString(CultureInfo.InvariantCulture);
        StartSound = d.StartSoundData;
        WarningSound = d.WarningSoundData;
        Status = d.Source.Length > 0 ? "Lexicon pack timer — it can be switched on or off and tested, not edited." : "";
    }

    private void Reload()
    {
        var selectedKey = Selected?.Key;
        var definitions = _manager.SpellTimers.Definitions
            .Where(d => Filter.Length == 0
                || d.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || d.Zone.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || d.Category.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Source.Length > 0)
            .ThenBy(d => d.Zone, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Timers.Clear();
        foreach (var d in definitions)
            Timers.Add(new TimerRow(d, this));
        if (selectedKey is not null)
            Selected = Timers.FirstOrDefault(r => r.Key == selectedKey);
    }

    internal void SetEnabled(TimerRow row, bool enabled) => _manager.SpellTimers.SetEnabled(row.Key, enabled);

    [RelayCommand]
    private void New()
    {
        Selected = null;
        Name = "";
        Category = "";
        Zone = "";
        Duration = "30";
        Warning = "10";
        StartSound = "";
        WarningSound = "tts";
        Status = "New timer: it starts whenever a spell or ability with this exact name is used.";
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanEdit)
            return;
        var name = Name.Trim();
        if (name.Length == 0)
        {
            Status = "The timer needs a name (the spell or ability that starts it).";
            return;
        }
        if (!int.TryParse(Duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) || duration < 1 || duration > 24 * 3600)
        {
            Status = "Duration: whole seconds, 1 or more.";
            return;
        }
        if (!int.TryParse(Warning, NumberStyles.Integer, CultureInfo.InvariantCulture, out var warning) || warning < 0)
        {
            Status = "Warning: whole seconds, 0 or more.";
            return;
        }
        // Fields this editor does not show carry over from the timer edited.
        var previous = Selected?.Definition ?? new TimerDefinition { Name = name };
        var category = Category.Trim();
        var definition = previous with
        {
            Name = name,
            Category = category.Length == 0 ? "General" : category,
            Zone = Zone.Trim(),
            DurationSeconds = duration,
            WarningSeconds = Math.Min(warning, duration),
            StartSoundData = StartSound.Trim(),
            WarningSoundData = WarningSound.Trim(),
            Source = "",
        };
        _manager.SpellTimers.AddOrUpdate(definition, Selected?.Key);
        Selected = Timers.FirstOrDefault(r => r.Key == definition.Key);
        Status = "Saved.";
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } row || !CanEdit)
            return;
        var removed = row.Definition;
        _manager.SpellTimers.Remove(row.Key);
        _manager.Undo.Push(() => _manager.SpellTimers.AddOrUpdate(removed));
        Status = "Deleted. Undo puts it back.";
        New();
    }

    [RelayCommand]
    private void Undo() => Status = _manager.Undo.TryUndo() ? "Restored." : "Nothing to undo.";

    /// <summary>Start the selected timer now — its bar appears on the
    /// Fights page and its sounds play as they would in a fight.</summary>
    [RelayCommand]
    private void Test()
    {
        if (Selected is { } row)
        {
            _manager.SpellTimers.StartTest(row.Definition);
            Status = $"Started “{row.Name}” — see the timer panel on the Fights page.";
        }
        else
        {
            Status = "Pick a timer to test.";
        }
    }
}
