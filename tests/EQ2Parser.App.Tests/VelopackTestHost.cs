using System.IO;
using Velopack;
using Velopack.Locators;

namespace EQ2Parser.App.Tests;

/// <summary>Velopack's UpdateManager refuses to construct until the
/// process has a locator — in the app, Program.Main's
/// VelopackApp.Build().Run() provides it before any WPF code.
/// UpdateService builds its manager on first use now, so constructing a
/// SourceManager (which owns one) no longer needs this; a test that goes
/// on to READ the manager (the version label on the Settings page) still
/// does, and calls <see cref="Ensure"/> first: Velopack's own test
/// locator, an empty packages folder, no hooks and no auto-apply, so
/// nothing is ever looked up, downloaded or applied.</summary>
internal static class VelopackTestHost
{
    private static readonly object Gate = new();

    public static void Ensure()
    {
        lock (Gate)
        {
            if (VelopackLocator.IsCurrentSet)
                return;
            var packages = Path.Combine(Path.GetTempPath(), "eq2parser-app-tests", "velopack-packages");
            Directory.CreateDirectory(packages);
            VelopackApp.Build()
                .SetArgs([])
                .SetAutoApplyOnStartup(false)
                .SetLocator(new TestVelopackLocator("EQ2Parser.Tests", "0.0.0", packages, null))
                .Run();
        }
    }
}
