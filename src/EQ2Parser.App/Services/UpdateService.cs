using System.Reflection;
using EQ2Parser.App.Localization;
using Velopack;
using Velopack.Sources;

namespace EQ2Parser.App.Services;

/// <summary>
/// Auto-update via Velopack + GitHub Releases: check quietly at startup,
/// download in the background, apply when the app exits — nobody has to
/// chase versions in Discord. Running from source (not installed) turns
/// the whole thing into a no-op with an honest status line.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Releases live on the public code repo now that it's public —
    /// the updater reads them anonymously. (They used to live in a separate
    /// EQ2Parser-releases repo while the code repo was private; v0.3.3 is
    /// the transitional build that moves the feed here, published to BOTH
    /// repos so installs still pointing at the old feed migrate across.)</summary>
    public const string RepoUrl = "https://github.com/VortexUK/EQ2Parser";

    /// <summary>Velopack's manager, built on first use rather than with
    /// the service.
    ///
    /// Building it needs the process-wide Velopack locator, which exists
    /// only once VelopackApp.Build().Run() has run (Program.Main, first
    /// statement); without one the constructor throws. Built eagerly, that
    /// made this service — and SourceManager, which owns one — impossible
    /// to construct anywhere else, a test included, for the sake of an
    /// object nothing touches until the version label is read or the
    /// update check starts.
    ///
    /// In the app nothing changes. The manager's constructor (and the
    /// GitHub source's) only store their arguments and read that locator —
    /// no file, registry or network access (read from Velopack 1.2.0's IL)
    /// — and the locator is set once, before any of this code, and never
    /// replaced. So an instance built at first use is the instance the
    /// field initializer would have built, and first use is still inside
    /// startup (the update check is one of its last statements).
    ///
    /// Stable channel: full releases only. (During the tester phase this
    /// was prerelease: true; releases are now published as full releases,
    /// and an install still on the old prerelease:true feed also sees a
    /// newer full release, so the first stable release migrates it over
    /// cleanly.)</summary>
    private readonly Lazy<UpdateManager> _manager;

    public UpdateService()
        : this(static () => new UpdateManager(new GithubSource(RepoUrl, null, prerelease: false)))
    {
    }

    /// <summary>Test seam: how the manager is built.</summary>
    internal UpdateService(Func<UpdateManager> createManager) =>
        _manager = new Lazy<UpdateManager>(createManager);

    /// <summary>Raised (on a background thread) whenever the status line
    /// changes — Settings shows it.</summary>
    public event Action<string>? StatusChanged;

    public string Status { get; private set; } = "";

    public string CurrentVersion =>
        _manager.Value.IsInstalled
            ? _manager.Value.CurrentVersion?.ToString() ?? "?"
            : Loc.Format("UpdateSvc_VersionFromSource", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));

    private void Set(string status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>Startup check: fire-and-forget; never throws. Building the
    /// manager is inside the guard too: a process without a Velopack
    /// locator gets the "check failed" status line, not a faulted task.</summary>
    public async Task CheckAndDownloadAsync()
    {
        try
        {
            var manager = _manager.Value;
            if (!manager.IsInstalled)
            {
                Set(Loc.Get("UpdateSvc_RunningFromSource"));
                return;
            }
            Set(Loc.Get("UpdateSvc_Checking"));
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                Set(Loc.Get("UpdateSvc_UpToDate"));
                return;
            }
            Set(Loc.Format("UpdateSvc_Downloading", update.TargetFullRelease.Version));
            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            // Applies silently after the app closes; next launch is current.
            manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
            Set(Loc.Format("UpdateSvc_Downloaded", update.TargetFullRelease.Version));
        }
        catch (Exception ex)
        {
            Set(Loc.Format("UpdateSvc_CheckFailed", ex.Message));
        }
    }
}
