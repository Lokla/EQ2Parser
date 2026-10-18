using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;

namespace EQ2Parser.Mac.ViewModels;

/// <summary>A log being read, as listed in Settings.</summary>
public sealed record SourceRow(LogSource Source, string Owner, string FileName, string State);

/// <summary>
/// Settings: where logs come from (watched folders, each log being read)
/// and the alert voice. Audio settings apply and persist at once, as in
/// the WPF app (SettingsViewModel.PersistAudio).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SourceManager _manager;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly Func<Task<string?>> _pickLog;
    private string _sourcesSig = "";

    public ObservableCollection<string> WatchedFolders { get; } = [];
    public ObservableCollection<SourceRow> Sources { get; } = [];
    public IReadOnlyList<TtsVoice> Voices { get; }

    [ObservableProperty]
    private string? _selectedFolder;

    [ObservableProperty]
    private SourceRow? _selectedSource;

    [ObservableProperty]
    private bool _parseFromStart;

    [ObservableProperty]
    private TtsVoice? _selectedVoice;

    [ObservableProperty]
    private double _ttsRate;

    [ObservableProperty]
    private double _alertVolume;

    [ObservableProperty]
    private string _testPhrase = "Incoming add. Pull it to the tank.";

    [ObservableProperty]
    private string _status = "";

    public string DataFolder { get; } = AppSettings.Directory;

    // ---- EQ2Lexicon: trigger pack and parse uploads ----

    public string LexiconUrl => _manager.Settings.LexiconBaseUrl;

    /// <summary>Set when the run refuses every non-loopback request
    /// (LoopbackOnlyGuard) — shown so a test window is never mistaken for
    /// one that uploads.</summary>
    public bool LoopbackOnly { get; } = LoopbackOnlyGuard.IsRequested;

    [ObservableProperty]
    private bool _lexiconTriggersEnabled;

    [ObservableProperty]
    private bool _uploadEnabled;

    [ObservableProperty]
    private bool _uploadRaidOnly;

    [ObservableProperty]
    private string _uploadStatus = "";

    [ObservableProperty]
    private string _uploadTokenState = "";

    private bool _loadingUpload;

    public SettingsViewModel(SourceManager manager, Func<Task<string?>> pickFolder, Func<Task<string?>> pickLog)
    {
        _manager = manager;
        _pickFolder = pickFolder;
        _pickLog = pickLog;
        Voices = AlertAudioService.ListVoices();
        _selectedVoice = Voices.FirstOrDefault(v => v.Id == manager.Settings.TtsVoiceId);
        _ttsRate = manager.Settings.TtsRate;
        _alertVolume = manager.Settings.AlertVolume;
        ReloadFolders();

        _loadingUpload = true;
        LexiconTriggersEnabled = manager.Settings.LexiconTriggersEnabled;
        UploadEnabled = manager.Settings.UploadEnabled;
        UploadRaidOnly = manager.Settings.UploadRaidOnly;
        _loadingUpload = false;
        UploadTokenState = TokenProtector.Unprotect(manager.Settings.LexiconApiTokenProtected) is null
            ? Loc.Get("SettingsVm_NoTokenSaved")
            : Loc.Get("SettingsVm_TokenSavedState");
        UploadStatus = manager.Uploads.Status;
        manager.Uploads.StatusChanged += () => Dispatcher.UIThread.Post(() => UploadStatus = manager.Uploads.Status);
    }

    partial void OnLexiconTriggersEnabledChanged(bool value)
    {
        if (_loadingUpload)
            return;
        _manager.Settings = _manager.Settings with { LexiconTriggersEnabled = value };
        _manager.Settings.Save();
        _ = _manager.Lexicon.SetEnabledAsync(value);
    }

    partial void OnUploadEnabledChanged(bool value)
    {
        if (_loadingUpload)
            return;
        _manager.Settings = _manager.Settings with { UploadEnabled = value };
        _manager.Settings.Save();
        ReconfigureUploads();
    }

    partial void OnUploadRaidOnlyChanged(bool value)
    {
        if (_loadingUpload)
            return;
        _manager.Settings = _manager.Settings with { UploadRaidOnly = value };
        _manager.Settings.Save();
        ReconfigureUploads();
    }

    private void ReconfigureUploads() =>
        _manager.Uploads.Configure(
            _manager.Settings.LexiconBaseUrl,
            TokenProtector.Unprotect(_manager.Settings.LexiconApiTokenProtected),
            _manager.Settings.UploadEnabled,
            _manager.Settings.UploadRaidOnly);

    /// <summary>The token box is passed in and cleared here, as the WPF
    /// app does with its PasswordBox, so the token never sits in a bound
    /// property. It goes straight to the Keychain.</summary>
    [RelayCommand]
    private void SaveUploadToken(TextBox? box)
    {
        var token = box?.Text?.Trim() ?? "";
        if (token.Length == 0)
        {
            UploadStatus = Loc.Get("SettingsVm_PasteTokenFirst");
            return;
        }
        if (TokenProtector.Protect(token) is not { } marker)
        {
            UploadStatus = "Couldn't store the token in the macOS Keychain.";
            return;
        }
        box!.Text = "";
        _manager.Settings = _manager.Settings with { LexiconApiTokenProtected = marker };
        _manager.Settings.Save();
        UploadTokenState = Loc.Get("SettingsVm_TokenSavedState");
        ReconfigureUploads();
        UploadStatus = Loc.Get("SettingsVm_TokenSavedHitTest");
    }

    [RelayCommand]
    private async Task TestUploadToken()
    {
        UploadStatus = Loc.Get("SettingsVm_Testing");
        UploadStatus = await _manager.Uploads.TestTokenAsync();
    }

    /// <summary>Forget the token: settings drop the marker and auto-upload
    /// goes off. The Keychain entry is left for the next Save to overwrite.</summary>
    [RelayCommand]
    private void ClearUploadToken()
    {
        _manager.Settings = _manager.Settings with { LexiconApiTokenProtected = null, UploadEnabled = false };
        _manager.Settings.Save();
        _loadingUpload = true;
        UploadEnabled = false;
        _loadingUpload = false;
        UploadTokenState = Loc.Get("SettingsVm_NoTokenSaved");
        ReconfigureUploads();
        UploadStatus = Loc.Get("SettingsVm_TokenRemoved");
    }

    partial void OnSelectedVoiceChanged(TtsVoice? value)
    {
        _manager.Audio.VoiceId = value?.Id;
        PersistAudio();
    }

    partial void OnTtsRateChanged(double value)
    {
        _manager.Audio.SpeakingRate = value;
        PersistAudio();
    }

    partial void OnAlertVolumeChanged(double value)
    {
        _manager.Audio.Volume = value;
        PersistAudio();
    }

    private void PersistAudio()
    {
        _manager.Settings = _manager.Settings with
        {
            TtsVoiceId = SelectedVoice?.Id,
            TtsRate = TtsRate,
            AlertVolume = AlertVolume,
        };
        AppSettings.SaveSoon(() => _manager.Settings);
    }

    [RelayCommand]
    private void TestVoice() => _manager.Audio.SpeakTest(TestPhrase);

    private void ReloadFolders()
    {
        WatchedFolders.Clear();
        foreach (var f in _manager.WatchedFolders)
            WatchedFolders.Add(f);
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        if (await _pickFolder() is { } folder)
        {
            _manager.AddWatchedFolder(folder);
            ReloadFolders();
        }
    }

    [RelayCommand]
    private void DetectFolders()
    {
        var found = WineLogLocator.FindLogFolders();
        foreach (var folder in found)
            _manager.AddWatchedFolder(folder);
        ReloadFolders();
        Status = found.Count == 0
            ? "No EQ2 log folder found in CrossOver, Whisky or ~/.wine."
            : $"Watching {found.Count} log folder(s) found in Wine installs.";
    }

    [RelayCommand]
    private void RemoveFolder()
    {
        if (SelectedFolder is { } folder)
        {
            _manager.RemoveWatchedFolder(folder);
            ReloadFolders();
        }
    }

    [RelayCommand]
    private async Task AddLog()
    {
        if (await _pickLog() is { } path)
        {
            _manager.Add(path, ParseFromStart);
            _manager.PersistSources();
            Status = ParseFromStart ? "Reading the whole log — older fights fill the list as they are parsed." : "Reading new lines from now on.";
        }
    }

    [RelayCommand]
    private void RemoveLog()
    {
        if (SelectedSource is { } row)
        {
            _manager.Remove(row.Source);
            _manager.PersistSources();
        }
    }

    /// <summary>UI tick: the sources list follows the folder watcher.</summary>
    public void Tick()
    {
        var rows = _manager.Sources.Select(s => new SourceRow(
            s, s.Owner, Path.GetFileName(s.Path),
            s.Stopped is { } stopped ? $"stopped: {stopped.Reason}"
                : s.PendingBytes > 0 ? $"catching up ({s.PendingBytes / 1024:N0} KB to go)"
                : s.AutoDiscovered ? "live (found in a watched folder)" : "live")).ToList();
        var sig = string.Join("|", rows.Select(r => r.FileName + r.State));
        if (sig == _sourcesSig)
            return;
        _sourcesSig = sig;
        var selected = SelectedSource?.Source;
        Sources.Clear();
        foreach (var r in rows)
            Sources.Add(r);
        SelectedSource = Sources.FirstOrDefault(r => ReferenceEquals(r.Source, selected));
    }
}
