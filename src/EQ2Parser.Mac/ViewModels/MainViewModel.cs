using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Services;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>The window's pages and the one-second UI tick that refreshes
/// them from the engine (the WPF shell's tick, at the same rate).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly SourceManager _manager;
    private readonly DispatcherTimer _timer;

    public FightsViewModel Fights { get; }
    public TriggersViewModel Triggers { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private string _statusLine = "";

    /// <summary>The latest notification (callouts, "already have it").</summary>
    [ObservableProperty]
    private string _notification = "";

    public MainViewModel(SourceManager manager, TopLevel window)
    {
        _manager = manager;
        Fights = new FightsViewModel(manager);
        Triggers = new TriggersViewModel(manager, async () =>
            window.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null);
        Settings = new SettingsViewModel(manager,
            async () =>
            {
                var picked = await window.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions { Title = "Folder holding EQ2 logs" });
                return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            },
            async () =>
            {
                var picked = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "EQ2 log file",
                    FileTypeFilter = [new FilePickerFileType("EQ2 logs") { Patterns = ["eq2log_*.txt", "*.txt"] }],
                });
                return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            });
        manager.CalloutAnnounced += text => Dispatcher.UIThread.Post(() => Notification = $"{DateTime.Now:HH:mm}  {text}");
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        Tick();
    }

    private void Tick()
    {
        Fights.Tick();
        Settings.Tick();
        SessionJournal.Heartbeat(_manager.Sources.Count);
        var sources = _manager.Sources;
        StatusLine = sources.Count == 0
            ? (_manager.WatchedFolders.Count == 0
                ? "No logs yet — add a log folder in Settings."
                : $"Watching {_manager.WatchedFolders.Count} folder(s) — a log is picked up as soon as EQ2 writes to it.")
            : $"Reading {string.Join(", ", sources.Select(s => s.Owner))}";
    }
}
