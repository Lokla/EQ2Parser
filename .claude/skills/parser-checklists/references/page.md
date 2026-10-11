# Add a top-level page

Applies when a new tab is added to the shell's tab strip, beside Main, Sources, Triggers, Timers, Overlays,
Raid and Settings.

## Checklist

1. View-model: a new `XViewModel` class in `src/EQ2Parser.App/ViewModels/`, an `ObservableObject` whose
   constructor takes the `SourceManager`. Give it a `Refresh()` method if it shows live data. Logic that can
   be tested belongs in `src/EQ2Parser.Core`, with the view-model as an adapter.
2. View: a new `XView` `UserControl` (XAML plus code-behind) in `src/EQ2Parser.App/Views/`. Do not set a
   `DataContext`; the view receives the view-model through the template in step 3. Follow
   `src/EQ2Parser.App/Views/SettingsView.xaml` for the page title and card styles.
3. `src/EQ2Parser.App/App.xaml`: add a `DataTemplate` with `DataType="{x:Type vm:XViewModel}"` that contains
   `<views:XView />`, beside the existing seven.
4. `src/EQ2Parser.App/ViewModels/MainViewModel.cs`:
   - a public property for the view-model, constructed in the constructor with the others;
   - an entry in `NavItems`: `new NavItem(Loc.Get("Nav_X"), X)`, placed before Settings;
   - a branch in `Tick` that calls `X.Refresh()` when `SelectedItem.Page == X`, if the page has one.
5. Strings in all four dictionaries: `Nav_X` for the tab, then the page's own keys under an `X_` prefix for
   XAML and `XVm_` for view-model text. See `locale-string.md`.
6. If the tab is shown only to some users, copy the Raid pattern: a private `NavItem` field kept out of the
   initial list and a `SyncRaidNav`-style method that inserts or removes it on the UI thread.

## Why these and what goes wrong

- The shell in `src/EQ2Parser.App/MainWindow.xaml` binds a `ContentControl` to `SelectedItem.Page`, and WPF
  picks the view by the view-model's type. Without the `DataTemplate` the tab shows the view-model's type name
  as plain text. Nothing fails at build time.
- The tab strip binds to `NavItems`, so `MainWindow.xaml` needs no edit.
- `Tick` refreshes only the page on screen, through an `if` / `else if` chain on `SelectedItem.Page`. A page
  left out of the chain renders once and never updates. The tick runs about every 100 ms on the UI thread, so
  keep `Refresh` cheap, and add unconditional work (as `Raid.Refresh()` is) only when it has to run while
  another tab is showing.
- `SyncRaidNav` inserts the Raid tab at `NavItems.Count - 1`, which assumes the last item is Settings. Keep
  Settings last, or Raid lands in the wrong place.
- `NavItems` labels are resolved once in the constructor. That is fine: a language change applies on restart.
- Page view-models are built once in the `MainViewModel` constructor and live for the whole session. Subscribe
  to service events there, and marshal anything raised on a background thread through the dispatcher before
  touching bound collections, as the `AttendanceAccessChanged` handler does.
- Back navigation (`NavigateBack`, `_tabHistory`) works on `NavItem` references and needs no change for an
  ordinary page. A conditional tab has to be removed from `_tabHistory` when it is hidden, as `SyncRaidNav`
  does.
- A page that owns settings follows `setting.md`; `OverlaysViewModel` shows the lightweight form, plain
  properties that read and write `_manager.Settings` directly.

## Tests

- Nothing automated covers the shell. Use the `parser-ui-check` skill: open the tab, check that the view
  renders rather than a type name, switch away and back, and confirm live data updates.
- `tests/EQ2Parser.Core.Tests/LocalizationTests.cs` for the strings.
- New Core logic gets its own test file under `tests/EQ2Parser.Core.Tests/`. If it adds types to
  `src/EQ2Parser.Core/Combat`, `tests/EQ2Parser.Core.Tests/LayeringTests.cs` checks that folder references no
  higher Core layer.
