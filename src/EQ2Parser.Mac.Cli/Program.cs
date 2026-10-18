using System.Globalization;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;
using EQ2Parser.Core.Combat;
using EQ2Parser.Core.Triggers;

namespace EQ2Parser.Mac.Cli;

/// <summary>
/// eq2parser-cli [log | --from-settings] [--from-start] [--data-dir DIR] [--seconds N]
///               [--trigger REGEX=PHRASE]... [--voice NAME]
/// eq2parser-cli --say TEXT [--voice NAME] | --voices
/// eq2parser-cli --data-dir DIR --store-token FILE | --forget-token
///
/// Without a log path it tails the most recently written eq2log_*.txt it
/// finds in a CrossOver/Whisky/Wine install; --from-settings reads the
/// sources saved in the data folder's settings.json instead (several logs,
/// uploads as configured there). App state goes to a throwaway folder
/// unless --data-dir names one, so a run never touches the app's own
/// settings, triggers, archive or Keychain entry. --seconds ends the run
/// on its own: the fight in progress is ended and the upload queue is
/// given until its status line goes quiet.
///
/// --store-token / --forget-token keep a token in the Keychain entry of
/// that data folder (scripts/tools/e2e-upload-mac.sh); a token stored by
/// this program can be read back by it without a Keychain prompt.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? logPath = null;
        string? dataDir = null;
        string? say = null;
        string? voice = null;
        string? storeToken = null;
        double? seconds = null;
        var fromStart = false;
        var fromSettings = false;
        var forgetToken = false;
        var listVoices = false;
        List<(string Regex, string Phrase)> triggers = [];
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from-start":
                    fromStart = true;
                    break;
                case "--from-settings":
                    fromSettings = true;
                    break;
                case "--data-dir" when i + 1 < args.Length:
                    dataDir = args[++i];
                    break;
                case "--seconds" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s <= 0)
                        return Usage($"--seconds wants a positive number, got '{args[i]}'");
                    seconds = s;
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
                case "--store-token" when i + 1 < args.Length:
                    storeToken = args[++i];
                    break;
                case "--forget-token":
                    forgetToken = true;
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
        if (LoopbackOnlyGuard.InstallIfRequested())
            Console.WriteLine($"{LoopbackOnlyGuard.EnvironmentVariable}=1: only loopback hosts are reachable.");

        if (storeToken is not null)
        {
            var token = File.ReadAllText(storeToken).Trim();
            if (token.Length == 0 || TokenProtector.Protect(token) != TokenProtector.KeychainMarker)
                return Usage("could not store the token in the Keychain");
            Console.WriteLine($"token stored for {AppSettings.Directory}");
            return 0;
        }
        if (forgetToken)
        {
            if (!TokenProtector.Forget())
                return Usage("could not delete the Keychain entry");
            Console.WriteLine($"token forgotten for {AppSettings.Directory}");
            return 0;
        }

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
            // Wait for the queue, not a fixed time: disposing stops playback.
            audio.FlushAsync().Wait(TimeSpan.FromSeconds(60));
            return 0;
        }

        if (!fromSettings)
        {
            logPath ??= FindLog();
            if (logPath is null || !File.Exists(logPath))
                return Usage(logPath is null ? "no EQ2 log found under CrossOver, Whisky or ~/.wine" : $"no such file: {logPath}");
        }

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
        var uploadStatusAt = Environment.TickCount64;
        manager.Uploads.StatusChanged += () =>
        {
            Interlocked.Exchange(ref uploadStatusAt, Environment.TickCount64);
            Console.WriteLine($"  upload: {manager.Uploads.Status}");
        };

        if (fromSettings)
            manager.RestoreFromSettings();
        else
            manager.Add(logPath!, fromStart);
        var sources = manager.Sources;
        if (sources.Count == 0)
            return Usage("no log to read (settings.json lists no existing source)");
        foreach (var source in sources)
        {
            Console.WriteLine($"Reading {source.Path}");
            Console.WriteLine($"  owner {source.Owner}, server '{Core.Upload.LogPaths.ParseServerName(source.Path)}'");
            source.Engine.EncounterEnded += PrintSummary;
        }
        Console.WriteLine($"  data dir {AppSettings.Directory}; uploads {(manager.Uploads.Active ? "ON" : "off")}");
        Console.WriteLine(seconds is { } limit ? $"  stopping after {limit:0} s." : "  Ctrl+C to stop.");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        if (seconds is { } runFor)
            stop.CancelAfter(TimeSpan.FromSeconds(runFor));
        string? lastLive = null;
        while (!stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
        {
            string? live;
            lock (manager.Sync)
            {
                live = sources.Select(s => s.Engine.ActiveEncounter).FirstOrDefault(e => e is { Active: true }) is { } fight
                    ? LiveLine(fight)
                    : null;
                foreach (var source in sources)
                {
                    if (source.Stopped is { } stopped)
                    {
                        Console.WriteLine($"source stopped: {stopped.Reason} — {stopped.Error.Message}");
                        return 1;
                    }
                }
            }
            if (live is not null && live != lastLive)
                Console.WriteLine(live);
            lastLive = live;
        }

        Console.WriteLine("stopping…");
        if (seconds is not null)
        {
            // End the fight in progress the way the app's exit does, then let
            // the upload queue finish: quiet for 5 s, at most 60 s.
            manager.StopFolderWatch();
            manager.StopPumps();
            foreach (var source in sources)
                source.EndFightInProgress("the harness run ended");
            var deadline = Environment.TickCount64 + 60_000;
            while (Environment.TickCount64 < deadline
                && Environment.TickCount64 - Interlocked.Read(ref uploadStatusAt) < 5_000)
                Thread.Sleep(250);
            Console.WriteLine($"  uploads paused: {manager.Uploads.UploadsPaused}");
        }
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
        Console.Error.WriteLine("usage: eq2parser-cli [log | --from-settings] [--from-start] [--data-dir DIR] [--seconds N]");
        Console.Error.WriteLine("                     [--trigger REGEX=PHRASE]... [--voice NAME]");
        Console.Error.WriteLine("       eq2parser-cli --say TEXT [--voice NAME]");
        Console.Error.WriteLine("       eq2parser-cli --voices");
        Console.Error.WriteLine("       eq2parser-cli --data-dir DIR --store-token FILE | --forget-token");
        return error is null ? 0 : 2;
    }
}
