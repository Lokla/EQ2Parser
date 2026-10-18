using EQ2Parser.App.Services;

namespace EQ2Parser.Shell.Tests;

public sealed class AlertAudioServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eq2parser-shell-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Records what the service asks of the OS; playback of a
    /// phrase lasts until the test releases it.</summary>
    private sealed class FakeBackend : IAlertAudioBackend
    {
        public readonly List<string> Synthesized = [];
        public readonly List<string> Played = [];
        public TaskCompletionSource FirstPlayback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<TtsVoice> ListVoices() => [];

        public Task<bool> SynthesizeToFileAsync(string text, string? voiceId, double rate, string wavPath, CancellationToken ct)
        {
            lock (Synthesized)
                Synthesized.Add(text);
            File.WriteAllText(wavPath, text);
            return Task.FromResult(true);
        }

        public async Task PlayFileAsync(string path, double volume, CancellationToken ct)
        {
            var text = File.ReadAllText(path);
            lock (Played)
                Played.Add(text);
            FirstPlayback.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task Phrases_Queue_Instead_Of_Talking_Over_Each_Other()
    {
        var backend = new FakeBackend();
        using var audio = new AlertAudioService(backend, _dir);

        audio.Speak("first");
        audio.Speak("second");
        await backend.FirstPlayback.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (backend.Played)
            Assert.Equal(["first"], backend.Played); // second waits for first to finish

        backend.Release.SetResult();
        await audio.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first", "second"], backend.Played);
    }

    [Fact]
    public async Task A_Repeated_Phrase_Is_Rendered_Once()
    {
        var backend = new FakeBackend();
        backend.Release.SetResult();
        using var audio = new AlertAudioService(backend, _dir);

        audio.Speak("adds incoming");
        audio.Speak("adds incoming");
        await audio.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["adds incoming"], backend.Synthesized);
        Assert.Equal(2, backend.Played.Count);
    }

    [Fact]
    public async Task Flush_Waits_For_What_Was_Queued_Before_It()
    {
        var backend = new FakeBackend();
        using var audio = new AlertAudioService(backend, _dir);

        audio.Speak("one");
        var flushed = audio.FlushAsync();
        await backend.FirstPlayback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(flushed.IsCompleted);

        backend.Release.SetResult();
        await flushed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(@"\\attacker\share\a.wav")]
    [InlineData("//attacker/share/a.wav")]
    [InlineData("http://example.invalid/a.wav")]
    [InlineData("smb://host/a.wav")]
    [InlineData("")]
    [InlineData("   ")]
    public void Sound_Paths_Off_This_Machine_Are_Refused(string path) =>
        Assert.True(AlertAudioService.IsNonLocalPath(path));

    [Theory]
    [InlineData("/Users/me/sounds/alert.wav")]
    [InlineData("sounds/alert.wav")]
    [InlineData("file:///Users/me/alert.wav")]
    public void Local_Sound_Paths_Are_Allowed(string path) =>
        Assert.False(AlertAudioService.IsNonLocalPath(path));

    [Fact]
    public void The_Chime_Is_A_Well_Formed_Pcm_Wav()
    {
        var wav = AlertAudioService.BuildChime();

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(44100, BitConverter.ToInt32(wav, 24));
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
    }
}
