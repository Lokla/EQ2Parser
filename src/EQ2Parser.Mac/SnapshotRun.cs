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
    public static void Schedule(Window window, IClassicDesktopStyleApplicationLifetime desktop, string dir, double seconds)
    {
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
            desktop.Shutdown();
        }, TimeSpan.FromSeconds(seconds));
    }
}
