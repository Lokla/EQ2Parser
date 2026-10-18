using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace EQ2Parser.App.Services;

/// <summary>A selectable TTS voice (backend identity + friendly name).</summary>
public sealed record TtsVoice(string Id, string DisplayName);

/// <summary>
/// The alert audio stack for non-Windows shells — the same public surface
/// as the WPF app's AlertAudioService, which the linked TriggerService,
/// TimerService and SourceManager are written against, over an
/// <see cref="IAlertAudioBackend"/>. The contract carries over: spoken
/// alerts queue so phrases never talk over each other, the chime and
/// sound-file alerts fire immediately and may overlap speech, rendered
/// phrases are cached per voice/rate/phrase so repeats play instantly,
/// and a sound path off the local filesystem is refused.
/// </summary>
public sealed class AlertAudioService : IDisposable
{
    private const int CacheCap = 300;

    /// <summary>The backend new instances use; set once at startup by the
    /// shell. Defaults to macOS speech on macOS, silence elsewhere.</summary>
    public static IAlertAudioBackend DefaultBackend { get; set; } =
        OperatingSystem.IsMacOS() ? new MacSayAudioBackend() : new SilentAlertAudioBackend();

    private readonly IAlertAudioBackend _backend;
    private readonly string _cacheDir;
    private readonly Channel<Func<CancellationToken, Task>> _speech =
        Channel.CreateBounded<Func<CancellationToken, Task>>(
            new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, string> _ttsCache = [];
    private readonly object _cacheGate = new();
    private readonly Lazy<string?> _chimePath;
    private readonly Task _speechPump;

    /// <summary>Master alert volume, 0–1.</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>Speaking rate multiplier (0.5 = half speed, 2 = double).</summary>
    public double SpeakingRate { get; set; } = 1.0;

    /// <summary>Neural pitch factor. Kept for settings compatibility; the
    /// system voices cannot shift pitch, so it is ignored here.</summary>
    public double VoiceDepth { get; set; } = 1.0;

    /// <summary>Backend voice id; null = the system default voice.</summary>
    public string? VoiceId { get; set; }

    /// <summary>Raised when a neural voice fails. There are no neural
    /// voices on this backend yet, so it never fires.</summary>
#pragma warning disable CA1822 // instance member: the WPF API callers bind to
    public event Action<string>? NeuralVoiceFailed
    {
        add { }
        remove { }
    }
#pragma warning restore CA1822

    public AlertAudioService()
        : this(DefaultBackend, Path.Combine(Path.GetTempPath(), "EQ2Parser-tts"))
    {
    }

    /// <summary>Test seam: the backend and where rendered phrases go.</summary>
    internal AlertAudioService(IAlertAudioBackend backend, string cacheDir)
    {
        _backend = backend;
        _cacheDir = cacheDir;
        _chimePath = new Lazy<string?>(WriteChime);
        _speechPump = Task.Run(async () =>
        {
            try
            {
                await foreach (var job in _speech.Reader.ReadAllAsync(_cts.Token))
                {
                    try
                    {
                        await job(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        // One bad phrase or a device hiccup never stops the pump.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
        });
    }

    /// <summary>Every installed voice, best first.</summary>
    public static IReadOnlyList<TtsVoice> ListVoices() => DefaultBackend.ListVoices();

    /// <summary>Queue a phrase — spoken after anything already queued.</summary>
    public void Speak(string text)
    {
        if (text.Length == 0)
            return;
        _speech.Writer.TryWrite(async ct =>
        {
            if (await SynthesizeAsync(text, ct) is { } wav)
                await _backend.PlayFileAsync(wav, Volume, ct);
        });
    }

    /// <summary>Guard for the Settings Test button: 0 = idle, else the
    /// tick until which re-clicks are ignored.</summary>
    private long _testGuardUntilMs;

    /// <summary>Speak for the Test button — clicks while the previous test
    /// phrase is still synthesizing or playing are IGNORED, not queued.</summary>
    public void SpeakTest(string text)
    {
        if (text.Length == 0)
            return;
        var now = Environment.TickCount64;
        if (Interlocked.Read(ref _testGuardUntilMs) > now)
            return;
        Interlocked.Exchange(ref _testGuardUntilMs, now + 15_000);
        var accepted = _speech.Writer.TryWrite(async ct =>
        {
            try
            {
                if (await SynthesizeAsync(text, ct) is { } wav)
                    await _backend.PlayFileAsync(wav, Volume, ct);
            }
            finally
            {
                Interlocked.Exchange(ref _testGuardUntilMs, 0);
            }
        });
        if (!accepted)
            Interlocked.Exchange(ref _testGuardUntilMs, 0);
    }

    /// <summary>The soft-bell alert. Fire-and-forget, overlaps speech, and
    /// never runs on the caller's thread (callers include the log pump).</summary>
    public void PlayChime() => _ = Task.Run(async () =>
    {
        if (_chimePath.Value is { } chime)
            await PlaySafelyAsync(chime);
    });

    /// <summary>No neural model is ever loaded on this backend.</summary>
#pragma warning disable CA1822 // instance member: the WPF API callers bind to
    public void UnloadNeuralModel()
    {
    }
#pragma warning restore CA1822

    /// <summary>Play an audio file (WAV/MP3/AIFF). Fire-and-forget.</summary>
    public void PlayFile(string path)
    {
        // A trigger's sound path can be server-supplied (the Lexicon pack) or
        // pasted in (ACT import): only local files the user has are played.
        if (IsNonLocalPath(path) || !File.Exists(path))
            return;
        _ = Task.Run(() => PlaySafelyAsync(path));
    }

    /// <summary>True for UNC paths (\\host\share, //host/share) and
    /// URL-shaped paths — anything that resolves off the local filesystem.</summary>
    internal static bool IsNonLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;
        var p = path.Trim();
        if (p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal))
            return true;
        if (Uri.TryCreate(p, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile)
            return true;
        return false;
    }

    private async Task PlaySafelyAsync(string path)
    {
        try
        {
            await _backend.PlayFileAsync(path, Volume, _cts.Token);
        }
        catch (Exception)
        {
            // No audio device, unreadable file or shutdown — never propagate.
        }
    }

    private async Task<string?> SynthesizeAsync(string text, CancellationToken ct)
    {
        var key = $"{VoiceId}|{SpeakingRate:0.##}|{text}";
        lock (_cacheGate)
        {
            if (_ttsCache.TryGetValue(key, out var cached) && File.Exists(cached))
                return cached;
        }
        Directory.CreateDirectory(_cacheDir);
        var path = Path.Combine(_cacheDir, Hash(key) + ".wav");
        if (!await _backend.SynthesizeToFileAsync(text, VoiceId, SpeakingRate, path, ct))
            return null;
        lock (_cacheGate)
        {
            if (_ttsCache.Count >= CacheCap)
                _ttsCache.Clear();
            _ttsCache[key] = path;
        }
        return path;
    }

    private static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    private string? WriteChime()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var path = Path.Combine(_cacheDir, "chime.wav");
            File.WriteAllBytes(path, BuildChime());
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A short two-partial bell with a soft attack and natural
    /// decay — the same tone as the Windows build, as 16-bit mono PCM.</summary>
    internal static byte[] BuildChime()
    {
        const int rate = 44100;
        const double seconds = 0.5;
        var count = (int)(rate * seconds);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            var dataBytes = count * 2;
            w.Write("RIFF"u8);
            w.Write(36 + dataBytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);              // PCM chunk size
            w.Write((short)1);        // PCM
            w.Write((short)1);        // mono
            w.Write(rate);
            w.Write(rate * 2);        // byte rate
            w.Write((short)2);        // block align
            w.Write((short)16);       // bits per sample
            w.Write("data"u8);
            w.Write(dataBytes);
            for (var i = 0; i < count; i++)
            {
                var t = (double)i / rate;
                var envelope = Math.Min(1.0, t / 0.006) * Math.Exp(-t * 7.0);
                var tone = 0.60 * Math.Sin(2 * Math.PI * 987.77 * t)   // B5
                         + 0.28 * Math.Sin(2 * Math.PI * 1975.53 * t)  // B6 partial
                         + 0.12 * Math.Sin(2 * Math.PI * 1479.98 * t); // F#6 shimmer
                var sample = 0.45 * envelope * tone;
                w.Write((short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue));
            }
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        _speech.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _speechPump.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces here on shutdown.
        }
        _cts.Dispose();
    }
}
