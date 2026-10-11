namespace EQ2Parser.Core.Upload;

/// <summary>
/// Pure parser for the EQ2 log path shape
/// &lt;install&gt;/logs/&lt;server&gt;/eq2log_&lt;character&gt;.txt. Mirrors the ACT
/// plugin's LogPathParser so both uploaders stamp identical logger_server
/// values on their payloads.
/// </summary>
public static class LogPaths
{
    /// <summary>
    /// EQ2 server name from a log path — the log's parent directory name.
    /// Returns "" when the path doesn't match the per-server layout — the
    /// legacy generic log at &lt;install&gt;/logs/eq2log.txt has parent "logs",
    /// not a server. "" means UNKNOWN, and nothing downstream can fill it
    /// in: the site has no default world to fall back to. Its parse ingest
    /// answers 400 ("logger_server is required") to an empty value, and the
    /// attendance ingest and raid-mains routes answer 400 as well, so a
    /// caller must not send a request for such a log at all — UploadService
    /// skips the fight and tells the user why, and UploadQueue.Enqueue drops
    /// an empty server as the backstop. Server names with spaces
    /// ("Antonia Bayle") pass through unmodified. A log in any other folder
    /// yields that folder's name, whatever it is; the site's allow-list
    /// decides whether that is a server it accepts.
    /// </summary>
    public static string ParseServerName(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return "";
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(logPath);
        }
        catch (ArgumentException)
        {
            return ""; // embedded NUL or similarly hostile path — unknown
        }
        if (string.IsNullOrEmpty(dir))
            return "";
        var name = Path.GetFileName(dir);
        if (string.IsNullOrEmpty(name) || name.Equals("logs", StringComparison.OrdinalIgnoreCase))
            return "";
        return name;
    }

    /// <summary>
    /// EQ2 install dir from a log path — where /do_file_commands files must
    /// be written. Per-server layout hops two dirs up from the log's folder
    /// (&lt;install&gt;/logs/&lt;server&gt;/eq2log_x.txt); the legacy generic layout
    /// (&lt;install&gt;/logs/eq2log.txt, signalled by ParseServerName == "")
    /// hops one. Returns null when the path doesn't look like either
    /// (no "logs" ancestor) — callers fall back to asking the user.
    /// </summary>
    public static string? ParseInstallDir(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return null;
        string? dir;
        try
        {
            dir = Path.GetDirectoryName(logPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(dir))
            return null;
        // Per-server: dir = <install>/logs/<server> → up past "logs".
        // Legacy:     dir = <install>/logs           → up once.
        if (!Path.GetFileName(dir).Equals("logs", StringComparison.OrdinalIgnoreCase))
        {
            dir = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(dir) || !Path.GetFileName(dir).Equals("logs", StringComparison.OrdinalIgnoreCase))
                return null; // not the EQ2 layout at all
        }
        var install = Path.GetDirectoryName(dir);
        return string.IsNullOrEmpty(install) ? null : install;
    }
}
