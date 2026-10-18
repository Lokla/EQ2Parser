using System.Globalization;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Mac.Cli;

/// <summary>
/// eq2parser-cli [log] [--from-start] [--data-dir DIR] [--trigger REGEX=PHRASE]
///               [--say TEXT] [--voices] [--voice NAME]
///
/// Without a log path it tails the most recently written eq2log_*.txt it
/// finds in a CrossOver/Whisky/Wine install. App state goes to a throwaway
/// folder unless --data-dir names one, so a run never touches the app's
/// own settings, triggers or archive.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? logPath = null;
        string? dataDir = null;
        string? say = null;
        string? voice = null;
        var fromStart = false;
        var listVoices = false;
        List<(string Regex, string Phrase)> triggers = [];
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from-start":
                    fromStart = true;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                case "--say" when i + 1 < args.Length:
                    say = args[++i];
                    break;
                case "--voice" when i + 1 < args.Length:
                    voice = args[++i];
                    break;
                case "--voices":
                    listVoices = true;
                    break;
                case "--trigger" when i + 1 < args.Length:
                    var spec = args[++i];
                    var eq = spec.LastIndexOf('=');
                    if (eq <= 0)
                        return Usage($"--trigger wants REGEX=PHRASE, got '{spec}'");
                    triggers.Add((spec[..eq], spec[(eq + 1)..]));
                    break;
                case "-h" or "--help":
                    return Usage(null);
                default:
                    if (args[i].StartsWith('-'))
                        return Usage($"unknown option {args[i]}");
                    logPath = args[i];
                    break;
            }
        }

        // Before anything reads AppSettings.Directory.
        dataDir ??= Path.Combine(Path.GetTempPath(), "eq2parser-cli");
        Environment.SetEnvironmentVariable("EQ2PARSER_DATA_DIR", Path.GetFullPath(dataDir));
        Loc.Initialize("en");
        CrashLog.Install();

        if (listVoices)
        {
            foreach (var v in AlertAudioService.ListVoices())
                Console.WriteLine(v.DisplayName);
            return 0;
        }

        if (say is not null)
        {
            using var audio = new AlertAudioService { VoiceId = voice };
            audio.Speak(say);
            audio.Speak("Second phrase, queued behind the first.");
            Thread.Sleep(8000);
            return 0;
        }

        logPath ??= FindLog();
        if (logPath is null || !File.Exists(logPath))
            return Usage(logPath is null ? "no EQ2 log found under CrossOver, Whisky or ~/.wine" : $"no such file: {logPath}");

        SessionJournal.Begin();
        using var manager = new SourceManager(AppSettings.Load());
        manager.Audio.VoiceId = voice ?? manager.Audio.VoiceId;
        foreach (var (regex, phrase) in triggers)
        {
            manager.Triggers.AddOrUpdate(new Trigger(regex, "CLI")
            {
                SoundType = TriggerSound.Tts,
                SoundData = phrase,
            });
        }
        manager.Triggers.AlertFired += fired =>
            Console.WriteLine($"  >> trigger: {fired.Trigger.RegexText} → \"{fired.TtsText}\"");

        var source = manager.Add(logPath, fromStart);
        Console.WriteLine($"Tailing {logPath}");
        Console.WriteLine($"  owner {source.Owner}, server '{Core.Upload.LogPaths.ParseServerName(logPath)}', data dir {AppSettings.Directory}");
        Console.WriteLine("  Ctrl+C to stop.");
        source.Engine.EncounterEnded += PrintSummary;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        string? lastLive = null;
        while (!stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
        {
            string? live;
            lock (manager.Sync)
            {
                live = source.Engine.ActiveEncounter is { Active: true } fight ? LiveLine(fight) : null;
                if (source.Stopped is { } stopped)
                {
                    Console.WriteLine($"source stopped: {stopped.Reason} — {stopped.Error.Message}");
                    return 1;
                }
            }
            if (live is not null && live != lastLive)
                Console.WriteLine(live);
            lastLive = live;
        }
        Console.WriteLine("stopping…");
        SessionJournal.End();
        return 0;
    }

    private static string? FindLog()
    {
        foreach (var folder in WineLogLocator.FindLogFolders())
        {
            if (WineLogLocator.Newest(folder) is { } newest)
                return newest.FullName;
        }
        return null;
    }

    private static string LiveLine(Encounter fight)
    {
        var top = fight.GetAllies()
            .OrderByDescending(c => c.Damage)
            .Take(3)
            .Select(c => $"{c.Name} {Dps(fight.EncDpsOf(c))}");
        return $"[{fight.Duration:mm\\:ss}] {fight.Title}: {Dps(fight.EncDps)} enc dps — {string.Join(", ", top)}";
    }

    /// <summary>Runs on the pump thread under the manager's lock.</summary>
    private static void PrintSummary(Encounter fight)
    {
        Console.WriteLine();
        Console.WriteLine($"== {fight.Title} ({fight.Zone}) — {fight.Duration:mm\\:ss}, {Dps(fight.EncDps)} enc dps, {fight.GetSuccessLevel()}");
        foreach (var c in fight.GetAllies().OrderByDescending(c => c.Damage).Take(10))
        {
            var share = fight.Damage > 0 ? 100.0 * c.Damage / fight.Damage : 0;
            Console.WriteLine($"   {c.Name,-20} {c.Damage,12:N0}  {Dps(fight.EncDpsOf(c)),8}  {share,5:0.0}%");
        }
        Console.WriteLine();
    }

    private static string Dps(double value) =>
        value >= 1000 ? (value / 1000).ToString("0.0", CultureInfo.InvariantCulture) + "k"
            : value.ToString("0", CultureInfo.InvariantCulture);

    private static int Usage(string? error)
    {
        if (error is not null)
            Console.Error.WriteLine($"error: {error}");
        Console.Error.WriteLine("usage: eq2parser-cli [log] [--from-start] [--data-dir DIR] [--trigger REGEX=PHRASE]... [--voice NAME]");
        Console.Error.WriteLine("       eq2parser-cli --say TEXT [--voice NAME]");
        Console.Error.WriteLine("       eq2parser-cli --voices");
        return error is null ? 0 : 2;
    }
}
