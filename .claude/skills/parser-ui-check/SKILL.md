---
name: parser-ui-check
description: Launch EQ2Parser on throwaway data, read the text it actually rendered and save a screenshot of each page that you can open and look at. Use after any EQ2Parser change that shows on screen (XAML, view-models, strings, settings, columns, overlays), before asking the user for a screenshot or to restart the app, to smoke-test a published build before a release, and whenever you are about to say a UI change works without having seen it.
---

# UI check (EQ2Parser)

## Which repo

This skill is for the EQ2Parser repository (`VortexUK/EQ2Parser`). It is often an additional
directory in a session rooted in another repo, and its `CLAUDE.md` is not loaded then, so:

- Run git as `git -C <EQ2Parser root>` and gh as `gh --repo VortexUK/EQ2Parser`. A bare `git`
  or `gh` acts on whichever repo the shell happens to be in.
- Before any tag or push, confirm `git -C <EQ2Parser root> remote get-url origin` names
  `VortexUK/EQ2Parser`.
- When a task spans this repo and EQ2Lexicon, say which repo each change lands in before starting.

## Why this exists

Tests cannot see a WPF window. Twice a release went out broken in ways only the screen showed:
once every label rendered as its raw localization key, once the main view threw at first
render. In both cases the check beforehand was "the process is still alive". This script
checks what was rendered, and gives you a picture, so a UI change can be looked at in the same
turn it was made instead of waiting for the user to restart the app and send a screenshot.

## The command

```powershell
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/ui-check.ps1                       # build, launch, check the Main page
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/ui-check.ps1 -AllPages             # walk every top tab
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/ui-check.ps1 -Page Settings,Timers
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/ui-check.ps1 -Log <EQ2Parser root>/tests/logs/eq2log_Menludiir.txt
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/ui-check.ps1 -Language de -AllPages
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/ui-check.ps1 -Page Settings -Expect "Only upload raid fights"
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/ui-check.ps1 -Exe <path to EQ2Parser.App.exe> -AllPages
```

Pages are `Main`, `Sources`, `Triggers`, `Timers`, `Overlays`, `Settings`. About 10 seconds
for one page, 20 for all six.

Without `-Exe` the script builds `src/EQ2Parser.App` (Debug) under `artifacts/verify` first,
so it always shows the current working tree, and never collides with a copy of the app
running from `bin/`.

For each page it writes two files under `artifacts/ui-check/` (git-ignored; `-Out` changes it):

- `<Page>.png`: the window. **Open it with the Read tool and look at it.** A PASS line means
  the automated checks passed, not that the layout is right.
- `<Page>.txt`: every UI Automation name on the page as `ControlType<TAB>Name`, including
  content scrolled out of view. Grep it to confirm a label, a value or a row is present.

## What it checks

- **No unresolved keys.** `Loc.Get` falls back to the key itself, so a missing or unembedded
  string shows as `Settings_Something`. Any element whose name is exactly a key from
  `src/EQ2Parser.App/Localization/strings.en.json` fails the page.
- **The tab strip is there**, in the chosen language. An empty or half-built window fails.
- **`-Expect "text"`** (several as `"one|two"`): each text must appear in some element on the pages
  visited. Use it to assert the thing you just added.
- **The app survived** and wrote no crash report into the scratch data folder. A new crash
  report fails the run and its first lines are printed.

Verdict line: `ui-check: OK` (exit 0), `ui-check: FAIL` (exit 1), `ui-check: INCOMPLETE`
(exit 2: bad arguments, nothing was checked).

## Getting data on screen

A fresh scratch instance has no fights. `-Log <file>` adds a log as a source and parses it
from the first line; the committed corpus `tests/logs/eq2log_Menludiir.txt` gives two fights,
a chart and a combatant grid. Prefer `-Log` on each run over reusing a data folder from an
earlier run: on a relaunch the same fights come back from the history archive only if they
are recent enough, and the corpus is not, so the page comes up empty.

The Main page's fight list groups by day and only today's section starts expanded, so a log
from another day shows one collapsed header and no fights to look at or select. Add `-Today`:
it shifts every timestamp of the copied log so the last line is a minute old, leaving the
spacing between lines (which is what fights are made of) alone.

`-Select "text"` selects list items by text they contain: the first match is selected and each
further one is added to the selection, as Ctrl+click would. Use it for the fight list
(`-Select "Boss A|Boss B"` gives the combined view, `-Select "All Bosses"` a rollup) and for
any other list. A text with no selectable item fails the page.

Beyond tabs and list selection there is no click automation: no buttons, checkboxes, context
menus or typing. To look at a state that needs those (a setting switched on, an overlay
visible), write the state into a prepared data folder and pass `-DataDir`, or check the page
that shows it and read the `.txt`. If the thing can only be reached by clicking through, say
so and ask the user to look; do not claim it was checked.

## Safety, and why the script is shaped this way

The app is launched with `EQ2PARSER_DATA_DIR` set to `artifacts/ui-check/data`, rebuilt each
run, so the real settings, history and upload token are never touched. Four things in the
app make a careless second instance harmful, and the script handles each. They are the reason
to use the script instead of launching the app by hand with the variable set:

- **It writes into the game folder.** The app treats the folder above `logs/` as the EQ2
  install and writes its raid command files (`eq2lexicon-raid-*.txt`) there. Pointing a
  scratch instance at a real log would overwrite the live ones. `-Log` therefore copies the
  file into `artifacts/ui-check/game/logs/` first. Do not work around the copy.
- **A copied data folder is still armed.** A copy of the real data folder names the real
  logs, and its stored token still decrypts for the same Windows user, so it would tail live
  logs and upload duplicates to the site. With `-DataDir` the script clears `Sources` and
  `WatchedFolders` and turns `UploadEnabled` off in that copy, and refuses the real data
  folder outright. `-KeepSources` skips the disarming; use it only on a folder you built.
- **It talks to the live site.** At startup the app fetches the public trigger library, which
  is what fills the Triggers and Timers pages. `-Offline` turns that off (those pages are then
  nearly empty).
- **It rewrites a registry value.** Every launch points the per-user Windows crash-dump
  folder for the app at its data folder (`SessionJournal.RegisterCrashDumps`), so a scratch
  run would leave it aimed at the scratch folder. The script saves the value and puts it back.

Only the process the script started is closed. A copy of the app the developer has open is a
live parsing session: never stop it. The window is on screen for a few seconds and can take
focus briefly; mention that if the user may be in game.

## Limits

- The screenshot is the window at its default size; long pages (Settings) are cut off in the
  picture but complete in the `.txt`.
- Overlay windows, dialogs, context menus and the first-run wizard are not visited.
- Audio, text-to-speech and anything timing-dependent are not checked.
- A published build passed with `-Exe` is still not the installed, Velopack-updated app.

Say which pages were checked and which were not.
