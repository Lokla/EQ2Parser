# Add or change a persisted setting

Applies when you add, rename, retype or remove anything stored in `settings.json`: a property of the
`AppSettings` record or of `OverlayWindowSettings`.

## Checklist

1. `src/EQ2Parser.App/Services/AppSettings.cs`: add an `init` property to `AppSettings` with its default as
   the initializer and a doc comment that says what the default (or null) means. Every existing install loads
   a file without this field, so the initializer is the value they all get.
2. Decide how the value reaches its consumer. Reading `manager.Settings.X` at the point of use needs no wiring
   (the `Callouts.Callout` handler in `SourceManager` reads `CalloutsEnabled` this way). If a service has to
   hold the value, push it in the `SourceManager` constructor in
   `src/EQ2Parser.App/Services/SourceManager.cs`; that is the startup half.
3. `src/EQ2Parser.App/ViewModels/SettingsViewModel.cs` (or the view-model of the page that owns the option,
   as `OverlaysViewModel` does for overlay options and `RaidViewModel` for raid values):
   - an `[ObservableProperty]` backing field;
   - the load in the constructor: either assign the backing field (no change handler fires), or assign the
     property inside a `_loading...` guard such as `_loadingUpload` or `_loadingCallouts`;
   - `partial void OnXChanged`: return while loading, set
     `_manager.Settings = _manager.Settings with { X = value }`, persist, then re-apply to the live service
     with the same call as step 2. That is the runtime half.
   - Persist with `_manager.Settings.Save()` for a toggle, `AppSettings.SaveSoon(() => _manager.Settings)`
     for a slider (debounced), or through the `Save` command for a validated text box (a range check with a
     `SettingsVm_*Range` message).
4. `src/EQ2Parser.App/Views/SettingsView.xaml`: a control in the right card, bound to the property, with its
   label and `ToolTip` as `{loc:Tr Settings_...}`.
5. Strings in all four dictionaries (`Settings_X`, `Settings_XTip`, any `SettingsVm_...` status text). See
   `locale-string.md`.
6. `tests/EQ2Parser.App.Tests/AppSettingsCompatTests.cs`: assert the default in
   `Minimal_Old_Shape_Loads_With_Defaults_For_Absent_Fields`; add a round-trip for a nested or nullable shape.

To rename or remove: keep the old property on the record as a read-only legacy input and migrate where the
value is used. The pattern is the `OverlayVisible` / `OverlayLocked` / `OverlayLeft` / `OverlayTop` fields,
which `OverlayController.GetSettings` folds into Timer A and `Legacy_Single_Overlay_Fields_Still_Deserialize`
pins. Do not change the type of an existing property; add a new one.

## Why these and what goes wrong

- `AppSettings.Load` goes through `PersistedJsonFile.Load` with default serializer options. Unknown JSON fields
  are ignored and absent ones take the initializer, so a renamed property silently resets to its default and
  the old value is dropped on the next save. A value of the wrong type makes deserialization throw: the whole
  file is renamed to `settings.json.corrupt-*` and every setting resets.
- Startup and runtime are two call sites. `Uploads.Configure(...)` is called from the `SourceManager`
  constructor and from `SettingsViewModel.ReconfigureUploads`, and its last parameter is optional
  (`raidOnly = false`), so a call site that omits a new argument still compiles and passes the default.
  `Audio.*` and `Callouts.*` have the same pair (the constructor, then `OnXChanged` or `PersistCallouts`).
- `manager.Settings` is replaced whole by several owners (`OverlayController.Update`, `ColumnSets`, the export
  spec, the raid page). Build the new record from the current `_manager.Settings`, never from a copy captured
  earlier, or you write someone else's stale values back.
- A value read in `SourceManager.Add` (`IdleEndSeconds`, `PollMilliseconds`) only affects log sources attached
  afterwards. Say so in the tooltip if your setting behaves that way.
- If the setting changes what is uploaded, use the `parser-contract` skill as well.

## Tests

- `tests/EQ2Parser.App.Tests/AppSettingsCompatTests.cs` (old shapes load, defaults, round-trip).
- `tests/EQ2Parser.Core.Tests/PersistedJsonFileTests.cs` if you touch how files are written.
- `tests/EQ2Parser.App.Tests/UploadServiceRaidOnlyTests.cs` if `UploadService.Configure` changes.
- `tests/EQ2Parser.Core.Tests/LocalizationTests.cs` for the strings.
