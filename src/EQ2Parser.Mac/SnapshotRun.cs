using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace EQ2Parser.Mac;

/// <summary>The --snapshot run: after the sources have had time to parse,
/// render every tab of the main window to PNG, then quit normally (the
/// usual exit order, so the archive and positions are saved as in a real
/// session).</summary>
internal static class SnapshotRun
{
    public static void Schedule(Window window, IClassicDesktopStyleApplicationLifetime desktop,
        global::EQ2Parser.App.Services.SourceManager manager, string dir, double seconds)
    {
        // A few timers running when the pages are captured, so the timer
        // panel shows bars (the audio backend is silent in this mode).
        DispatcherTimer.RunOnce(() =>
        {
            foreach (var definition in manager.SpellTimers.Definitions.Where(d => d.Enabled).Take(3))
                manager.SpellTimers.StartTest(definition);
        }, TimeSpan.FromSeconds(Math.Max(0.5, seconds - 3)));
        // Tall enough that long pages (Settings) render whole.
        window.Height = 1400;
        window.Show();
        DispatcherTimer.RunOnce(() =>
        {
            Directory.CreateDirectory(dir);
            var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
            var count = tabs?.ItemCount ?? 1;
            for (var i = 0; i < count; i++)
            {
                if (tabs is not null)
                    tabs.SelectedIndex = i;
                var name = tabs?.SelectedItem is TabItem { Header: string header } ? header : $"page{i}";
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var path = Path.Combine(dir, $"{i + 1}-{name.ToLowerInvariant()}.png");
                window.CaptureRenderedFrame()?.Save(path);
                Console.WriteLine(path);
            }
            // The drill, walked down from the top ally: buckets, then the
            // first bucket's abilities, then the top ability's swings.
            if (window.DataContext is ViewModels.MainViewModel main && main.Fights.Allies.Count > 0)
            {
                if (tabs is not null)
                    tabs.SelectedIndex = 0;
                var fights = main.Fights;
                fights.OpenDrill(fights.Allies[0].Key);
                Capture(window, dir, "drill-1-buckets");
                if (fights.AbilityRows.FirstOrDefault(r => !r.IsDivider) is { } bucket)
                {
                    fights.DrillInto(bucket);
                    Capture(window, dir, "drill-2-abilities");
                    if (fights.AbilityRows.Skip(1).FirstOrDefault(r => !r.IsDivider) is { } ability)
                    {
                        fights.DrillInto(ability);
                        Capture(window, dir, "drill-3-swings");
                    }
                }
            }
            desktop.Shutdown();
        }, TimeSpan.FromSeconds(seconds));
    }

    private static void Capture(Window window, string dir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var path = Path.Combine(dir, name + ".png");
        window.CaptureRenderedFrame()?.Save(path);
        Console.WriteLine(path);
    }
}
