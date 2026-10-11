using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using Microsoft.Win32;

namespace EQ2Parser.App.ViewModels;

public sealed partial class SourceRow(LogSource source) : ObservableObject
{
    public LogSource Source { get; } = source;
    public string Owner => Source.Owner;
    public string Path => Source.Path;

    /// <summary>The pill beside the owner's name: how this log came to
    /// be tailed.</summary>
    public string Mode => Source.AutoDiscovered
        ? Loc.Get("Sources_ModeAuto")
        : Source.ParseFromStart ? Loc.Get("Sources_ModeFullFile") : Loc.Get("Sources_ModeLiveTail");

    [ObservableProperty]
    private string _status = "";
}

/// <summary>Manage tailed log files. "Add live" starts at the end of the
/// file; "Add + parse existing" chews through the whole log first (useful
/// for history review of a past raid night).</summary>
public sealed partial class SourcesViewModel(SourceManager manager) : ObservableObject
{
    public ObservableCollection<SourceRow> Rows { get; } = [];
    public ObservableCollection<string> WatchedFolders { get; } = [.. manager.WatchedFolders];

    [RelayCommand]
    private void AddLive() => Add(parseFromStart: false);

    [RelayCommand]
    private void AddWithHistory() => Add(parseFromStart: true);

    [RelayCommand]
    private void WatchFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("Sources_ChooseLogsFolderTitle"),
        };
        if (dialog.ShowDialog() != true)
            return;
        manager.AddWatchedFolder(dialog.FolderName);
        if (!WatchedFolders.Contains(dialog.FolderName))
            WatchedFolders.Add(dialog.FolderName);
    }

    [RelayCommand]
    private void UnwatchFolder(string? folder)
    {
        if (folder is null)
            return;
        manager.RemoveWatchedFolder(folder);
        WatchedFolders.Remove(folder);
        SyncFromManager();
    }

    [RelayCommand]
    private void Remove(SourceRow? row)
    {
        if (row is null)
            return;
        manager.Remove(row.Source);
        Rows.Remove(row);
        manager.PersistSources();
    }

    private void Add(bool parseFromStart)
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("Sources_ChooseLogFileTitle"),
            Filter = Loc.Get("Sources_LogFileFilter"),
        };
        if (dialog.ShowDialog() != true)
            return;
        if (manager.Sources.Any(s => string.Equals(s.Path, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
            return;
        var source = manager.Add(dialog.FileName, parseFromStart);
        Rows.Add(new SourceRow(source));
        manager.PersistSources();
    }

    /// <summary>Shell tick: pick up folder-watcher discoveries, then
    /// refresh per-source status lines. One lock for the whole snapshot
    /// (this used to take Sync once per source per 100ms tick), and the
    /// bound Status setters run after it is released.</summary>
    public void Refresh()
    {
        if (manager.Sources.Count != Rows.Count)
            SyncFromManager();
        var statuses = new string[Rows.Count];
        lock (manager.Sync)
        {
            for (var i = 0; i < Rows.Count; i++)
                statuses[i] = StatusFor(Rows[i].Source);
        }
        for (var i = 0; i < Rows.Count && i < statuses.Length; i++)
            Rows[i].Status = statuses[i];
    }

    /// <summary>One source's status line. The caller holds the manager's
    /// Sync lock (engine state is read). "error:" means the tail has
    /// STOPPED. A source that only had to skip lines is still reading, so
    /// its skips ride on the end of the normal counters instead — calling
    /// it dead would send the user off to re-add a log that is working.
    ///
    /// Each part is a whole dictionary string and the parts are joined
    /// with <see cref="StatusSeparator"/>, so no language has to fit its
    /// words around another's order. Two things in the line stay as they
    /// come: the error message (an exception's own text) and the exception
    /// type name, which is what the user quotes from the crashes folder.</summary>
    internal static string StatusFor(LogSource source)
    {
        if (source.Error is { } error)
            return Loc.Format("Sources_StatusError", error.Message);
        var status = Loc.Format(
            "Sources_StatusCounts",
            source.Processor.LinesSeen, source.Processor.LinesMatched, source.Engine.History.Count);
        if (source.Engine.InCombat)
            status += StatusSeparator + Loc.Get("Sources_StatusInCombat");
        var skipped = source.SkippedLines;
        if (skipped > 0)
            status += StatusSeparator + Loc.Format("Sources_StatusSkipped", skipped, source.LastLineError?.GetType().Name);
        return status;
    }

    private const string StatusSeparator = " · ";

    public void SyncFromManager()
    {
        Rows.Clear();
        foreach (var source in manager.Sources)
            Rows.Add(new SourceRow(source));
    }
}
