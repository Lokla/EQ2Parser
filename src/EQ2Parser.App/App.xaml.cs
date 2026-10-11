using System.Windows;
using System.Windows.Controls;
using EQ2Parser.App.Services;
using EQ2Parser.App.ViewModels;
using LiveChartsCore.SkiaSharpView;

namespace EQ2Parser.App;

// WPF Application lifetime: the fields are torn down in OnExit — making
// Application IDisposable is not the WPF pattern.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposed in OnExit; WPF Application cannot be using-scoped.")]
public partial class App : Application
{
    /// <summary>Process exit code for a launch that failed during startup
    /// (the crash-loop exit in CrashLog is 70) — distinct, so a script or
    /// a support log can tell the two apart.</summary>
    internal const int StartupFailureExitCode = 71;

    /// <summary>How long a failed launch waits for the update check after
    /// the user has dismissed the error dialog (the check has been running
    /// since before the dialog opened, so this is on top of however long
    /// they took to read it). Long enough for a release download on an
    /// ordinary connection; short enough that the windowless process is
    /// gone before anyone wonders about it. A download cut off here just
    /// gets its next chance on the next launch.</summary>
    private static readonly TimeSpan UpdateGraceAfterDialog = TimeSpan.FromSeconds(60);

    private SourceManager? _manager;
    private FocusWatcher? _focusWatcher;
    private Task? _updateCheck;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // First, before anything can throw: even a startup crash must leave
        // a log behind. The journal + WER dump registration right after —
        // they cover the deaths CrashLog can't see (native/SO/external kill).
        CrashLog.Install(this);
        SessionJournal.Begin();
        // Everything else is guarded as ONE unit. A throw in here used to
        // reach the dispatcher handler CrashLog just installed, which logs
        // a UI-thread exception and SWALLOWS it — right for a broken button
        // mid-raid, wrong for a launch: the process lived on with no window
        // (or half its services), and since the update check was the last
        // thing startup did, that install never looked for the release
        // that fixed it.
        try
        {
            Start();
        }
        catch (Exception ex)
        {
            FailStartup(ex, _manager?.Updates, _updateCheck);
        }
    }

    private void Start()
    {
        // Process-wide default match-timeout for every Regex built without an
        // explicit one (belt-and-suspenders for the grammar + any future
        // pattern). Trigger patterns set their own explicit timeout; the
        // real grammar-ReDoS defense is the log-line length cap in
        // LogTailReader. Must be set before any Regex is constructed.
        AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromMilliseconds(200));
        // The editors explain every option in its tooltip; WPF's default
        // ~5s auto-dismiss cuts them off mid-read.
        ToolTipService.ShowDurationProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(60_000));
        LiveChartsCore.LiveCharts.Configure(config => config.AddDarkTheme());
        // Settings, THEN the dictionaries, THEN every service. The language
        // is a setting, and the services resolve status strings while they
        // are being constructed — built before Loc.Initialize, they stored
        // raw keys. SourceManager takes the settings as a parameter so this
        // order cannot be undone by accident. It is also before ANY window:
        // the {loc:Tr} markup extension resolves at XAML load.
        var settings = AppSettings.Load();
        Localization.Loc.Initialize(settings.LanguageCode);
        _manager = new SourceManager(settings);
        SessionJournal.Mark("settings + localization loaded");
        _manager.RestoreHistory();
        SessionJournal.Mark("history restored");
        _manager.RestoreFromSettings();
        _manager.StartFolderWatch();
        SessionJournal.Mark("sources restored + folder watch started");
        var overlay = new OverlayController(_manager);
        // Overlay auto-hide: event-driven foreground tracking (no polling).
        // The setting is read per event so the Overlays-page toggle applies
        // without a restart; the callback arrives on this UI thread.
        _focusWatcher = new FocusWatcher();
        _focusWatcher.ForegroundFriendlyChanged += friendly =>
            overlay.SetSuppressed(_manager!.Settings.OverlayAutoHide && !friendly);
        var window = new MainWindow(new MainViewModel(_manager, overlay));
        MainWindow = window;
        // Overlays are windows too — without this, closing the main window
        // leaves the app running headless behind any open overlay.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        // First run: a fresh install has no folders or sources — offer the
        // log-folder wizard once (accepted or skipped, never nag again).
        if (!_manager.Settings.FirstRunShown
            && _manager.Settings.WatchedFolders.Count == 0
            && _manager.Settings.Sources.Count == 0)
        {
            new Views.FirstRunWindow(_manager) { Owner = window }.ShowDialog();
            _manager.Settings = _manager.Settings with { FirstRunShown = true };
            _manager.Settings.Save();
        }
        overlay.RestoreFromSettings();
        // Kept in a field: if anything after this line throws, the failure
        // path waits on THIS check instead of starting a second one.
        _updateCheck = _manager.Updates.CheckAndDownloadAsync();
        _ = _manager.Lexicon.StartupAsync();
        SessionJournal.Mark("startup complete — window shown, overlays restored");
    }

    /// <summary>The launch failed: leave the evidence, tell the user once,
    /// give the updater its chance, and exit non-zero. Never returns.
    /// Static, and everything it touches is optional, because Program.Main
    /// calls it too — for a failure before there is an App at all.
    ///
    /// The order is the point:
    ///  1. The crash report, before anything else can go wrong.
    ///  2. The update check, started BEFORE the dialog so it runs while
    ///     the user reads. A launch that fails every time is exactly the
    ///     install that most needs the next release, and this is the only
    ///     moment it will ever ask for one. Velopack applies a downloaded
    ///     update from a separate process that waits for this one to exit
    ///     (UpdateService: WaitExitThenApplyUpdates), so the check only
    ///     has to FINISH before the exit below — any exit will do.
    ///  3. The dialog (modal — the one explanation the user gets).
    ///  4. Windows hidden, then a bounded wait for the check: the UI thread
    ///     is about to block, and a frozen main window or an overlay stuck
    ///     over the game is worse than none.
    ///  5. Environment.Exit, not Shutdown: OnExit persists and disposes
    ///     services that may be half-built, and must not get the chance to
    ///     throw its way back into a live process. Skipping OnExit also
    ///     skips the journal's clean-exit mark, which is correct — the
    ///     next launch's postmortem then points at the report from step 1.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void FailStartup(Exception ex, UpdateService? updates = null, Task? runningUpdateCheck = null)
    {
        try
        {
            var report = CrashLog.ReportStartupFailure(ex);
            SessionJournal.Mark($"startup FAILED ({ex.GetType().Name}) — exiting with code {StartupFailureExitCode}");
            // Task.Run: when the manager never got built this constructs a
            // fresh UpdateService, and its constructor is no more trusted
            // than the code that just threw — on the pool, a throw there
            // is captured by the task instead of escaping this method.
            // (CheckAndDownloadAsync itself never throws, and its
            // continuations do not need this thread, so blocking on it
            // below cannot deadlock.)
            var update = runningUpdateCheck
                ?? Task.Run(() => (updates ?? new UpdateService()).CheckAndDownloadAsync());
            CrashLog.ShowStartupFailure(ex, report);
            HideWindows();
            update.Wait(UpdateGraceAfterDialog);
        }
        catch
        {
            // Nothing on the way out may keep the process alive.
        }
        Environment.Exit(StartupFailureExitCode);
    }

    /// <summary>Best-effort, in its own guard: a window that will not hide
    /// must not cost the update its wait.</summary>
    private static void HideWindows()
    {
        try
        {
            if (Current is not { } app)
                return;
            foreach (Window window in app.Windows)
                window.Hide();
        }
        catch
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SessionJournal.Mark("exit begin");
        _focusWatcher?.Dispose();
        // Join the folder watcher BEFORE persisting — a source discovered
        // mid-persist would lose its resume position.
        _manager?.StopFolderWatch();
        // Stop reading BEFORE persisting as well. Dispose ends the fight
        // each source is in and saves it, so the resume position written
        // here has to be the last line that fight holds: a line read
        // after the position was saved would be in tonight's fight and
        // read again into the next session's first one.
        _manager?.StopPumps();
        _manager?.PersistSources();
        // Ends the fights in progress while their consumers are attached,
        // then drains the archive writer — SourceManager.Dispose has the
        // order and the bounds.
        _manager?.Dispose();
        // Last: a settings change made within the debounce window of
        // quitting still reaches disk.
        EQ2Parser.Core.Persistence.PersistedJsonFile.FlushPending();
        SessionJournal.End();
        base.OnExit(e);
    }
}
