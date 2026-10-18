using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>A trigger as listed. Lexicon-pack triggers (Source set) can be
/// switched on and off but not edited — the pack owns them.</summary>
public sealed partial class TriggerRow(Trigger trigger, TriggersViewModel owner) : ObservableObject
{
    public Trigger Trigger { get; private set; } = trigger;
    public string Key => Trigger.Key;
    public string Scope => Trigger.ZoneScope;
    public string Pattern => Trigger.RegexText;
    public string Sound => TriggersViewModel.Describe(Trigger);
    public string Origin => Trigger.Source.Length > 0 ? "Lexicon" : "Mine";

    [ObservableProperty]
    private bool _enabled = trigger.Enabled;

    partial void OnEnabledChanged(bool value)
    {
        if (value != Trigger.Enabled)
            owner.SetEnabled(this, value);
    }

    internal void Refresh(Trigger trigger)
    {
        Trigger = trigger;
        Enabled = trigger.Enabled;
    }
}

public sealed record SoundOption(TriggerSound Value, string Label);

/// <summary>
/// The Triggers page: every trigger (yours and the synced Lexicon pack),
/// an editor for your own, a test button that plays the alert exactly as a
/// match would, ACT share-format import from the clipboard, and the most
/// recent alerts that fired.
/// </summary>
public sealed partial class TriggersViewModel : ObservableObject
{
    private readonly SourceManager _manager;
    private readonly Func<Task<string?>> _readClipboard;

    public ObservableCollection<TriggerRow> Triggers { get; } = [];
    public ObservableCollection<string> RecentAlerts { get; } = [];

    public IReadOnlyList<SoundOption> SoundOptions { get; } =
    [
        new(TriggerSound.Tts, "Speak text"),
        new(TriggerSound.Beep, "Chime"),
        new(TriggerSound.WavFile, "Sound file"),
        new(TriggerSound.None, "Silent"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private TriggerRow? _selected;

    [ObservableProperty]
    private string _regex = "";

    [ObservableProperty]
    private string _category = "";

    [ObservableProperty]
    private string _zone = "";

    [ObservableProperty]
    private SoundOption? _sound;

    [ObservableProperty]
    private string _soundData = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _filter = "";

    public bool CanEdit => Selected is null || Selected.Trigger.Source.Length == 0;

    public TriggersViewModel(SourceManager manager, Func<Task<string?>> readClipboard)
    {
        _manager = manager;
        _readClipboard = readClipboard;
        _sound = SoundOptions[0];
        manager.Triggers.DefinitionsChanged += () => Dispatcher.UIThread.Post(Reload);
        manager.Triggers.AlertFired += fired => Dispatcher.UIThread.Post(() =>
        {
            var what = fired.TtsText is { Length: > 0 } text ? $"“{text}”" : Describe(fired.Trigger);
            RecentAlerts.Insert(0, $"{DateTime.Now:HH:mm:ss}  {what}   ← {fired.Trigger.RegexText}");
            while (RecentAlerts.Count > 50)
                RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
        });
        Reload();
    }

    internal static string Describe(Trigger t) => t.SoundType switch
    {
        TriggerSound.Tts => $"say “{t.SoundData}”",
        TriggerSound.Beep => "chime",
        TriggerSound.WavFile => $"play {Path.GetFileName(t.SoundData)}",
        _ => "silent",
    };

    partial void OnFilterChanged(string value) => Reload();

    partial void OnSelectedChanged(TriggerRow? value)
    {
        if (value is null)
            return;
        var t = value.Trigger;
        Regex = t.RegexText;
        Category = t.Category;
        Zone = t.Zone;
        Sound = SoundOptions.FirstOrDefault(o => o.Value == t.SoundType) ?? SoundOptions[0];
        SoundData = t.SoundData;
        Status = t.Source.Length > 0 ? "Lexicon pack trigger — it can be switched on or off, not edited." : "";
    }

    private void Reload()
    {
        var selectedKey = Selected?.Key;
        var definitions = _manager.Triggers.Definitions
            .Where(t => Filter.Length == 0
                || t.RegexText.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || t.ZoneScope.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                || t.SoundData.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Source.Length > 0)
            .ThenBy(t => t.ZoneScope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.RegexText, StringComparer.Ordinal)
            .ToList();
        Triggers.Clear();
        foreach (var t in definitions)
            Triggers.Add(new TriggerRow(t, this));
        if (selectedKey is not null)
            Selected = Triggers.FirstOrDefault(r => r.Key == selectedKey);
    }

    internal void SetEnabled(TriggerRow row, bool enabled) => _manager.Triggers.SetEnabled(row.Key, enabled);

    [RelayCommand]
    private void New()
    {
        Selected = null;
        Regex = "";
        Category = "";
        Zone = "";
        Sound = SoundOptions[0];
        SoundData = "";
        Status = "New trigger: enter a regular expression and what to do when a log line matches it.";
    }

    /// <summary>The editor's trigger; the fields the editor does not show
    /// (timer link, cooldown, zone restriction) carry over from the one
    /// being edited.</summary>
    private Trigger? Build()
    {
        if (Regex.Trim().Length == 0)
        {
            Status = "The pattern is empty.";
            return null;
        }
        try
        {
            var previous = Selected?.Trigger;
            return new Trigger(Regex.Trim(), Category, Zone)
            {
                Enabled = previous?.Enabled ?? true,
                RestrictToCategoryZone = previous?.RestrictToCategoryZone ?? false,
                SoundType = Sound?.Value ?? TriggerSound.Tts,
                SoundData = SoundData,
                StartsTimer = previous?.StartsTimer ?? false,
                TimerName = previous?.TimerName ?? "",
                AudioCooldown = previous?.AudioCooldown ?? TimeSpan.FromSeconds(1),
            };
        }
        catch (ArgumentException ex)
        {
            Status = $"Not a valid regular expression: {ex.Message}";
            return null;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanEdit || Build() is not { } trigger)
            return;
        _manager.Triggers.AddOrUpdate(trigger, Selected?.Key);
        Selected = Triggers.FirstOrDefault(r => r.Key == trigger.Key);
        Status = "Saved.";
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } row || !CanEdit)
            return;
        var removed = row.Trigger;
        if (_manager.Triggers.Remove(row.Key))
        {
            _manager.Undo.Push(() => _manager.Triggers.AddOrUpdate(removed));
            Status = "Deleted. Undo puts it back.";
            New();
        }
    }

    [RelayCommand]
    private void Undo()
    {
        Status = _manager.Undo.TryUndo() ? "Restored." : "Nothing to undo.";
    }

    /// <summary>Play the alert the way a match would.</summary>
    [RelayCommand]
    private void Test()
    {
        switch (Sound?.Value)
        {
            case TriggerSound.Tts when SoundData.Length > 0:
                _manager.Audio.SpeakTest(SoundData);
                break;
            case TriggerSound.Beep:
                _manager.Audio.PlayChime();
                break;
            case TriggerSound.WavFile:
                _manager.Audio.PlayFile(SoundData);
                break;
            default:
                Status = "Nothing to play.";
                break;
        }
    }

    /// <summary>ACT's share format: a &lt;Trigger …/&gt; (or a &lt;Spell …/&gt;
    /// timer) copied from ACT or from chat.</summary>
    [RelayCommand]
    private async Task ImportFromClipboard()
    {
        var text = await _readClipboard();
        if (string.IsNullOrWhiteSpace(text))
        {
            Status = "The clipboard is empty.";
            return;
        }
        switch (ActShareFormat.TryImport(text))
        {
            case Trigger trigger:
                _manager.Triggers.AddOrUpdate(trigger);
                Selected = Triggers.FirstOrDefault(r => r.Key == trigger.Key);
                Status = "Imported an ACT trigger.";
                break;
            case TimerDefinition timer:
                _manager.SpellTimers.AddOrUpdate(timer);
                Status = $"Imported the ACT spell timer “{timer.Name}”.";
                break;
            default:
                Status = "The clipboard does not hold an ACT trigger or spell timer.";
                break;
        }
    }
}
