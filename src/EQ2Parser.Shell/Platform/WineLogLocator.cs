namespace EQ2Parser.App.Services;

/// <summary>
/// EQ2 log folders on macOS, where the game runs under Wine: CrossOver
/// bottles, Whisky bottles and a plain Wine prefix each hold a Windows
/// drive_c with the usual install layouts in it. The counterpart of
/// Eq2LogLocator's drive scan; same answer shape (existing folders that
/// hold eq2log_*.txt, most recently written first).
/// </summary>
public static class WineLogLocator
{
    /// <summary>Install layouts inside a drive_c, relative, '/'-separated.</summary>
    private static readonly string[] DriveCCandidates =
    [
        "users/Public/Daybreak Game Company/Installed Games/EverQuest II/logs",
        "Program Files (x86)/Steam/steamapps/common/EverQuest 2/logs",
        "Program Files/Steam/steamapps/common/EverQuest 2/logs",
        "Daybreak Game Company/Installed Games/EverQuest II/logs",
        "Program Files (x86)/Sony/EverQuest II/logs",
    ];

    /// <summary>Every drive_c this user has: CrossOver and Whisky bottles,
    /// then ~/.wine.</summary>
    public static IEnumerable<string> DriveCRoots(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] bottleParents =
        [
            Path.Combine(home, "Library", "Application Support", "CrossOver", "Bottles"),
            Path.Combine(home, "Library", "Containers", "com.isaacmarovitz.Whisky", "Bottles"),
        ];
        foreach (var parent in bottleParents)
        {
            string[] bottles;
            try
            {
                bottles = Directory.Exists(parent) ? Directory.GetDirectories(parent) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var bottle in bottles)
            {
                var driveC = Path.Combine(bottle, "drive_c");
                if (Directory.Exists(driveC))
                    yield return driveC;
            }
        }
        var wine = Path.Combine(home, ".wine", "drive_c");
        if (Directory.Exists(wine))
            yield return wine;
    }

    /// <summary>Every existing log folder found, most-recently-written first.</summary>
    public static List<string> FindLogFolders(string? home = null)
    {
        List<(string Path, DateTime Newest)> found = [];
        foreach (var driveC in DriveCRoots(home))
        {
            foreach (var relative in DriveCCandidates)
            {
                var candidate = Path.Combine(driveC, relative);
                if (Newest(candidate) is { } newest)
                    found.Add((candidate, newest.LastWriteTimeUtc));
            }
        }
        return [.. found.OrderByDescending(f => f.Newest).Select(f => f.Path)];
    }

    /// <summary>The most recently written eq2log_*.txt under
    /// <paramref name="logsFolder"/> (any depth), or null.</summary>
    public static FileInfo? Newest(string logsFolder)
    {
        try
        {
            if (!Directory.Exists(logsFolder))
                return null;
            return new DirectoryInfo(logsFolder)
                .EnumerateFiles("eq2log_*.txt", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
