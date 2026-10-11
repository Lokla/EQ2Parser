# Add a UI string or a language

Applies when any text a user can read is added or reworded, and when a new interface language is added. Parse
grammar, log data and report output are not localized.

## Checklist: a string

1. Choose the key. Keys are `Prefix_Name`, and the prefix says where the string is used: the page name for
   XAML (`Settings_`, `Main_`, `Overlays_`, `Triggers_`, `Timers_`, `Raid_`, `Nav_`, `Cols_`), the page name
   plus `Vm` for view-model code (`SettingsVm_`, `TriggersVm_`), and `...Svc_` for services (`UploadSvc_`).
2. Add the key to all four files, at the same position beside its prefix group:
   `src/EQ2Parser.App/Localization/strings.en.json`, `src/EQ2Parser.App/Localization/strings.de.json`,
   `src/EQ2Parser.App/Localization/strings.fr.json`, `src/EQ2Parser.App/Localization/strings.ru.json`. The
   four files share one key order, which keeps a translation diff readable.
3. Use the same `{0}`, `{1}` placeholders in every language. Their order may differ; the set may not.
4. Reference it as `{loc:Tr Key}` in XAML, `Loc.Get("Key")` in code, or `Loc.Format("Key", args)` for a string
   with placeholders. `Loc` is in `src/EQ2Parser.App/Localization/Loc.cs`.
5. If the key is built at runtime (see below), add every variant by hand.

## Checklist: a language

1. Add a `strings.xx.json` beside the others in `src/EQ2Parser.App/Localization/`, with exactly the English
   key set.
2. Add `("xx", "Native name")` to `Loc.Languages`. The Settings language list and the OS-language rule
   (`UiLanguage.Resolve` in `src/EQ2Parser.Core/UiLanguage.cs`) both read from it.
3. In `tests/EQ2Parser.Core.Tests/LocalizationTests.cs`, add the code to both lists, `LanguageCodes` and
   `Supported`, and add an `InlineData` row to `Os_Language_Defaults_Are_Honoured`.
4. In `src/EQ2Parser.App/EQ2Parser.App.csproj`, confirm the `EmbeddedResource` item for
   `Localization\strings.*.json` still carries `WithCulture="false"`. The wildcard picks the new file up, so
   the item itself should not need an edit.
5. Update the doc comment on `AppSettings.LanguageCode`, which lists the codes.

## Why these and what goes wrong

- `LocalizationTests` checks four things: every language has exactly the English keys, placeholders match
  English, every referenced key exists in English, and the dictionaries are embedded in the built App assembly
  (that last check is skipped when the App has not been built).
- "Referenced" means a literal: `{loc:Tr Key}` in XAML, or `Loc.Get("Key")` / `Loc.Format("Key"` in C#. Keys
  built at runtime are invisible to the test, and a missing one renders as the raw key:
  - `Loc.Get($"Arch_{a}")` in `src/EQ2Parser.App/Views/ExportWindow.xaml.cs`, one per `Archetype.All`;
  - the `locPrefix + "..."` lookups in `src/EQ2Parser.App/ViewModels/ActImportFlow.cs`, where every suffix has
    to exist under both `TriggersVm_` and `TimersVm_`;
  - ternaries inside the call, such as the `...One` / `...Many` pairs in `TriggersViewModel` and
    `TimersViewModel` and the token-state lookup in `SettingsViewModel`.
- Without `WithCulture="false"` MSBuild reads `.de` or `.en` in the file name as a culture and moves the
  dictionary into a satellite assembly. The main assembly then has no strings and every label shows its key.
  That shipped once.
- `Loc.Get` before `Loc.Initialize` returns the key. Startup (`App.Start`) loads the settings, calls
  `Loc.Initialize`, and only then builds `SourceManager` and with it every service. `SourceManager` takes the
  settings as a constructor parameter so that order cannot be undone, and
  `tests/EQ2Parser.App.Tests/SourceManagerStartupTests.cs` pins it. So a string resolved in a service
  constructor is real text in the app. A test that builds a service without calling `Loc.Initialize` still
  gets the key; `LexiconSyncService.Status` resolves lazily for that reason.
- `{loc:Tr}` and static tables (`ColumnSets`, `ExportColumnDefs`, `NavItems`) resolve once. A language change
  applies on restart; do not build live switching around one string.
- A missing translation falls back to English, then to the key, so nothing crashes and nothing warns. Only
  the tests and your eyes catch it.

## Tests

- `tests/EQ2Parser.Core.Tests/LocalizationTests.cs`. Build the App first so the embedding check runs against
  a fresh assembly, then use the `parser-ui-check` skill to see the string rendered.
