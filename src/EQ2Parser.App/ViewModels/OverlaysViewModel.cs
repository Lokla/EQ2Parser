using CommunityToolkit.Mvvm.ComponentModel;
using EQ2Parser.App.Localization;
using EQ2Parser.App.Services;

namespace EQ2Parser.App.ViewModels;

/// <summary>One overlay window's configuration card. Every change applies
/// to the live window instantly and persists.</summary>
public sealed partial class OverlayConfigVm : ObservableObject
{
    private readonly OverlayController _controller;
    private readonly OverlayKind _kind;
    private bool _syncing;

    public string Title { get; }
    public string ItemsLabel { get; }

    /// <summary>Timer panels cap their bars with a slider; mini parses are
    /// grip-resized instead (rows auto-fit the height).</summary>
    public bool HasMaxItems { get; }
    public bool IsResizable => !HasMaxItems;

    /// <summary>Notifications only: the toast stay-duration slider.</summary>
    public bool HasToastSeconds { get; }

    /// <summary>Mini parses only: the row-column toggles.</summary>
    public bool HasMeterColumns => IsResizable;

    public OverlayConfigVm(OverlayController controller, OverlayKind kind, string title, string itemsLabel)
    {
        _controller = controller;
        _kind = kind;
        Title = title;
        ItemsLabel = itemsLabel;
        HasMaxItems = !OverlayController.IsResizable(kind);
        HasToastSeconds = kind == OverlayKind.Notifications;
        SyncFrom(controller.GetSettings(kind));
        controller.SettingsChanged += changed =>
        {
            if (changed == kind)
                SyncFrom(controller.GetSettings(kind));
        };
    }

    private void SyncFrom(OverlayWindowSettings s)
    {
        _syncing = true;
        Visible = s.Visible;
        Locked = s.Locked;
        Opacity = s.Opacity;
        Scale = s.Scale;
        Width = s.Width;
        MaxItems = s.MaxItems;
        ToastSeconds = s.ToastSeconds;
        ColClass = s.MeterColumns is null || s.MeterColumns.Contains("Class");
        ColDeaths = s.MeterColumns is null || s.MeterColumns.Contains("Deaths");
        ColValue = s.MeterColumns is null || s.MeterColumns.Contains("Value");
        ColShare = s.MeterColumns is null || s.MeterColumns.Contains("Share");
        _syncing = false;
    }

    /// <summary>Persist the four column toggles. All on → null, the
    /// "defaults" state old settings files already have.</summary>
    private void SaveColumns()
    {
        if (_syncing)
            return;
        List<string> picked = [];
        if (ColClass) picked.Add("Class");
        if (ColDeaths) picked.Add("Deaths");
        if (ColValue) picked.Add("Value");
        if (ColShare) picked.Add("Share");
        _controller.Update(_kind, s => s with { MeterColumns = picked.Count == 4 ? null : picked });
    }

    [ObservableProperty]
    private bool _visible;

    [ObservableProperty]
    private bool _locked;

    [ObservableProperty]
    private double _opacity;

    [ObservableProperty]
    private double _scale;

    [ObservableProperty]
    private double _width;

    [ObservableProperty]
    private int _maxItems;

    [ObservableProperty]
    private double _toastSeconds;

    [ObservableProperty]
    private bool _colClass;

    [ObservableProperty]
    private bool _colDeaths;

    [ObservableProperty]
    private bool _colValue;

    [ObservableProperty]
    private bool _colShare;

    partial void OnColClassChanged(bool value) => SaveColumns();
    partial void OnColDeathsChanged(bool value) => SaveColumns();
    partial void OnColValueChanged(bool value) => SaveColumns();
    partial void OnColShareChanged(bool value) => SaveColumns();

    public string OpacityLabel => $"{Opacity:P0}";
    public string ScaleLabel => $"{Scale:0.00}×";
    public string WidthLabel => $"{Width:0}px";
    public string MaxItemsLabel => MaxItems.ToString();
    public string ToastSecondsLabel => Loc.Format("Overlays_ToastSecondsLabel", ToastSeconds);

    partial void OnVisibleChanged(bool value)
    {
        if (_syncing)
            return;
        if (value)
            _controller.Show(_kind);
        else
            _controller.Hide(_kind);
    }

    partial void OnLockedChanged(bool value)
    {
        if (!_syncing)
            _controller.SetLocked(_kind, value);
    }

    partial void OnOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(OpacityLabel));
        if (!_syncing)
            _controller.Update(_kind, s => s with { Opacity = Math.Clamp(value, 0.3, 1) });
    }

    partial void OnScaleChanged(double value)
    {
        OnPropertyChanged(nameof(ScaleLabel));
        if (!_syncing)
            _controller.Update(_kind, s => s with { Scale = Math.Clamp(value, 0.7, 1.6) });
    }

    partial void OnWidthChanged(double value)
    {
        OnPropertyChanged(nameof(WidthLabel));
        if (!_syncing)
            _controller.Update(_kind, s => s with { Width = Math.Clamp(value, 200, 480) });
    }

    partial void OnMaxItemsChanged(int value)
    {
        OnPropertyChanged(nameof(MaxItemsLabel));
        if (!_syncing)
            _controller.Update(_kind, s => s with { MaxItems = Math.Clamp(value, 3, 25) });
    }

    partial void OnToastSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(ToastSecondsLabel));
        if (!_syncing)
            _controller.Update(_kind, s => s with { ToastSeconds = Math.Clamp(value, 2, 30) });
    }
}

/// <summary>The Overlays page: three mini parse meters (DPS, healing,
/// tanking — run any combination) and both timer panels, each fully
/// configurable with instant visual feedback.</summary>
public sealed class OverlaysViewModel : ObservableObject
{
    public IReadOnlyList<OverlayConfigVm> Cards { get; }

    private readonly SourceManager _manager;
    private readonly OverlayController _overlay;

    public OverlaysViewModel(OverlayController overlay, SourceManager manager)
    {
        _manager = manager;
        _overlay = overlay;
        Cards =
        [
            new OverlayConfigVm(overlay, OverlayKind.MiniParseDps, Loc.Get("Overlays_TitleDps"), Loc.Get("Overlays_ItemsRows")),
            new OverlayConfigVm(overlay, OverlayKind.MiniParseHps, Loc.Get("Overlays_TitleHps"), Loc.Get("Overlays_ItemsRows")),
            new OverlayConfigVm(overlay, OverlayKind.MiniParseTank, Loc.Get("Overlays_TitleTank"), Loc.Get("Overlays_ItemsRows")),
            new OverlayConfigVm(overlay, OverlayKind.TimerA, Loc.Get("Overlays_TitleTimerA"), Loc.Get("Overlays_ItemsBars")),
            new OverlayConfigVm(overlay, OverlayKind.TimerB, Loc.Get("Overlays_TitleTimerB"), Loc.Get("Overlays_ItemsBars")),
            new OverlayConfigVm(overlay, OverlayKind.Notifications, Loc.Get("Overlays_TitleNotifications"), Loc.Get("Overlays_ItemsToasts")),
        ];
    }

    /// <summary>The focus auto-hide toggle. Applies live: unticking
    /// un-suppresses immediately (no waiting for the next alt-tab).</summary>
    public bool AutoHide
    {
        get => _manager.Settings.OverlayAutoHide;
        set
        {
            _manager.Settings = _manager.Settings with { OverlayAutoHide = value };
            _manager.Settings.Save();
            if (!value)
                _overlay.SetSuppressed(false);
        }
    }

    /// <summary>The tick box: fade all three meters after a fight.</summary>
    public bool MiniParseFadeEnabled
    {
        get => _manager.Settings.MiniParseFadeEnabled;
        set
        {
            _manager.Settings = _manager.Settings with { MiniParseFadeEnabled = value };
            _manager.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Seconds before the fade. Debounced save — rides a slider.</summary>
    public int MiniParseFadeSeconds
    {
        get => _manager.Settings.MiniParseFadeSeconds;
        set
        {
            if (_manager.Settings.MiniParseFadeSeconds == value)
                return;
            _manager.Settings = _manager.Settings with { MiniParseFadeSeconds = value };
            AppSettings.SaveSoon(() => _manager.Settings);
            OnPropertyChanged(nameof(MiniParseFadeSecondsLabel));
        }
    }

    public string MiniParseFadeSecondsLabel =>
        Loc.Format("Overlays_FadeSecondsLabel", MiniParseFadeSeconds);
}
