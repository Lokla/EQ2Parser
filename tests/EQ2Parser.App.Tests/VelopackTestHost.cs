using System.IO;
using Velopack;
using Velopack.Locators;

namespace EQ2Parser.App.Tests;

/// <summary>SourceManager owns an UpdateService, and Velopack's
/// UpdateManager refuses to construct until the process has a locator —
/// in the app, Program.Main's VelopackApp.Build().Run() provides it before
/// any WPF code. A test that builds a real SourceManager calls
/// <see cref="Ensure"/> first: Velopack's own test locator, an empty
/// packages folder, no hooks and no auto-apply, so nothing is ever looked
/// up, downloaded or applied.</summary>
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
