using System.Runtime.CompilerServices;

namespace EQ2Parser.Shell.Tests;

/// <summary>Every test in this assembly sees a throwaway data folder, so
/// nothing a test triggers (the session journal, settings, the Keychain
/// entry name) can touch the real install.</summary>
internal static class TestDataDir
{
    public static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "eq2parser-shell-tests", "data-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Path);
        Environment.SetEnvironmentVariable("EQ2PARSER_DATA_DIR", Path);
    }
}
