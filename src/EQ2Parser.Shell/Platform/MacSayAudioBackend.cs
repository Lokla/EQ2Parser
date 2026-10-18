using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EQ2Parser.App.Services;

/// <summary>
/// macOS speech through the system's own tools: <c>/usr/bin/say</c> renders
/// a phrase to WAV (every installed voice, including downloaded Enhanced
/// and Premium ones) and <c>/usr/bin/afplay</c> plays it. Arguments go
/// through <see cref="ProcessStartInfo.ArgumentList"/> and the phrase
/// through stdin, so no text from a log line or a trigger ever reaches a
/// shell or is read as an option.
/// </summary>
public sealed partial class MacSayAudioBackend : IAlertAudioBackend
{
    private const string Say = "/usr/bin/say";
    private const string AfPlay = "/usr/bin/afplay";

    /// <summary><c>say</c>'s own default is about 175–200 words a minute;
    /// the multiplier scales from here.</summary>
    private const double BaseWordsPerMinute = 180;

    // "Albert              en_US    # Hello! My name is Albert."
    // "Eddy (English (US)) en_US    # Hello! My name is Eddy."
    [GeneratedRegex(@"^(?<name>.+?)\s+(?<locale>[a-z]{2,3}[_-][A-Za-z0-9_-]+)\s+#", RegexOptions.CultureInvariant)]
    private static partial Regex VoiceLine();

    public IReadOnlyList<TtsVoice> ListVoices()
    {
        try
        {
            var output = RunForOutput(Say, ["-v", "?"]);
            return ParseVoices(output, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>English voices first (alert phrases and callouts are
    /// English), then the interface language, then the rest; duplicates
    /// (say lists some voices twice) collapse.</summary>
    internal static IReadOnlyList<TtsVoice> ParseVoices(string sayListing, string uiLanguage)
    {
        List<(TtsVoice Voice, string Locale)> voices = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var line in sayListing.Split('\n'))
        {
            var m = VoiceLine().Match(line.TrimEnd('\r'));
            if (!m.Success)
                continue;
            var name = m.Groups["name"].Value.Trim();
            var locale = m.Groups["locale"].Value;
            if (seen.Add(name))
                voices.Add((new TtsVoice(name, $"{name} ({locale})"), locale));
        }
        return [.. voices
            .OrderBy(v => v.Locale.StartsWith("en", StringComparison.Ordinal) ? 0
                : v.Locale.StartsWith(uiLanguage, StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(v => v.Voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.Voice)];
    }

    public async Task<bool> SynthesizeToFileAsync(string text, string? voiceId, double rate, string wavPath, CancellationToken ct)
    {
        var wpm = (int)Math.Round(BaseWordsPerMinute * Math.Clamp(rate, 0.5, 3.0));
        List<string> args = ["-f", "-", "-r", wpm.ToString(CultureInfo.InvariantCulture),
            "-o", wavPath, "--data-format=LEI16@22050"];
        if (!string.IsNullOrWhiteSpace(voiceId))
            args.InsertRange(0, ["-v", voiceId]);
        using var process = Start(Say, args, redirectInput: true);
        await process.StandardInput.WriteAsync(text.AsMemory(), ct);
        process.StandardInput.Close();
        await WaitAsync(process, ct);
        return process.ExitCode == 0 && File.Exists(wavPath);
    }

    public async Task PlayFileAsync(string path, double volume, CancellationToken ct)
    {
        var v = Math.Clamp(volume, 0, 1).ToString("0.###", CultureInfo.InvariantCulture);
        using var process = Start(AfPlay, ["-v", v, path], redirectInput: false);
        await WaitAsync(process, ct);
    }

    private static Process Start(string file, IEnumerable<string> args, bool redirectInput)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = redirectInput,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        return Process.Start(info) ?? throw new InvalidOperationException($"could not start {file}");
    }

    private static async Task WaitAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            throw;
        }
    }

    private static string RunForOutput(string file, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {file}");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output;
    }
}
