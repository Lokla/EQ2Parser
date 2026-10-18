using Avalonia;
using Avalonia.Headless;
using EQ2Parser.App.Services;

namespace EQ2Parser.Mac;

internal static class Program
{
    /// <summary>--snapshot DIR [SECONDS]: run off-screen on the headless
    /// renderer, let the sources parse for SECONDS (default 8), save one
    /// PNG per page into DIR and exit. Needs no screen-recording permission,
    /// so a change can be looked at without the window.</summary>
    internal static (string Dir, double Seconds)? Snapshot { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        var at = Array.IndexOf(args, "--snapshot");
        if (at >= 0 && at + 1 < args.Length)
        {
            var seconds = at + 2 < args.Length && double.TryParse(args[at + 2],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 8;
            Snapshot = (Path.GetFullPath(args[at + 1]), seconds);
        }
        CrashLog.Install();
        SessionJournal.Begin();
        LoopbackOnlyGuard.InstallIfRequested();
        var builder = Snapshot is null
            ? BuildAvaloniaApp()
            : AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .WithInterFont();
        builder.StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the Avalonia previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
