using System.Text.RegularExpressions;

namespace EQ2Parser.Core.Upload;

/// <summary>
/// Pure parser for the EQ2 log path shape
/// &lt;install&gt;/logs/&lt;server&gt;/eq2log_&lt;character&gt;.txt. Mirrors the ACT
/// plugin's LogPathParser so both uploaders stamp identical logger_server
/// values on their payloads.
/// </summary>
public static partial class LogPaths
{
    /// <summary>The shape of a server name the site accepts as
    /// <c>logger_server</c>: a letter, then up to 30 more characters, each a
    /// letter, a digit, a space, an apostrophe, an underscore or a hyphen —
    /// 1 to 31 characters, ASCII only, either case. Mirrors
    /// <c>WORLD_NAME_RE</c> in EQ2Lexicon's
    /// <c>backend/server/core/validation.py</c>, character for character;
    /// change both together. The site applies it through
    /// <c>sanitize_world</c> (same module), which strips surrounding
    /// whitespace first — that is what makes the trailing <c>$</c> mean the
    /// end of the name on both sides — and <c>ingest_parse</c>
    /// (<c>backend/server/api/parses/ingest.py</c>) answers 400
    /// "logger_server '…' is malformed." to a name that fails it. The
    /// attendance ingest and the raid-mains lookup gate on the same
    /// function. The length here is the PATTERN's: the request model's own
    /// <c>max_length</c> on the field has been a different number.</summary>
    public const string ServerNamePattern = @"^[A-Za-z][A-Za-z0-9 '_-]{0,30}$";

    [GeneratedRegex(ServerNamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex ServerNameShape();

    /// <summary>Whether the site would accept <paramref name="serverName"/>
    /// as a <c>logger_server</c> shape (see <see cref="ServerNamePattern"/>;
    /// whitespace around it is ignored, as the site ignores it). False for
    /// null, empty and blank. This is the SHAPE only — whether the server is
    /// one the site tracks is its allow-list's decision, made per upload. A
    /// log folder whose name fails this (it starts with a digit, holds a
    /// character outside the set, or runs past 31) can only be answered
    /// with a 400, every fight, so callers do not send for it.</summary>
    public static bool IsServerNameAccepted(string? serverName) =>
        serverName is not null && ServerNameShape().IsMatch(serverName.Trim());

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
    /// yields that folder's name, whatever it is: <see cref="IsServerNameAccepted"/>
    /// says whether the site would take it as a server name at all, and the
    /// site's allow-list decides whether it is a server it accepts.
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
