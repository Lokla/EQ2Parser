namespace EQ2Parser.App.Services;

/// <summary>
/// What <see cref="AlertAudioService"/> needs from the OS: a voice list,
/// phrase → WAV file synthesis, and file playback. The service owns the
/// queueing, caching and safety rules; a backend only talks to the OS.
/// </summary>
public interface IAlertAudioBackend
{
    /// <summary>Installed voices, best first. Never throws.</summary>
    IReadOnlyList<TtsVoice> ListVoices();

    /// <summary>Render <paramref name="text"/> to a WAV at
    /// <paramref name="wavPath"/>. <paramref name="rate"/> is the speaking
    /// rate multiplier (1 = normal). False when the phrase could not be
    /// rendered.</summary>
    Task<bool> SynthesizeToFileAsync(string text, string? voiceId, double rate, string wavPath, CancellationToken ct);

    /// <summary>Play an audio file at <paramref name="volume"/> (0–1),
    /// completing when playback ends or <paramref name="ct"/> fires.</summary>
    Task PlayFileAsync(string path, double volume, CancellationToken ct);
}

/// <summary>A platform with no audio backend: alerts are silently skipped.</summary>
public sealed class SilentAlertAudioBackend : IAlertAudioBackend
{
    public IReadOnlyList<TtsVoice> ListVoices() => [];

    public Task<bool> SynthesizeToFileAsync(string text, string? voiceId, double rate, string wavPath, CancellationToken ct) =>
        Task.FromResult(false);

    public Task PlayFileAsync(string path, double volume, CancellationToken ct) => Task.CompletedTask;
}
