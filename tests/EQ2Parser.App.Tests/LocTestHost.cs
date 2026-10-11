using System.Runtime.CompilerServices;
using EQ2Parser.App.Localization;

namespace EQ2Parser.App.Tests;

/// <summary>
/// The app loads its dictionaries once, in App.Start, before anything can
/// ask for a string. A test host has no App.Start, and Loc.Get answers with
/// the KEY until the dictionaries are loaded — so a test that reads a
/// status line would pass or fail on whether some other test class
/// happened to call Loc.Initialize first. This gives the whole test
/// assembly the app's guarantee: English is loaded before any test runs.
///
/// A test that needs another language switches to it and switches back
/// (<see cref="In"/>); the assembly runs its tests one at a time, so the
/// switch cannot leak into a test running beside it.
/// </summary>
internal static class LocTestHost
{
    // CA2255 warns that a module initializer is for application code. This
    // one IS the test host's startup, and xunit 2 has no assembly-wide hook.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void LoadEnglish() => Loc.Initialize("en");
#pragma warning restore CA2255

    /// <summary>Run <paramref name="body"/> with the interface in
    /// <paramref name="language"/>, then put English back.</summary>
    internal static void In(string language, Action body)
    {
        Loc.Initialize(language);
        try
        {
            body();
        }
        finally
        {
            Loc.Initialize("en");
        }
    }
}
