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
///
/// Every operation here fails soft. They run on the UI thread against a
/// SQLite file that can be missing (the session opened without an archive)
/// or can answer with an error (locked by another program, a damaged page,
/// a full disk), and a throw from a button handler is the "unexpected
/// error" dialog — five of them in a minute and the app closes itself. So
/// a failure becomes one line under the list (<see cref="Notice"/>) and a
/// crash report for whoever has to find out why; the list stays as it was.
/// </summary>
public sealed partial class ArchiveViewModel : ObservableObject
{
    /// <summary>Crash-report kind for an operation of this window that
    /// failed. Ordinary rate limit: a search that fails will fail again on
    /// the next keystroke.</summary>
    internal const string FailureKind = "archive-window";

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

    /// <summary>What went wrong, in the interface language; "" when
    /// nothing has. Either the reason there is no archive this session
    /// (shown INSTEAD of the result line: "Nothing found." would be a lie
    /// about an archive that was never searched), or the last operation
    /// that failed. Cleared by the next operation that works.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    public bool HasNotice => Notice.Length > 0;

    public ArchiveViewModel(SourceManager manager)
    {
        _manager = manager;
        Search();
    }

    partial void OnRangeIndexChanged(int value) => Search();

    /// <summary>True, with the reason on screen, when this session has no
    /// archive. Every command checks it first: without a store they would
    /// all "succeed" at doing nothing, which reads as an empty archive.</summary>
    private bool ShowUnavailable()
    {
        if (_manager.History.IsAvailable)
            return false;
        Rows.Clear();
        ResultLabel = "";
        Notice = ArchiveNotice.Startup(ArchiveNotice.Read(_manager.History)) ?? "";
        return true;
    }

    /// <summary>An operation threw: say so in one line and leave the
    /// evidence. The line carries the exception's kind, never its message.</summary>
    private void Failed(string notice, Exception ex, string what)
    {
        CrashLog.Report(FailureKind, ex, $"Archive window: {what} failed. The archive file was left as it is.");
        Notice = notice;
    }

    [RelayCommand]
    private void Search()
    {
        if (ShowUnavailable())
            return;
        var days = RangeDays[Math.Clamp(RangeIndex, 0, RangeDays.Length - 1)];
        var since = days is { } d ? DateTimeOffset.Now.AddDays(-d) : (DateTimeOffset?)null;
        var text = SearchText.Trim();
        IReadOnlyList<EncounterSummary> results;
        try
        {
            results = _manager.History.Search(text.Length == 0 ? null : text, since);
        }
        catch (Exception ex)
        {
            // The rows on screen are from the last search that worked; a
            // count for THIS one would be invented.
            ResultLabel = "";
            Failed(Loc.Format("Archive_SearchFailed", HistoryService.Kind(ex)), ex, "the search");
            return;
        }
        Rows.Clear();
        foreach (var summary in results)
            Rows.Add(new ArchiveRow(summary, _manager.History.IsLoaded(summary.Id)));
        var bosses = results.Count(s => s.IsBoss);
        ResultLabel = ResultLabelFor(results.Count, bosses);
        Notice = "";
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
        if (row is null || row.IsLoaded || ShowUnavailable())
            return;
        try
        {
            lock (_manager.Sync)
            {
                _manager.History.LoadFight(row.Summary, _manager.Correlator);
            }
        }
        catch (Exception ex)
        {
            // Not marked loaded: it is not in the parser, and the button
            // stays for another try.
            Failed(Loc.Format("Archive_LoadFailed", HistoryService.Kind(ex)), ex, "loading a fight");
            return;
        }
        row.IsLoaded = true;
        Notice = "";
    }

    /// <summary>Async, one fight per lock acquisition — loading 500 fights
    /// synchronously on the UI thread (each under the global sync) froze
    /// the app for the whole pull-back. Each fight is also its own guard:
    /// one that cannot be read costs that fight, the rest still load, and
    /// only the ones that did are marked.</summary>
    [RelayCommand]
    private async Task LoadAll()
    {
        if (ShowUnavailable())
            return;
        List<ArchiveRow> pending = [.. Rows.Where(r => !r.IsLoaded)];
        if (pending.Count == 0)
            return;
        List<ArchiveRow> loaded = [];
        Exception? firstFailure = null;
        await Task.Run(() =>
        {
            foreach (var row in pending)
            {
                try
                {
                    lock (_manager.Sync)
                    {
                        _manager.History.LoadFight(row.Summary, _manager.Correlator);
                    }
                    loaded.Add(row);
                }
                catch (Exception ex)
                {
                    firstFailure ??= ex;
                }
            }
        });
        foreach (var row in loaded)
            row.IsLoaded = true;
        if (firstFailure is null)
        {
            Notice = "";
            return;
        }
        var failed = pending.Count - loaded.Count;
        Failed(
            Loc.Format(failed == 1 ? "Archive_LoadAllFailedOne" : "Archive_LoadAllFailedMany", failed, HistoryService.Kind(firstFailure)),
            firstFailure,
            $"loading {failed} of {pending.Count} fights");
    }

    [RelayCommand]
    private void PurgeRow(ArchiveRow? row)
    {
        if (row is null || ShowUnavailable())
            return;
        try
        {
            _manager.History.PurgeFromArchive(row.Summary.Id);
        }
        catch (Exception ex)
        {
            // The row stays: the fight is still in the archive.
            Failed(Loc.Format("Archive_PurgeFailed", HistoryService.Kind(ex)), ex, "purging a fight");
            return;
        }
        Rows.Remove(row);
        Notice = "";
    }
}
