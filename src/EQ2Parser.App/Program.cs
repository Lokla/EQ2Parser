using EQ2Parser.App.Services;
using Velopack;

namespace EQ2Parser.App;

/// <summary>
/// Explicit entry point so the Velopack bootstrap runs before any WPF code —
/// it handles install/update/uninstall hooks and must be first in Main
/// (see https://docs.velopack.io). StartupObject in the csproj points here.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        App app;
        try
        {
            app = new App();
            app.InitializeComponent();
        }
        catch (Exception ex)
        {
            // Building the Application (its resource dictionaries, the
            // theme) failed — earlier than OnStartup, so neither CrashLog
            // nor the journal is installed yet and the process would die
            // with no report and no update check. Start the journal by
            // hand so this launch reads as its own session, then take the
            // same exit as a failure inside OnStartup. Only construction
            // is guarded: app.Run() below hosts the whole session, and
            // nothing that escapes it later is a STARTUP failure.
            SessionJournal.Begin();
            App.FailStartup(ex);
            return; // unreachable — FailStartup exits the process
        }
        app.Run();
    }
}
