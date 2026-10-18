using System.Diagnostics;
using System.Globalization;

namespace EQ2Parser.App.Services;

/// <summary>
/// The session journal for non-Windows shells: the same session.log of
/// timestamped one-liners as the WPF app's SessionJournal, so a session
/// that ended badly leaves a trail. The Windows-only postmortem (event log,
/// crash-dump registry) has no counterpart; the previous session's journal
/// is kept as session.previous.log instead.
/// </summary>
public static class SessionJournal
{
    private static readonly object Gate = new();
    private static DateTime _lastHeartbeat;
    private static DateTime _lastTts;
    private static bool _ended;

    public static string FilePath => Path.Combine(AppSettings.Directory, "session.log");

    public static void Begin()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            lock (Gate)
            {
                _ended = false;
                if (File.Exists(FilePath))
                    File.Copy(FilePath, Path.Combine(AppSettings.Directory, "session.previous.log"), overwrite: true);
                File.WriteAllText(FilePath,
                    Line($"session start — EQ2Parser {CrashLog.Version} · {Environment.OSVersion} · .NET {Environment.Version}"));
            }
        }
        catch
        {
            // The journal must never be the thing that breaks startup.
        }
    }

    public static void Mark(string what)
    {
        try
        {
            lock (Gate)
            {
                if (!_ended)
                    File.AppendAllText(FilePath, Line(what));
            }
        }
        catch
        {
        }
    }

    public static void Heartbeat(int sourceCount)
    {
        var now = DateTime.UtcNow;
        if (now - _lastHeartbeat < TimeSpan.FromMinutes(1))
            return;
        _lastHeartbeat = now;
        long workingSet;
        try
        {
            using var proc = Process.GetCurrentProcess();
            workingSet = proc.WorkingSet64;
        }
        catch
        {
            workingSet = 0;
        }
        Mark($"heartbeat ws={workingSet >> 20}MB gc={GC.GetTotalMemory(false) >> 20}MB sources={sourceCount}");
    }

    public static void MarkTts(string what)
    {
        var now = DateTime.UtcNow;
        if (now - _lastTts < TimeSpan.FromSeconds(10))
            return;
        _lastTts = now;
        Mark(what);
    }

    public static void End()
    {
        Mark("session end (clean exit)");
        lock (Gate)
        {
            _ended = true;
        }
    }

    private static string Line(string what) =>
        $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {what}{Environment.NewLine}";
}
