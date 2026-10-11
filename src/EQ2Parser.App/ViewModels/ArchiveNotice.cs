using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

/// <summary>What is wrong with the fight archive, as facts. A snapshot of
/// <see cref="HistoryService"/> taken by <see cref="ArchiveNotice.Read"/>;
/// a record of its own so the wording can be tested for every state
/// without breaking a real file to reach it. The two detail fields are
/// <see cref="HistoryService.Kind"/> of the exception concerned: its type
/// name, plus SQLite's result code when it has one.</summary>
internal sealed record ArchiveHealth(
    ArchiveState State,
    string? QuarantinedFile = null,
    string? OpenErrorKind = null,
    long SaveFailures = 0,
    string? SaveErrorKind = null,
    bool RestoreFailed = false,
    int RestoreFailures = 0,
    string? RestoreErrorKind = null);

/// <summary>
/// The words for a degraded archive, in the interface language. The main
/// window's warning strip and the Archive window both show them.
///
/// Why this exists: the archive survives a damaged or unreachable
/// history.db, a failed save and an unreadable fight, and says so in a
/// crash report and the journal — which nobody reads mid-raid. A session
/// whose archive could not be opened parses, shows and uploads every
/// fight and saves none of them, and looked exactly like a healthy one.
///
/// Built from state, never from text: <see cref="HistoryService.Problem"/>
/// and <see cref="HistoryService.DegradedReason"/> are English by design
/// (they are what the crash report says) and an exception's message is
/// whatever the runtime or Windows wrote. The one untranslated thing a
/// sentence carries is the exception's KIND in brackets, which is what a
/// tester quotes and what the journal line beside it says too.
///
/// Each problem is one whole dictionary string of two sentences: what
/// happened, then what it means for the user. Several problems are
/// several strings on lines of their own.
/// </summary>
internal static class ArchiveNotice
{
    /// <summary>The archive's state right now, as a snapshot.</summary>
    internal static ArchiveHealth Read(HistoryService history) => new(
        history.State,
        history.QuarantinedPath is { } quarantined ? System.IO.Path.GetFileName(quarantined) : null,
        history.OpenError is { } openError ? HistoryService.Kind(openError) : null,
        history.SaveFailures,
        history.LastSaveError is { } saveError ? HistoryService.Kind(saveError) : null,
        history.RestoreError is not null,
        history.RestoreFailures,
        history.RestoreError is { } restoreError ? HistoryService.Kind(restoreError) : null);

    /// <summary>The strip's text for this session, or "" when there is
    /// nothing to say (which is what hides the strip). Called on every
    /// shell tick, so the healthy case — nearly every session, ten times
    /// a second — is three reads and no allocation.</summary>
    internal static string For(HistoryService history) =>
        history.State == ArchiveState.Healthy && history.SaveFailures == 0 && history.RestoreError is null
            ? ""
            : Text(Read(history));

    /// <summary>Everything wrong, one problem per line, in the order it
    /// matters to someone raiding: whether fights are being saved at all,
    /// then fights of this session that were not, then earlier fights that
    /// did not come back. "" when nothing is wrong.</summary>
    internal static string Text(ArchiveHealth health)
    {
        List<string>? lines = null;
        if (Startup(health) is { } startup)
            (lines ??= []).Add(startup);
        if (health.SaveFailures > 0)
        {
            (lines ??= []).Add(Loc.Format(
                health.SaveFailures == 1 ? "ArchiveNotice_SavesFailedOne" : "ArchiveNotice_SavesFailedMany",
                health.SaveFailures, health.SaveErrorKind));
        }
        if (health.RestoreFailed)
        {
            (lines ??= []).Add(health.RestoreFailures > 0
                ? Loc.Format(
                    health.RestoreFailures == 1 ? "ArchiveNotice_RestoreFailedOne" : "ArchiveNotice_RestoreFailedMany",
                    health.RestoreFailures, health.RestoreErrorKind)
                : Loc.Format("ArchiveNotice_RestoreFailedAll", health.RestoreErrorKind));
        }
        return lines is null ? "" : string.Join(Environment.NewLine, lines);
    }

    /// <summary>What became of history.db when the session opened it, or
    /// null when it opened normally. The Archive window shows this in
    /// place of its result line when there is no archive to search.
    ///
    /// The quarantined case names the file the old archive was renamed to,
    /// because "your history is empty" and "your history is in this file"
    /// are different news. An unavailable archive that was renamed first
    /// (damaged, then no fresh one could be created) names it too.</summary>
    internal static string? Startup(ArchiveHealth health) => health.State switch
    {
        ArchiveState.Quarantined => Loc.Format("ArchiveNotice_Quarantined", health.QuarantinedFile),
        ArchiveState.Unavailable when health.QuarantinedFile is { } file =>
            Loc.Format("ArchiveNotice_UnavailableAfterQuarantine", file, health.OpenErrorKind),
        ArchiveState.Unavailable => Loc.Format("ArchiveNotice_Unavailable", health.OpenErrorKind),
        _ => null,
    };
}
