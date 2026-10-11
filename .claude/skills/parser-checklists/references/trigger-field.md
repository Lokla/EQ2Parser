# Add a field to a trigger or a spell timer

Applies when `Trigger` or `TimerDefinition` gains a property. ACT's trigger XML share format is a compatibility
promise: an existing attribute never changes meaning, and a field of our own is either an extra optional
attribute that ACT ignores (like `Z`) or is left out of the XML on purpose (like `AudioCooldown`).

## Checklist: trigger

1. `src/EQ2Parser.Core/Triggers/Trigger.cs`: an `init` property with its default and a line in the private copy
   constructor `Trigger(Trigger source)`. Its behaviour goes in `src/EQ2Parser.Core/Triggers/TriggerEngine.cs`.
2. `src/EQ2Parser.Core/Triggers/ActShareFormat.cs`, if it is in the XML: read it in `ImportTrigger` with
   `Attr(shortName, longName)`, write it in `Export(Trigger)`, and list it in `docs/engine-behaviour.md` section 4.
3. `src/EQ2Parser.App/Services/TriggerService.cs`: append a parameter with a default to the `TriggerSetting`
   record, and map it in both `Load` and `Save`.
4. `src/EQ2Parser.App/Services/LexiconSyncService.cs`: a parameter on `PackTrigger` and a line in `Apply`.
5. `src/EQ2Parser.App/ViewModels/TriggersViewModel.cs`: an editor `[ObservableProperty]`, then `NewTrigger`
   (reset), `EditRow` (load), `SaveTrigger` (build) and `MoveTrigger` (copy). Add the control to
   `src/EQ2Parser.App/Views/TriggersView.xaml` and its strings to all four dictionaries.

## Checklist: spell timer

1. `src/EQ2Parser.Core/Triggers/TimerDefinition.cs`: an `init` property with its default, and the behaviour
   that uses it in `src/EQ2Parser.Core/Triggers/SpellTimerService.cs`.
2. `ActShareFormat`, if it is in the XML: `ImportSpell` and `Export(TimerDefinition)`, and the doc as above.
3. `LexiconSyncService`: a parameter on `PackTimer` and a line in `Apply`.
4. `src/EQ2Parser.App/ViewModels/TimersViewModel.cs`: an editor property, then `NewTimer`, `EditRow` and
   `SaveTimer`. Add the control to `src/EQ2Parser.App/Views/TimersView.xaml` and its strings.
5. If a running bar shows it: `TimerBarSnapshot` and `TimerService.Snapshot` in
   `src/EQ2Parser.App/Services/TimerService.cs`, then `TimersViewModel.ApplyBar`.
6. `CurationViewModel.BuildDefinition`, if mined data can fill the field.

A field that curators set also has a site half. In the sibling repo the pack fields are declared in
`EQ2Lexicon/backend/server/api/act/_shared.py` (`TriggerEntry`, `SpellTimerEntry` and the two `_row_to_entry`
helpers) and selected by `_ACT_TRIGGER_COLS` / `_ACT_SPELL_TIMER_COLS` in `EQ2Lexicon/backend/eq2db/raids.py`,
with a migration for the column; `EQ2Lexicon/backend/server/api/act/pack.py` only assembles those entries.
Say which repo each change lands in, and use the `parser-contract` skill.

## Why these and what goes wrong

- `Trigger` is a class with a hand-written copy constructor, used by `WithEnabled` on every enable flip, and
  `TriggersViewModel.MoveTrigger` (drag and drop) is a second hand-written copy. A field missing from either
  is lost at that moment. `TimerDefinition` is a record, so `with` copies it, as `MoveTimer` does.
- Both editors rebuild the object from editor fields in `SaveTrigger` / `SaveTimer`. A field missing there
  resets to its default every time the user edits that row.
- `timers.json` is the `TimerDefinition` record serialized directly, so a new property persists by itself and
  a renamed one silently loses saved values. `triggers.json` goes through `TriggerSetting`, hence step 3.
  Lexicon rows are written to neither file, so a curated value arrives only through the pack mapping.
- `ActConfigImport` and `ChatTriggerShare` reuse `ActShareFormat.TryImport`; `LexiconMerge` is generic. No edits.
- The pack records bind by name in snake_case (`TimerDurationS` is `timer_duration_s`); a name that does not
  match the server's field binds nothing and reports nothing. A field absent from the JSON (the cached pack
  applied at startup, or a site that has not deployed yet) takes the record parameter's own default (`false`,
  `0`, `null`), not the Core default. Where those differ (`Modable` defaults to true), make the pack parameter
  nullable and coalesce in `Apply`.

## Tests

- `tests/EQ2Parser.Core.Tests/ActShareFormatTests.cs`: a round-trip under the short and the long attribute name.
- `tests/EQ2Parser.Core.Tests/LexiconMergeTests.cs`: `TriggerWithEnabledTests` compares every `Trigger` field.
- `tests/EQ2Parser.App.Tests/LexiconSyncServiceTests.cs`: extend the canned pack, with and without the field.
- `tests/EQ2Parser.Core.Tests/TriggerEngineTests.cs` or `tests/EQ2Parser.Core.Tests/SpellTimerServiceTests.cs`.
