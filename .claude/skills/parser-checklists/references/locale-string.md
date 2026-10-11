# Add a UI string or a language

Applies when any text a user can read is added or reworded, and when a new interface language is added.

## What is localized and what is not

Decide every string against this before choosing a key.

- **Localize interface text**: what the user reads in the app's own windows. Labels, buttons, tooltips, status
  lines, window and dialog titles, dialog text, toast text, file-dialog titles and filter labels.
- **Do not localize data or diagnostics**:
  - anything the log named (abilities, mobs, zones, damage schools) and the control words a timer stores
    (`Vocabulary.ControlEffects` in `src/EQ2Parser.Core/Triggers/Vocabulary.cs`);
  - report and export output, and clipboard summaries;
  - crash-report files and the session journal, which the developer reads;
  - exception messages, even when one is shown inside a translated line;
  - text handed to the speech engine. The voice is the one the user picked in Settings, never one chosen for
    the interface language, and a translated sentence read by an English voice is worse than English;
  - anything written to a file a script or the game reads (the raid command files, the DKP ledger);
  - identifiers that are compared or persisted, such as the "General" category default.
- **A string that is both shown and spoken or logged is split.** Localize the shown copy only.
  `SourceManager.CalloutToast` is the pattern: the callout is spoken in English and toasted in the interface
  language.
- **A translated line may carry untranslated data** as an argument: the Sources row keeps the exception type
  name, the curation labels keep the damage school.

## Checklist: a string

1. Choose the key. Keys are `Prefix_Name`, and the prefix says where the string is used: the page name for
   XAML (`Settings_`, `Main_`, `Overlays_`, `Triggers_`, `Timers_`, `Raid_`, `Nav_`, `Cols_`), the page name
   plus `Vm` for view-model code (`SettingsVm_`, `TriggersVm_`), `...Svc_` for services (`UploadSvc_`), and
   the view or class name where one class serves several pages (`OverlayShell_`, `CategoryTree_`, `CrashDlg_`,
   `Callout_`). A page whose keys already share one prefix across XAML and code (`Sources_`, `Curation_`,
   `Archive_`) keeps it.
2. Add the key to all four files, at the same position beside its prefix group:
   `src/EQ2Parser.App/Localization/strings.en.json`, `src/EQ2Parser.App/Localization/strings.de.json`,
   `src/EQ2Parser.App/Localization/strings.fr.json`, `src/EQ2Parser.App/Localization/strings.ru.json`. The
   four files share one key order, which keeps a translation diff readable. Add lines; do not re-sort or
   re-indent a file.
3. Use the same `{0}`, `{1}` placeholders in every language. Their order may differ; the set may not. A
   format spec (`{0:N0}`, `{1:0.#}`) belongs in the dictionary string, so pass the number, not a formatted
   string.
4. Reference it as `{loc:Tr Key}` in XAML, `Loc.Get("Key")` in code, or `Loc.Format("Key", args)` for a string
   with placeholders. `Loc` is in `src/EQ2Parser.App/Localization/Loc.cs`. Keep the key a plain literal at the
   call site.
5. Write whole strings. Never join translated fragments into a sentence, and never build one around English
   word order or English plurals:
   - a count gets a `...One` / `...Many` pair, picked at the call site with
     `Loc.Format(n == 1 ? "X_One" : "X_Many", n)`;
   - Russian has more than two forms, so word a Russian count so the noun does not change with the number
     (`боёв: {0}`); do the same in German or French when a line has no One/Many pair to lean on;
   - several independent facts on one line are several whole strings joined with a separator (` · `), as
     `SourcesViewModel.StatusFor` and `ActImportFlow` do.
6. Reuse the words the dictionaries already use for fight, source, trigger, timer, overlay, raid, mob and
   ability. Search the file before introducing a new one.
7. A file-dialog filter is `label|pattern|label|pattern`. Name its key `...FileFilter` and translate the
   labels only; the test that keeps the patterns intact finds filters by that suffix.
8. If the key has to be built at runtime, see the next section before writing the call.

## Keys built at runtime

A key that is not a plain literal at its call site is invisible to the literal scan, and a missing one renders
as the raw key. `LocalizationTests.Dynamic_Loc_Calls_Are_On_The_Allow_List` fails on any `Loc.Get` /
`Loc.Format` whose first argument is not a literal, until the call site is on that test's `DynamicCallSites`
list beside a family test. The families that exist:

| Call | Family | Test that enumerates it |
|---|---|---|
| `Loc.Get($"Arch_{a}")` in `src/EQ2Parser.App/Views/ExportWindow.xaml.cs` | one key per `Archetype.All` | `Archetype_Keys_Exist_In_Every_Language` |
| `locPrefix + "..."` in `src/EQ2Parser.App/ViewModels/ActImportFlow.cs` | every suffix in that file under every prefix a caller passes (`TriggersVm_`, `TimersVm_`) | `Act_Import_Keys_Exist_For_Every_Page_In_Every_Language` |
| `Callout_{effect}_One` / `_Many` in `SourceManager.CalloutToast` | one pair per `Vocabulary.ControlEffects` | `Callout_Keys_Exist_For_Every_Control_Effect_In_Every_Language` |
| `CrashLog.DialogText(key)` in `src/EQ2Parser.App/Services/CrashLog.cs` | every `CrashDlg_` literal in that file | `Crash_Dialog_Keys_Exist_In_Every_Language` |
| `{loc:Tr Key}`, resolved by `TrExtension` in `Loc.cs` | the XAML scan | `Every_Referenced_Key_Exists_In_English` |

A call that picks between two literal keys (`n == 1 ? "X_One" : "X_Many"`) needs no entry:
`Conditional_Keys_Exist_In_Every_Language` reads both keys from every such call.

To add a family:

1. Prefer a literal. A `switch` with one `Loc.Get("...")` per arm is guarded for free
   (`OverlayController.TitleFor`).
2. Otherwise add every variant to all four dictionaries, write a test in
   `tests/EQ2Parser.Core.Tests/LocalizationTests.cs` that enumerates the family from the list or enum that
   drives it and asserts every key with `AssertInEveryLanguage`, and add the call site to `DynamicCallSites`
   naming that test. One entry stands for one call site.
3. Do not import `Loc` by alias or `using static`, and do not pass `Loc.Get` on as a delegate. Both hide the
   call from the scan, and the tripwire fails on them.

## Checklist: a language

1. Add a `strings.xx.json` beside the others in `src/EQ2Parser.App/Localization/`, with exactly the English
   key set.
2. Add `("xx", "Native name")` to `Loc.Languages`. The Settings language list and the OS-language rule
   (`UiLanguage.Resolve` in `src/EQ2Parser.Core/UiLanguage.cs`) both read from it.
3. In `tests/EQ2Parser.Core.Tests/LocalizationTests.cs`, add the code to both lists, `LanguageCodes` and
   `Supported`, and add an `InlineData` row to `Os_Language_Defaults_Are_Honoured`. The family tests then
   cover the new language without further change.
4. In `src/EQ2Parser.App/EQ2Parser.App.csproj`, confirm the `EmbeddedResource` item for
   `Localization\strings.*.json` still carries `WithCulture="false"`. The wildcard picks the new file up, so
   the item itself should not need an edit.
5. Update the doc comment on `AppSettings.LanguageCode`, which lists the codes.

## Why these and what goes wrong

- `LocalizationTests` checks: every language has exactly the English keys; no file declares a key twice;
  placeholders match English and every placeholder string is a valid format in every language; filter
  strings keep their patterns; every referenced key exists; every runtime family is complete in all four
  files; no dynamic call is unguarded; and the dictionaries are embedded in the built App assembly (that last
  check is skipped when the App has not been built).
- "Referenced" means a literal, `{loc:Tr Key}` in XAML or `Loc.Get("Key")` / `Loc.Format("Key"` in C#, plus
  the two-literal choice and the families above. Nothing else is seen.
- Without `WithCulture="false"` MSBuild reads `.de` or `.en` in the file name as a culture and moves the
  dictionary into a satellite assembly. The main assembly then has no strings and every label shows its key.
  That shipped once.
- `Loc.Get` before `Loc.Initialize` returns the key. Startup (`App.Start`) loads the settings, calls
  `Loc.Initialize`, and only then builds `SourceManager` and with it every service. `SourceManager` takes the
  settings as a constructor parameter so that order cannot be undone, and
  `tests/EQ2Parser.App.Tests/SourceManagerStartupTests.cs` pins it. So a string resolved in a service
  constructor is real text in the app.
- The one thing that can run before the dictionaries is a crash. `CrashLog` therefore keeps its dialog text in
  English in code as well (`CrashLog.DialogEnglish`) and falls back to it; a test holds the two copies equal.
  Do not copy that pattern for ordinary strings.
- In the App test assembly English is loaded for every test by
  `tests/EQ2Parser.App.Tests/LocTestHost.cs`, so a test that reads a status line does not depend on which
  test class ran first. A test of another language wraps its assertions in `LocTestHost.In("de", ...)`, which
  puts English back.
- A format string that does not parse in one language does not crash: `Loc.Format` shows the English instead.
  Only the valid-format test catches it.
- `{loc:Tr}` and static tables (`ColumnSets`, `ExportColumnDefs`, `NavItems`) resolve once. A language change
  applies on restart; do not build live switching around one string.
- A missing translation falls back to English, then to the key, so nothing crashes and nothing warns. Only
  the tests and your eyes catch it.
- Numbers and dates are formatted by the Windows culture, not by the interface language, so a German
  interface on an English Windows shows `1,234`.

## Tests

- `tests/EQ2Parser.Core.Tests/LocalizationTests.cs` for the dictionaries and the key guards. It reads the
  source tree, so it runs without building the App. Build the App first anyway so the embedding check runs
  against a fresh assembly.
- `tests/EQ2Parser.App.Tests/LocalizedTextTests.cs` for strings assembled in code: the English reads as
  intended, and no language shows a raw key.
- Then use the `parser-ui-check` skill (`-Language de`) to see the string rendered.
