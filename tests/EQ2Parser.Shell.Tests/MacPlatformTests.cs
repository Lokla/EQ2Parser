using EQ2Parser.App.Services;

namespace EQ2Parser.Shell.Tests;

public sealed class MacSayVoiceListTests
{
    private const string Listing =
        "Albert              en_US    # Hello! My name is Albert.\n" +
        "Alice (Italienisch (Italien)) it_IT    # Ciao! Mi chiamo Alice.\n" +
        "Alice (Italienisch (Italien)) it_IT    # Ciao! Mi chiamo Alice.\n" +
        "Anna (Deutsch (Deutschland)) de_DE    # Hallo! Ich heiße Anna.\n" +
        "Zoe (Premium)       en_GB    # Hello! My name is Zoe.\n" +
        "not a voice line\n";

    [Fact]
    public void Voices_Parse_With_Spaces_And_Parentheses_In_Their_Names()
    {
        var voices = MacSayAudioBackend.ParseVoices(Listing, "de");

        Assert.Contains(voices, v => v.Id == "Alice (Italienisch (Italien))");
        Assert.Contains(voices, v => v.Id == "Zoe (Premium)");
    }

    [Fact]
    public void Duplicates_Collapse_And_English_Comes_First_Then_The_Interface_Language()
    {
        var ids = MacSayAudioBackend.ParseVoices(Listing, "de").Select(v => v.Id).ToList();

        Assert.Equal(["Albert", "Zoe (Premium)", "Anna (Deutsch (Deutschland))", "Alice (Italienisch (Italien))"], ids);
    }
}

public sealed class WineLogLocatorTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "eq2parser-shell-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string Log(string driveCParent, string relative, DateTime written)
    {
        var path = Path.Combine(_home, driveCParent, "drive_c", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "(1)[x] line\r\n");
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    [Fact]
    public void Finds_Logs_In_CrossOver_Whisky_And_Wine_Newest_Folder_First()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fresh = old.AddDays(30);
        Log("Library/Application Support/CrossOver/Bottles/EverQuest II",
            "users/Public/Daybreak Game Company/Installed Games/EverQuest II/logs/Wuoshi/eq2log_Vorath.txt", old);
        Log("Library/Containers/com.isaacmarovitz.Whisky/Bottles/EQ2",
            "Program Files (x86)/Steam/steamapps/common/EverQuest 2/logs/Varsoon/eq2log_Bob.txt", fresh);
        Log(".wine", "Daybreak Game Company/Installed Games/EverQuest II/logs/eq2log_Carl.txt", old.AddDays(-5));

        var folders = WineLogLocator.FindLogFolders(_home);

        Assert.Equal(3, folders.Count);
        Assert.Contains("Whisky", folders[0], StringComparison.Ordinal);
        Assert.Contains("CrossOver", folders[1], StringComparison.Ordinal);
        Assert.EndsWith("logs", folders[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_Folder_Without_Logs_Is_Not_Offered()
    {
        Directory.CreateDirectory(Path.Combine(_home, "Library/Application Support/CrossOver/Bottles/Empty/drive_c/users/Public/Daybreak Game Company/Installed Games/EverQuest II/logs"));

        Assert.Empty(WineLogLocator.FindLogFolders(_home));
    }

    [Fact]
    public void Newest_Picks_The_Live_Log_Over_A_Rotated_One()
    {
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var rotated = Log(".wine", "x/logs/Wuoshi/eq2log_Vorath.2026.09.23.txt", t);
        var live = Log(".wine", "x/logs/Wuoshi/eq2log_Vorath.txt", t.AddDays(1));

        Assert.Equal(live, WineLogLocator.Newest(Path.Combine(_home, ".wine/drive_c/x/logs"))!.FullName);
        Assert.NotEqual(rotated, live);
    }
}

public sealed class TokenProtectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA")] // a Windows DPAPI blob copied over
    public void Anything_But_The_Keychain_Marker_Reads_As_No_Token(string? stored) =>
        Assert.Null(TokenProtector.Unprotect(stored));
}
