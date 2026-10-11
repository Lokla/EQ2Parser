---
name: parser-triage
description: Triage an EQ2Parser bug report or crash by pinning the reporter's exact build, classifying the failure, reproducing it from the real log lines, fixing every call path at once and landing a regression test. Use when a tester or user reports an EQ2Parser bug, a crash, a freeze, wrong numbers after an update, "it stopped parsing", pastes a crash log or session log, or asks where the app stores its data or how to reset it. Use it before proposing a cause.
---

# Parser triage

## Which repo

This skill is for the EQ2Parser repository (`VortexUK/EQ2Parser`). It is often an additional
directory in a session rooted in another repo, and its `CLAUDE.md` is not loaded then, so:

- Run git as `git -C <EQ2Parser root>` and gh as `gh --repo VortexUK/EQ2Parser`. A bare `git`
  or `gh` acts on whichever repo the shell happens to be in.
- Before any tag or push, confirm `git -C <EQ2Parser root> remote get-url origin` names
  `VortexUK/EQ2Parser`.
- When a task spans this repo and EQ2Lexicon, say which repo each change lands in before starting.

## Why the order matters

Each step below exists because skipping it has already cost a release: a voice bug "fixed"
three releases running, one call path at a time; attendance and pet bugs that came back
because no test held the real lines; a real bug blamed on a stale build; a fix declared
working from reading code when the running app said otherwise. Work the steps in order and
say which one you are on.

References: `references/app-data.md` (every file the app keeps, resets, working on a copy)
and `references/crash-logs.md` (crash files, the session journal, the skipped-line and
stopped-source walk-through). Read the one the report calls for. Do not ask the user where
the files are.

## 1. Pin the facts

- **Build.** The first line of every `crashes\crash-*.txt` and of `session.log` reads
  `EQ2Parser <X.Y.Z>+<commit>`. With no log, ask for the version shown in Settings.
  `git -C <EQ2Parser root> merge-base --is-ancestor <fix commit> <their commit>` says whether
  their build has a given fix. Updates are checked only at launch and applied after exit
  (`UpdateService`), so the first launch after a release still runs the old build. Never
  suggest "an old build" without the version in hand; once the version has the fix, the bug
  is real.
- **What they did, and the evidence**: raw log lines (`(epoch)[local timestamp] message`), a
  screenshot, the crash file. Ask for the single smallest missing piece instead of guessing.
- Never ask for the API token, and do not ask for `settings.json` as routine
  (`references/app-data.md`, last section).

## 2. Classify

| The report | Usually | Read first |
| --- | --- | --- |
| Error dialog at launch ("could not start and has to close"), then the app is gone | An exception during startup. It is fatal: `App.FailStartup` writes a `fatal-startup` crash report, shows one dialog and exits with code 71. Builds up to v0.5.7 swallowed it instead and kept running with no window or half an app. An unreadable `history.db` is no longer a cause: the archive row below | `crash-*-fatal-startup.txt` (the newest `ui-thread` file on those older builds), last startup line of `session.log`, `src/EQ2Parser.App/App.xaml.cs` |
| "hit the same error repeatedly and has to close" | Five UI exceptions in 60 s: a tick or a template throwing every pass | `crash-*-crash-loop.txt`, `src/EQ2Parser.App/Services/CrashLog.cs` |
| Vanished with no dialog | Native crash, memory, or killed from outside. A Windows crash dump exists only if the machine has local dumps enabled; a missing one proves nothing | `crashes\postmortem-*.txt` (it names any dump it found), `references/crash-logs.md` |
| Froze | UI thread blocked | Heartbeat gaps in `session.log` |
| "It stopped parsing", one character stopped updating, or numbers are short | A line that throws is skipped and the source reads on (the row gains `N skipped on error`). The source stops only when 200 lines in a row fail or the log reader itself fails (the row reads `error: ...`, worded in the interface language from `LogSource.Stopped`); it ends its fight in progress as it stops, so that fight is saved, cut short. Each case leaves a crash report. Builds up to v0.5.7 ended the tail loop on the first throw and wrote no file | The Sources page row, `crash-*-log-line.txt`, `crash-*-log-source-stopped.txt` or `crash-*-log-reader.txt`, the `log line skipped` and `source STOPPED` lines of `session.log`, then the walk-through in `references/crash-logs.md`; `src/EQ2Parser.App/Services/LogSource.cs`, `src/EQ2Parser.Core/Engine/LogLineProcessor.cs` |
| Numbers are right but a trigger, timer, callout or attendance update did not happen | A handler on the alert side of a line threw. The processor contains it: the line is still parsed, nothing is skipped, the source never stops for it. The Sources row ends `alert errors: N (last: <type>) — numbers unaffected, see the crashes folder` | The Sources page row, `crash-*-log-alert.txt` (names the stage and quotes the line), the `alert handler FAILED` lines of `session.log`, `LogSource.AlertFaults`; `src/EQ2Parser.Core/Engine/LogLineProcessor.cs` |
| The history is empty after a start, old fights are gone, or tonight's fights are missing from the archive | `history.db` could not be opened, or saves are failing. A damaged file is renamed `history.db.corrupt-<timestamp>` and a fresh archive started (nothing deleted). A file that is merely out of reach (locked, access denied, disk full) is left alone and the session saves nothing. A failed save loses that fight from the archive only. Each of these shows a warning strip under the tab bar for as long as it holds, in the interface language, and the Archive window says why it has nothing to search; builds without the strip showed nothing at all | A screenshot of the strip, `crash-*-archive-*.txt`, the `archive ...` lines of `session.log`, a `history.db.corrupt-` file in the data folder, `HistoryService.State`; "The archive" in `references/app-data.md`; `src/EQ2Parser.App/Services/HistoryService.cs`, `src/EQ2Parser.App/ViewModels/ArchiveNotice.cs`, `src/EQ2Parser.Core/History/HistoryStore.cs` |
| The last pull of the night is missing, or is there but cut short | The app was closed, or the log removed from Sources, in the middle of a fight. The current build ends that fight as the source goes away, so it is archived and queued for upload, cut short at its last line. After an exit the upload itself may not have finished before the process ended; the fight can then be uploaded from the fight tree after the next start. Builds without the journal lines named here lost the fight | `exit: fight in progress ended` or `source removed: ...; its fight in progress was ended` in `session.log`, and `archive: exit did not wait for ...` if the save did not make it; "A fight in progress when its source goes away" in `references/crash-logs.md`; `src/EQ2Parser.App/Services/SourceManager.cs` |
| The Main page refreshes only every couple of seconds and the mini-parse overlays read "Importing history…", long after any import | On builds before the fix, a source that had STOPPED kept counting its unread log as import backlog, so once that log was a megabyte ahead the whole UI stayed in import mode until restart. Otherwise it is a real backlog: a parse-from-start still running | The Sources page row (`error: ...` on one of them), `source STOPPED` in `session.log`; `LogSource.PendingBytes` and `SourceManager.ImportBusy` in `src/EQ2Parser.App/Services/SourceManager.cs` |
| Wrong number, missing hits | A line shape the grammar missed or misread, or a stat definition | `src/EQ2Parser.Core/Grammar/EnglishGrammar.cs`, `docs/engine-behaviour.md` |
| Wrong attribution: pet as player, wrong class, attendance false positive | Classifier or roster rules | `src/EQ2Parser.Core/Analysis/CombatantClassifier.cs`, `src/EQ2Parser.Core/Raid/RaidRosterTracker.cs` |
| Fights split or merged wrongly | Idle rule or correlator | `src/EQ2Parser.Core/Engine/ParserEngine.cs`, `src/EQ2Parser.Core/Correlation/EncounterCorrelator.cs` |
| Trigger, timer or voice silent or wrong | Live-only gate, zone scope, audio path | `src/EQ2Parser.Core/Triggers/TriggerEngine.cs`, `src/EQ2Parser.App/Services/AlertAudioService.cs` |
| Upload refused or missing | The status line on the Settings page says why | `src/EQ2Parser.App/Services/UploadService.cs`, `src/EQ2Parser.Core/Upload/UploadQueue.cs`; a server refusal is EQ2Lexicon's ingest |
| Settings, triggers or timers reset themselves | The file failed to load and was quarantined (the archive has its own quarantine: the row above) | A `.corrupt-` sibling file, `src/EQ2Parser.Core/Persistence/PersistedJsonFile.cs` |
| Looks wrong on screen | View or view model | The sibling `parser-ui-check` skill |

"Wrong after an update" has two builds in it: get both versions, then
`git -C <EQ2Parser root> log --oneline v<old>..v<new> -- src` lists what changed between them.

## 3. Reproduce before proposing a cause

- **Parsing, attribution, attendance, triggers:** write the failing test first, from the
  reporter's lines (step 5 says where). Lines fed in a test carry no arrival stamp, which
  `LogLineProcessor` treats as live, so triggers, timers and raid lines all fire there.
- **Only visible in the running app:** run it on scratch data, never the real folder. The
  sibling `parser-ui-check` skill does this safely in one command and hands back a
  screenshot. Before launching by hand with `EQ2PARSER_DATA_DIR`, read "Working on a copy" in
  `references/app-data.md`: a copied `settings.json` carries a working upload token and the
  real log paths, so the run would upload to the live site and rewrite the raid command files.
- **Replay is not live.** A line older than `LogLineProcessor.TriggerFreshness` (30 s) builds
  fights but fires no trigger, timer, callout or attendance. Reproduce those in tests.
- If it does not reproduce, say so and ask for more evidence. An unreproduced guess offered
  as the cause is how the wrong thing gets fixed.
- A fix is working when the test that failed now passes and, for anything a user sees, when
  you have looked at the running app (`parser-ui-check`). Reading code finds a fix; it does
  not confirm one.

## 4. List every call path before fixing

Grep for every caller of the faulty member and every sibling implementation, and list them
in your reply before editing. Speech reaches `AlertAudioService.Speak` from `TriggerService`,
`TimerService`, the callout handler in `SourceManager` and the sound preview in
`TriggersViewModel`, with `SpeakTest` beside it for Settings; a voice fix that covers one of
them is how a bug gets "fixed" three times. Player-versus-pet filtering has the same shape:
`CombatantClassifier.PlayerAllyNames` feeds both attendance and the raid-only upload count.
Fix at the shared point when there is one, otherwise fix all the paths in one change.

## 5. Land the fix with a regression test built from the real lines

| Bug | Test goes in `tests/EQ2Parser.Core.Tests/` | Pattern to copy |
| --- | --- | --- |
| One line parsed wrongly or not at all | `EnglishGrammarTests.cs` | `EnglishGrammar.TryParse` on the message part only |
| A new line shape | the above, plus a line in `tests/logs/eq2log_Menludiir.txt` | Update the counts in `GoldenLogTests.Committed_Corpus_Full_Pipeline` |
| A line that threw (skipped with a `log-line` report; up to v0.5.7 it killed the tail loop) | `EnglishGrammarTests.cs`, or `EnglishGrammarTotalityTests.cs` for an amount | `Suffixless_Decimal_Amount_Parses_Instead_Of_Throwing`, `Unrepresentable_Amount_Is_A_Non_Match_On_Every_Shape` |
| An alert handler that threw on a line (a `log-alert` report) | The handler's own test file; `AlertStageContainmentTests.cs` holds the containment itself | Feed the quoted line through `LogLineProcessor.Process` with the real handler subscribed |
| The archive: a file that will not open, a failed save, a schema step | `HistoryStoreTests.cs`, and `tests/EQ2Parser.App.Tests/HistoryServiceResilienceTests.cs` for what the app does about it; `tests/EQ2Parser.App.Tests/ArchiveNoticeTests.cs` for what it shows | `A_File_Of_Garbage_Is_Quarantined_And_A_Fresh_Archive_Works`, `Swings_Table_From_Before_The_Arrival_Stamp_Gains_The_Column`, `A_Search_That_Errors_Says_So_And_Keeps_The_Last_Results` |
| A fight lost or cut when the app closed or a log was removed | `tests/EQ2Parser.App.Tests/SourceManagerStartupTests.cs` | `Closing_The_App_Mid_Fight_Leaves_The_Fight_In_The_Archive_File`, `A_Source_Removed_Mid_Fight_Has_Its_Fight_Ended_Announced_And_Archived` |
| Several lines: deaths, owner resolution, fight boundaries | `DeathAttributionTests.cs`, `ParserEngineTests.cs` | The `Feed(processor, epoch, message)` helper |
| Pet, player or class | `CombatantClassifierTests.cs` | `Pet_Name_Shapes` rows, or `Classify` on a built fight |
| Attendance, `/who`, roster, DKP files | `RaidTrackingTests.cs` | `RaidRosterTracker.OnLine`, `WhoParser.Feed` |
| Triggers, timers, merging, upload | `TriggerEngineTests.cs`, `SpellTimerServiceTests.cs`, `EncounterCorrelatorTests.cs`, `UploadQueueTests.cs` | Nearest existing test |
| Settings lost across versions | `tests/EQ2Parser.App.Tests/AppSettingsCompatTests.cs` | Write the old JSON, load, assert |

Replace character and guild names with invented ones before the lines reach a test, a commit
message or an issue: the repo is public. Keep each name's shape (word count, length,
capitals, possessive), because pet detection and the roster filters key on shape, and check
the renamed test still fails before the fix. Then run the sibling `parser-verify` skill; a
fix that has to reach testers goes out through the sibling `parser-release` skill.

## 6. Resetting data is destructive

The data folder holds things that cannot be rebuilt (own triggers, the fight archive, the
DKP ledger), and on an installed machine it is also the program's folder. Before removing
anything from a real one, state exactly which files go and what is lost with each, and wait
for a yes. Prefer a rename, or working on a copy. `references/app-data.md` has the per-file
consequences.
