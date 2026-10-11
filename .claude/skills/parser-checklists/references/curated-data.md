# Add a curated-data entry

Applies when an entry is added to `source_overrides.json` (where an ability really comes from) or
`scripted_wins.json` (bosses that end by a say line, not a death), and when the class list changes. Every
entry is a claim about the game. Check it against a log or the sibling repo's `census-query` skill before
writing it; a remembered fact about a class or a pet has been wrong here before.

## Checklist: source override

1. `src/EQ2Parser.Core/Resources/source_overrides.json`: add an object to `overrides`.
   - `ability`: the base name. `SpellClassMap.Normalize` trims it, lower-cases it and strips a trailing roman
     numeral, so one entry covers every rank.
   - `classes`: the detected classes the rule applies to. Omit it for a rule that fits any class.
   - `source`: an `AbilitySource` name: `class`, `raid`, `item`, `system` or `pet`.
   - `note`: the evidence, in words. Notes are public; do not name players.
   - `grantedBy`: the classes that grant it, for raid-buff attribution. Valid without `source`.
   - `pet`: true for a summoner pet's kit, so the same ability on any other detected class is tagged as a
     renamed pet. It only takes effect together with `classes`.
2. `tests/EQ2Parser.Core.Tests/SourceOverridesTests.cs`: assert the new entry in
   `Embedded_Overrides_Fix_The_Known_Mislabels` (or the pet test), for a class it fits and one it does not.

## Checklist: scripted win

1. Take the exact NPC say line from a log where the fight was won.
2. `src/EQ2Parser.Core/Resources/scripted_wins.json`: add an object to `rules` with `speakers` (every name the
   NPC speaks under, including a renamed final phase), `phrase` (a substring of the quoted text, copied
   exactly) and `note`.
3. `tests/EQ2Parser.Core.Tests/ScriptedWinsTests.cs`: add the line and a near-miss to
   `Matches_Only_The_Curated_Lines`.

## Class list changes

`ByClass` in `src/EQ2Parser.Core/Analysis/Archetype.cs` and `ByClass` in
`src/EQ2Parser.App/Services/ClassColors.cs` are parallel dictionaries: change both.
`src/EQ2Parser.Core/Analysis/MeleeCapabilities.cs` changes only for a weapon rule that is certain, because it
accuses a player of padding. `src/EQ2Parser.Core/Resources/spell_classes.json` is generated: run
`EQ2Lexicon/scripts/dev/build_spell_classes.py` with `--out` pointing at that file, and put corrections in
`source_overrides.json`, never in the generated file. The `Arch_*` strings are per archetype, not per class;
they change only with `Archetype.All` (`src/EQ2Parser.App/Views/ExportWindow.xaml.cs` hard-codes its count).

## Why these and what goes wrong

- The first matching rule wins, in file order. A class-scoped rule matches only when a class was detected, so
  put the specific rule before a general one for the same ability.
- Mistakes are dropped without a message: an entry with an empty `ability` or an unknown `source` word
  produces no rule, and `pet` without `classes` is ignored. Class names compare without case but must be
  spelled as the map spells them (`Shadowknight`, `Troubador`, `Conjuror`); a misspelling never matches.
- A syntax error in `source_overrides.json` throws from `SourceOverrides.LoadEmbedded` while `SourceManager`
  is built, so the app fails to start. In `scripted_wins.json` it is swallowed and every scripted win quietly
  stops working. The tests load the embedded files, so run them after any edit.
- A user can hot-fix overrides without a release: `SourceManager` merges a `source_overrides.json` from the
  app data directory over the embedded set, and its rules are tried first. Promote a proven fix from there.
- A scripted-win match is case-sensitive, needs an NPC say line and an exact speaker, and is only consulted
  while a fight is active. A player quoting the phrase does not count.
- A new curated file needs its own `EmbeddedResource` line in `src/EQ2Parser.Core/EQ2Parser.Core.csproj`.

## Tests

- `tests/EQ2Parser.Core.Tests/SourceOverridesTests.cs` and `tests/EQ2Parser.Core.Tests/ScriptedWinsTests.cs`.
- `tests/EQ2Parser.Core.Tests/ArchetypeTests.cs` for a class list change.
