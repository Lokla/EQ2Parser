using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Persistence;
using EQ2Parser.Mac.ViewModels;

namespace EQ2Parser.Mac;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>The WPF app's startup order (App.xaml.cs): settings and
    /// language first, then the manager, the archive, the sources and the
    /// folder watch.</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            Loc.Initialize(settings.LanguageCode);
            var manager = new SourceManager(settings);
            SessionJournal.Mark("settings + localization loaded");
            manager.RestoreHistory();
            FirstRun(manager);
            manager.RestoreFromSettings();
            manager.StartFolderWatch();
            SessionJournal.Mark("sources restored + folder watch started");
            _ = manager.Lexicon.StartupAsync();

            var window = new MainWindow();
            window.DataContext = new MainViewModel(manager, window);
            desktop.MainWindow = window;
            var shutDown = false;
            desktop.ShutdownRequested += (_, _) =>
            {
                if (!shutDown)
                {
                    shutDown = true;
                    Shutdown(manager);
                }
            };
            SessionJournal.Mark("startup complete — window shown");
            if (Program.Snapshot is { } snapshot)
                SnapshotRun.Schedule(window, desktop, snapshot.Dir, snapshot.Seconds);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Nothing configured yet: watch every EQ2 log folder found in
    /// a CrossOver, Whisky or Wine install, so the first fight shows up
    /// without a trip to Settings.</summary>
    private static void FirstRun(SourceManager manager)
    {
        if (manager.Settings.WatchedFolders.Count > 0 || manager.Settings.Sources.Count > 0)
            return;
        foreach (var folder in WineLogLocator.FindLogFolders())
            manager.AddWatchedFolder(folder);
    }

    /// <summary>The WPF app's exit order (App.OnExit): stop watching and
    /// reading, persist the resume positions, then end and save the fights
    /// in progress.</summary>
    private static void Shutdown(SourceManager manager)
    {
        SessionJournal.Mark("exit begin");
        manager.StopFolderWatch();
        manager.StopPumps();
        manager.PersistSources();
        manager.Dispose();
        PersistedJsonFile.FlushPending();
        SessionJournal.End();
    }
}
