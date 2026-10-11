using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.History;

namespace EQ2Parser.App.ViewModels;

/// <summary>One archived fight in the search results.</summary>
public sealed partial class ArchiveRow(EncounterSummary summary, bool loaded) : ObservableObject
{
    public EncounterSummary Summary { get; } = summary;

    public string WhenLabel => Summary.Start.LocalDateTime.ToString("ddd d MMM · HH:mm");
    public string Zone => Summary.Zone;
    public string Title => Summary.Title;

    /// <summary>Duration, damage and whose log it came from, plus a
    /// marker on trash. Each part is a whole dictionary string.</summary>
    public string DetailLabel
    {
        get
        {
            List<string> parts =
            [
                $"{TimeSpan.FromSeconds(Math.Max(0, Summary.DurationSeconds)):m\\:ss}",
                Loc.Format("Archive_DetailDamage", CombatantRow.Compact(Summary.Damage)),
                Loc.Format("Archive_DetailLog", Summary.Owner),
            ];
            if (!Summary.IsBoss)
                parts.Add(Loc.Get("Archive_DetailTrash"));
            return string.Join("  ·  ", parts);
        }
    }

    public Brush OutcomeBrush => Summary.Success switch
    {
        SuccessLevel.Win => ClassColors.OutcomeWin,
        SuccessLevel.Loss => ClassColors.OutcomeLoss,
        _ => ClassColors.OutcomePartial,
    };

    [ObservableProperty]
    private bool _isLoaded = loaded;
}

/// <summary>
/// The parse archive: search everything ever stored (bosses never age
/// out), pull fights back into the parser, or purge them for good.
/// </summary>
public sealed partial class ArchiveViewModel : ObservableObject
{
    private readonly SourceManager _manager;

    public ObservableCollection<ArchiveRow> Rows { get; } = [];

    /// <summary>In step with <see cref="RangeDays"/>, entry for entry.</summary>
    public IReadOnlyList<string> RangeChoices { get; } =
    [
        Loc.Get("Archive_RangeWeek"),
        Loc.Get("Archive_RangeMonth"),
        Loc.Get("Archive_RangeQuarter"),
        Loc.Get("Archive_RangeYear"),
        Loc.Get("Archive_RangeAll"),
    ];

    private static readonly int?[] RangeDays = [7, 30, 90, 365, null];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _rangeIndex = 2;

    [ObservableProperty]
    private string _resultLabel = "";

    public ArchiveViewModel(SourceManager manager)
    {
        _manager = manager;
        Search();
    }

    partial void OnRangeIndexChanged(int value) => Search();

    [RelayCommand]
    private void Search()
    {
        var days = RangeDays[Math.Clamp(RangeIndex, 0, RangeDays.Length - 1)];
        var since = days is { } d ? DateTimeOffset.Now.AddDays(-d) : (DateTimeOffset?)null;
        var text = SearchText.Trim();
        var results = _manager.History.Search(text.Length == 0 ? null : text, since);
        Rows.Clear();
        foreach (var summary in results)
            Rows.Add(new ArchiveRow(summary, _manager.History.IsLoaded(summary.Id)));
        var bosses = results.Count(s => s.IsBoss);
        ResultLabel = ResultLabelFor(results.Count, bosses);
    }

    /// <summary>The line under the search box: how many fights matched
    /// and how many of them are bosses — two whole strings, joined.</summary>
    internal static string ResultLabelFor(int fights, int bosses) =>
        fights == 0
            ? Loc.Get("Archive_NothingFound")
            : string.Join(
                " · ",
                Loc.Format(fights == 1 ? "Archive_FightsOne" : "Archive_FightsMany", fights),
                Loc.Format(bosses == 1 ? "Archive_BossesOne" : "Archive_BossesMany", bosses));

    [RelayCommand]
    private void LoadRow(ArchiveRow? row)
    {
        if (row is null || row.IsLoaded)
            return;
        lock (_manager.Sync)
        {
            _manager.History.LoadFight(row.Summary, _manager.Correlator);
        }
        row.IsLoaded = true;
    }

    /// <summary>Async, one fight per lock acquisition — loading 500 fights
    /// synchronously on the UI thread (each under the global sync) froze
    /// the app for the whole pull-back.</summary>
    [RelayCommand]
    private async Task LoadAll()
    {
        List<ArchiveRow> pending = [.. Rows.Where(r => !r.IsLoaded)];
        if (pending.Count == 0)
            return;
        await Task.Run(() =>
        {
            foreach (var row in pending)
            {
                lock (_manager.Sync)
                {
                    _manager.History.LoadFight(row.Summary, _manager.Correlator);
                }
            }
        });
        foreach (var row in pending)
            row.IsLoaded = true;
    }

    [RelayCommand]
    private void PurgeRow(ArchiveRow? row)
    {
        if (row is null)
            return;
        _manager.History.PurgeFromArchive(row.Summary.Id);
        Rows.Remove(row);
    }
}
