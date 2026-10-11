using EQ2Parser.App.Localization;
using EQ2Parser.App.Views;

namespace EQ2Parser.App.Services;

public enum OverlayKind
{
    MiniParseDps = 0,
    MiniParseHps = 1,
    MiniParseTank = 2,
    TimerA = 3,
    TimerB = 4,
    Notifications = 5,
}

/// <summary>
/// Owns the in-game overlay windows (three mini parse meters + the two
/// timer panels): show/hide, lock (click-through), and live-applied
/// appearance settings — everything persisted per window.
/// </summary>
public sealed class OverlayController(SourceManager manager)
{
    private readonly Dictionary<OverlayKind, OverlayShellWindow> _windows = [];

    /// <summary>Raised when any kind's settings change — the Overlays page
    /// mirrors its controls off this.</summary>
    public event Action<OverlayKind>? SettingsChanged;

    /// <summary>Mini parses are user-resizable (rows auto-fit the height);
    /// timer panels size themselves to their bars.</summary>
    public static bool IsResizable(OverlayKind kind) => kind
        is OverlayKind.MiniParseDps or OverlayKind.MiniParseHps or OverlayKind.MiniParseTank;

    public OverlayWindowSettings GetSettings(OverlayKind kind) => kind switch
    {
        OverlayKind.MiniParseDps => manager.Settings.MiniParseOverlay ?? new OverlayWindowSettings(),
        OverlayKind.MiniParseHps => manager.Settings.MiniParseHpsOverlay ?? new OverlayWindowSettings(),
        OverlayKind.MiniParseTank => manager.Settings.MiniParseTankOverlay ?? new OverlayWindowSettings(),
        OverlayKind.TimerB => manager.Settings.TimerOverlayB ?? new OverlayWindowSettings(),
        OverlayKind.Notifications => manager.Settings.NotificationsOverlay ?? new OverlayWindowSettings(),
        // Panel A inherits the legacy single-overlay fields on first run.
        _ => manager.Settings.TimerOverlayA ?? new OverlayWindowSettings
        {
            Visible = manager.Settings.OverlayVisible,
            Locked = manager.Settings.OverlayLocked,
            Left = manager.Settings.OverlayLeft,
            Top = manager.Settings.OverlayTop,
        },
    };

    /// <summary>Persist a settings change and push it onto the live window.</summary>
    public void Update(OverlayKind kind, Func<OverlayWindowSettings, OverlayWindowSettings> change)
    {
        var updated = change(GetSettings(kind));
        manager.Settings = kind switch
        {
            OverlayKind.MiniParseDps => manager.Settings with { MiniParseOverlay = updated },
            OverlayKind.MiniParseHps => manager.Settings with { MiniParseHpsOverlay = updated },
            OverlayKind.MiniParseTank => manager.Settings with { MiniParseTankOverlay = updated },
            OverlayKind.TimerB => manager.Settings with { TimerOverlayB = updated },
            OverlayKind.Notifications => manager.Settings with { NotificationsOverlay = updated },
            _ => manager.Settings with { TimerOverlayA = updated },
        };
        manager.Settings.Save();
        if (_windows.TryGetValue(kind, out var window))
            window.Apply(updated);
        SettingsChanged?.Invoke(kind);
    }

    public void Show(OverlayKind kind)
    {
        if (!_windows.ContainsKey(kind))
        {
            var settings = GetSettings(kind);
            System.Windows.Controls.UserControl content = kind switch
            {
                OverlayKind.MiniParseDps => new MiniParseContent(manager, "DPS"),
                OverlayKind.MiniParseHps => new MiniParseContent(manager, "HPS"),
                OverlayKind.MiniParseTank => new MiniParseContent(manager, "Tanking"),
                OverlayKind.TimerB => new TimerPanelContent(manager, panel: 2),
                OverlayKind.Notifications => new NotificationsContent(manager),
                _ => new TimerPanelContent(manager, panel: 1),
            };
            var window = new OverlayShellWindow(this, kind, TitleFor(kind), content, settings);
            window.Closed += (_, _) => _windows.Remove(kind);
            _windows[kind] = window;
            window.Show();
        }
        Update(kind, s => s with { Visible = true });
    }

    /// <summary>The header an unlocked overlay window shows: what it is,
    /// and the hint to drag it into place and lock it. One whole string
    /// per kind — the hint is not appended, so a translation is free to
    /// order the two halves its own way.</summary>
    internal static string TitleFor(OverlayKind kind) => kind switch
    {
        OverlayKind.MiniParseDps => Loc.Get("OverlayShell_TitleDps"),
        OverlayKind.MiniParseHps => Loc.Get("OverlayShell_TitleHps"),
        OverlayKind.MiniParseTank => Loc.Get("OverlayShell_TitleTank"),
        OverlayKind.TimerB => Loc.Get("OverlayShell_TitleTimerB"),
        OverlayKind.Notifications => Loc.Get("OverlayShell_TitleNotifications"),
        _ => Loc.Get("OverlayShell_TitleTimerA"),
    };

    public void Hide(OverlayKind kind)
    {
        if (_windows.TryGetValue(kind, out var window))
        {
            _windows.Remove(kind);
            window.Close();
        }
        Update(kind, s => s with { Visible = false });
    }

    private bool _suppressed;

    /// <summary>Focus auto-hide: temporarily hide/show every open overlay
    /// WINDOW without touching the persisted Visible flags — restoring
    /// focus (or unticking the option) brings back exactly what the user
    /// had. Distinct from Hide(kind), which closes and persists.</summary>
    public void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed)
            return;
        _suppressed = suppressed;
        foreach (var window in _windows.Values)
        {
            if (suppressed)
                window.Hide();
            else
                window.Show();
        }
    }

    public void SetLocked(OverlayKind kind, bool locked) =>
        Update(kind, s => s with { Locked = locked });

    public void SavePosition(OverlayKind kind, double left, double top) =>
        Update(kind, s => s with { Left = left, Top = top });

    /// <summary>Grip resize on a resizable overlay.</summary>
    public void SaveSize(OverlayKind kind, double width, double height) =>
        Update(kind, s => s with { Width = width, Height = height });

    /// <summary>Restore whichever overlays were open last session.</summary>
    public void RestoreFromSettings()
    {
        foreach (var kind in Enum.GetValues<OverlayKind>())
        {
            if (GetSettings(kind).Visible)
                Show(kind);
        }
    }
}
