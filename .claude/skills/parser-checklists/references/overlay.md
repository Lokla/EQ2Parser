# Add an overlay

Applies when a new in-game overlay window is added (a new `OverlayKind`), or when an overlay gains its own
option. The overlays share one shell window and one card on the Overlays page; what differs per kind is
selected by switches.

## Checklist

1. `src/EQ2Parser.App/Services/OverlayController.cs`: append a member to `OverlayKind` with the next number.
2. `src/EQ2Parser.App/Services/AppSettings.cs`: add an `OverlayWindowSettings? XOverlay` property to
   `AppSettings`, null meaning never configured. An option that only this kind uses is a new property on
   `OverlayWindowSettings` with an `init` default (see `setting.md`).
3. Back in `OverlayController`, add an explicit arm for the new kind to all four switches: `GetSettings`,
   `Update`, and the content and title switches inside `Show`. Add the kind to `IsResizable` if the user
   resizes it with the grip.
4. The content control: a new `UserControl` in `src/EQ2Parser.App/Views/` that implements `IOverlayContent`
   (declared in `src/EQ2Parser.App/Views/OverlayShellWindow.xaml.cs`). `Refresh(OverlayWindowSettings)` is
   called on the shell's tick and returns true while there is something to show. Model it on
   `src/EQ2Parser.App/Views/NotificationsContent.xaml.cs`.
5. `src/EQ2Parser.App/ViewModels/OverlaysViewModel.cs`: add
   `new OverlayConfigVm(overlay, OverlayKind.X, Loc.Get("Overlays_TitleX"), Loc.Get("Overlays_Items..."))`
   to `Cards`.
6. For an option of its own, in `OverlayConfigVm`: a `Has...` flag set from the kind, an
   `[ObservableProperty]`, a line in `SyncFrom`, and an `On...Changed` partial that calls `_controller.Update`
   unless `_syncing`. Then add the control to the `OverlayCard` template in
   `src/EQ2Parser.App/Views/OverlaysView.xaml`, with its `Visibility` bound to the flag.
7. Strings in all four dictionaries: `Overlays_TitleX` and anything new on the card.
8. `tests/EQ2Parser.App.Tests/AppSettingsCompatTests.cs`: the new property is null when absent and survives a
   save and load.

## Why these and what goes wrong

- Timer A has no arm of its own. In every one of the four switches it is the `_ =>` default, so a kind you
  forget becomes Timer A without any warning:
  - missing from `GetSettings` or `Update`, the new overlay reads and writes `TimerOverlayA`, so dragging or
    hiding it moves or hides the user's Timer A;
  - missing from the content switch, `Show` opens a second Timer A panel;
  - missing from the title switch, it is labelled as the timers panel.
- `OverlayShellWindow` casts the content to `IOverlayContent` in its constructor. A control that does not
  implement it throws when the overlay is first shown, not at build time.
- `OverlayConfigVm` infers two card sections from `IsResizable`: a resizable kind gets the four meter-column
  toggles (`ColClass`, `ColDeaths`, `ColValue`, `ColShare`, stored in `MeterColumns`), and every other kind
  gets the `MaxItems` slider. If neither suits the new overlay, add a flag instead of shipping a control that
  does nothing.
- `OverlayController.RestoreFromSettings` walks `Enum.GetValues<OverlayKind>()`, so restoring at startup needs
  no registration. It also means a kind that falls through to Timer A in `GetSettings` is shown at startup
  whenever Timer A is visible.
- The default position in `OverlayShellWindow` is derived from the enum number (`% 3` and `/ 3`). The numbers
  are not persisted, but append new members so existing defaults do not move.
- `Hide` closes the window and persists `Visible = false`; `SetSuppressed` (focus auto-hide) only hides
  windows and must leave the persisted flags alone. Do not route a new feature through the wrong one.
- `Refresh` runs on a 150 ms UI-thread timer. Build from a service snapshot and patch the existing rows in
  place, as `TimerPanelContent` does with `SpellTimers.Snapshot`; do not rebuild the list.

## Tests

- Nothing automated opens an overlay. Use the `parser-ui-check` skill: show it, drag it, lock it, restart, and
  confirm Timer A's position and visibility did not change.
- `tests/EQ2Parser.App.Tests/AppSettingsCompatTests.cs` and `tests/EQ2Parser.Core.Tests/LocalizationTests.cs`;
  `tests/EQ2Parser.Core.Tests/MiniParseBuilderTests.cs` if the content reuses the meter logic.
