using System.Reflection;
using EQ2Parser.App.Localization;

namespace EQ2Parser.App.Services;

/// <summary>
/// The WPF app's Velopack updater has no counterpart here yet: a macOS
/// build is run from source or a local publish, so this only reports the
/// version — the same surface, so the linked SourceManager and Settings
/// code need no changes.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Raised whenever the status line changes.</summary>
    public event Action<string>? StatusChanged;

    public string Status { get; private set; } = "";

#pragma warning disable CA1822 // instance member: the WPF API callers bind to
    public string CurrentVersion =>
        Loc.Format("UpdateSvc_VersionFromSource", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));
#pragma warning restore CA1822

    /// <summary>Never throws; there is nothing to download.</summary>
    public Task CheckAndDownloadAsync()
    {
        Status = Loc.Get("UpdateSvc_RunningFromSource");
        StatusChanged?.Invoke(Status);
        return Task.CompletedTask;
    }
}
