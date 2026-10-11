---
name: parser-checklists
description: Checklists for the multi-file changes in EQ2Parser, where registries, parallel lists and switch statements have to change in step. Use it before starting any such change in EQ2Parser, so no fixed registration point is missed - adding a setting, a UI string or language, a grid or export column, an overlay, a top-level page, a trigger or timer field, a history.db schema change, a swing category, or a curated-data entry (source_overrides.json, scripted_wins.json).
---

# EQ2Parser change checklists

## Which repo

This skill is for the EQ2Parser repository (`VortexUK/EQ2Parser`). It is often an additional
directory in a session rooted in another repo, and its `CLAUDE.md` is not loaded then, so:

- Run git as `git -C <EQ2Parser root>` and gh as `gh --repo VortexUK/EQ2Parser`. A bare `git`
  or `gh` acts on whichever repo the shell happens to be in.
- Before any tag or push, confirm `git -C <EQ2Parser root> remote get-url origin` names
  `VortexUK/EQ2Parser`.
- When a task spans this repo and EQ2Lexicon, say which repo each change lands in before starting.

## Pick the checklist

Most features here touch several files that the compiler does not link: a persisted record, a
view-model, a XAML view, four language dictionaries, a switch with a default arm. Find the change
in the table and read that one reference file before editing. A task that is two of these (a new
overlay with its own setting and strings) reads each file it needs.

| Change | Read |
|---|---|
| Add, rename, retype or remove a persisted setting | `references/setting.md` |
| Add or change a UI string; add a language | `references/locale-string.md` |
| Add a column to the encounter, drill or swing grid, or to the Discord export | `references/grid-column.md` |
| Add an overlay window, or a per-overlay option | `references/overlay.md` |
| Add a top-level page (a tab in the shell) | `references/page.md` |
| Add a field to a trigger or a spell timer | `references/trigger-field.md` |
| Change the `history.db` schema | `references/history-schema.md` |
| Add a swing category or a stat bucket, or change which bucket a category feeds | `references/swing-category.md` |
| Add an entry to `source_overrides.json` or `scripted_wins.json`; change the class list | `references/curated-data.md` |

Paths in the reference files are relative to the EQ2Parser root. A path that starts with
`EQ2Lexicon/` is in the sibling repo, checked out beside this one.

## Rules that hold for every checklist

- **Persisted names are contracts.** Settings property names, column keys, the properties of
  `TriggerSetting` and `TimerDefinition`, SQLite columns and enum numbers are all on a user's disk
  or on the site. The JSON loaders ignore unknown fields, so a rename silently resets the user's
  value; a type change fails to deserialize and the whole file is quarantined.
- **Grep for the neighbour.** When you add `X` beside an existing `Y`, search `src/` and `tests/`
  for `Y` and match every hit. The reference files list the places that existed when they were
  written; the grep is what proves the list is still complete.
- **Do not trust default arms.** Several switches end in `_ =>`, so a missing case compiles and
  runs as some other case. Add an explicit arm to every switch over the type you extended.
- **Every user-visible string goes through `Loc`**, with the key in all four dictionaries
  (`references/locale-string.md`).
- **Core stays UI-free.** `src/EQ2Parser.Core` targets plain .NET and cannot reference WPF; put
  testable logic there and keep `src/EQ2Parser.App` a thin shell. Inside Core, `Combat` is the
  bottom layer and references nothing above it (`LayeringTests`).
- **Numbers stay stable.** Stat definitions that feed visible numbers (EncDPS, durations, the ally
  graph, success level, the idle rule) are compared across uploads on the site. A change that
  moves them is a decision for the user, recorded in `docs/engine-behaviour.md`, not a side effect.
- **Game facts come from data.** Which class has an ability, what a pet is called, what a boss
  says: check a log or the sibling repo's `census-query` skill. Recall has been confidently wrong
  here before.
- **This repo is public.** No machine paths, account names, tokens or real player names in code,
  notes, tests or fixtures.

## Afterwards

- Run the `parser-verify` skill after any checklist, before saying the work is done.
- For anything a user can see (a control, a column, an overlay, a page, a string), look at it with
  the `parser-ui-check` skill. Most of the registration points above have no automated test, so
  looking is the only check that the new thing is wired to the right place.
- If the change touches the upload payload, or a list mirrored between this repo and EQ2Lexicon
  (swing types, bucket names, the trigger pack fields, vocabularies), also use the
  `parser-contract` skill.
