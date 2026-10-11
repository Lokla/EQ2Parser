# Crash logs and the session journal: what they hold and how to read them

Everything here lives under the data folder (`%LOCALAPPDATA%\EQ2Parser` unless
`EQ2PARSER_DATA_DIR` is set; see `app-data.md`). The crash files are in its `crashes`
subfolder. The bug-report template (`.github/ISSUE_TEMPLATE/bug_report.yml`) points testers
at the parent folder, so when someone "can't find the crash log", send them to `crashes`.
The error dialog also prints the full path of the file it just wrote.

## The version line

The first line of every crash file and of `session.log` carries the build:

```text
EQ2Parser 0.5.7+<40-hex commit> — ui-thread exception at 2026-01-31 21:04:05
```

The version is the assembly's informational version: the release number, a `+`, and the
commit the build was made from. That answers "is the reporter on the build with the fix"
without guessing:

```text
git -C <EQ2Parser root> merge-base --is-ancestor <fix commit> <their commit>   # exit 0 = fix is in their build
git -C <EQ2Parser root> tag --contains <fix commit>                           # releases that have the fix
```

A version of `1.0.0+...` is a from-source build (no version is pinned in
`Directory.Build.props`; releases inject it at publish time). Settings shows the same
release number as `EQ2Parser v...` for a tester who has no log to hand.

## Crash files: `crashes\crash-<yyyy-MM-dd_HHmmss>-<kind>.txt`

Written by `CrashLog` (`src/EQ2Parser.App/Services/CrashLog.cs`), installed as the first
statement of `App.OnStartup`.

| Kind | Raised by | What the user saw | Process afterwards |
| --- | --- | --- | --- |
| `ui-thread` | An exception on the UI thread | "EQ2Parser hit an unexpected error and kept running." | Keeps running; the exception is swallowed |
| `crash-loop` | The fifth UI-thread exception inside 60 seconds | "EQ2Parser hit the same error repeatedly and has to close." | Exits with code 70 |
| `fatal` | An unhandled exception on any other thread | "EQ2Parser hit a fatal error and has to close." | Dies |
| `fatal-startup` | An exception during startup: inside `App.Start` (run by `App.OnStartup`), or while `Program.Main` builds the `App` | "EQ2Parser could not start and has to close." | Exits with code 71 (`App.StartupFailureExitCode`) |
| `background-task` | A faulted task nobody awaited | Nothing | Keeps running |
| `log-line` | Processing one log line threw (`LogSource.PumpLine`) | Nothing; the Sources row gains `N skipped on error` | Keeps running; the line is skipped and the source reads on |
| `log-alert` | An alert handler threw while a line was processed: triggers, trigger share, raid line, status callout or spell timers (`LogLineProcessor.AlertFaulted`, reported by `LogSource`) | Nothing. An alert may not have fired | Keeps running; the line was still parsed, nothing is skipped and the source cannot stop for it |
| `log-source-stopped` | 200 lines in a row failed for one source (`LineFaultBreaker.DefaultTripAfter`) | Nothing; the Sources row reads `error: stopped after 200 lines in a row failed to process ...` | Keeps running; that source has stopped, after ending its fight in progress (announced like any finished fight) |
| `log-reader` | The tail reader itself failed, for example an IO error mid-read | Nothing; the Sources row reads `error: <message>` | Keeps running; that source has stopped, after ending its fight in progress (announced like any finished fight) |
| `fight-consumer` | One consumer of a finished fight threw (`SourceManager.Isolated`: correlator, archive save, upload, raid roster, archive collapse). Also written by `LogSource` when a consumer throws on the fight a stopping source ends | Nothing | Keeps running; the other consumers still ran |
| `archive-quarantined` | `history.db` was damaged at startup and was renamed aside (`HistoryService`) | Nothing yet (see the note below the table) | Keeps running on a fresh, empty archive; nothing was deleted |
| `archive-unavailable` | `history.db` could not be opened at startup and was left alone | Nothing yet | Keeps running WITHOUT an archive; no fight of the session is saved |
| `archive-restore` | The startup restore could not list the recent fights, or could not load some of them | Nothing; those fights are missing from the tree | Keeps running; the other fights were restored |
| `archive-save` | Saving a finished fight to the archive failed | Nothing | Keeps running; that fight is not in the archive |
| `archive-backup` | The exit snapshot of the archive failed | Nothing | Exits normally; `history.db` and the earlier backups are untouched |

All from `log-line` down are failures the app caught where they happened and reported through
`CrashLog.Report`. Builds up to v0.5.7 have none of them, nor `fatal-startup`. The five
`archive-` kinds and `log-alert` are newer still: builds without them fail startup on an
unopenable archive, swallow a failed save without a word, and skip the whole line when an
alert handler throws. The two startup `archive-` kinds have no on-screen notice yet;
`HistoryService.Problem` holds the text for one. `references/app-data.md` has the archive
states.

Layout of each file:

1. `EQ2Parser <version> — <kind> exception at <local time>`
2. `<Windows version> · .NET <runtime version>`
3. A blank line. For a caught kind, a context block and another blank line: the log file
   name for the four `log-` kinds, plus the skipped-line count and the failing line itself
   (its first 300 characters) for `log-line` and `log-source-stopped`; the stage, the fault
   count and the line for `log-alert`, under a sentence saying no combat data is missing; the
   consumer name for `fight-consumer`; for the `archive-` kinds what happened to the file in
   plain words (the quarantined file's name, "left exactly as it is"), and for `archive-save`
   the fight's title and zone.
4. `Exception.ToString()`: type, message, inner exceptions, stack trace.

What a crash file does NOT contain: what the user was doing or any setting. A `log-line`,
`log-source-stopped` or `log-alert` report DOES quote one log line, which can be another
player's chat; the report says so and tells the user to delete the line before sharing if
they prefer. Other kinds hold a log line only if the exception message quotes one. Nothing
is redacted, so file paths in a stack trace or message can show the Windows user name and
character names. By design the API token never reaches a log string (`SECURITY.md`).

Reading traps:

- **One file per five seconds.** Reports closer together than that are dropped, except the
  ones that always land: `fatal`, `crash-loop`, `fatal-startup`, `log-source-stopped`,
  `log-reader`, `archive-quarantined`, `archive-unavailable`, `archive-restore`, and the
  `fight-consumer` report of a stopping source. A crash loop therefore usually leaves one
  `ui-thread` file and one `crash-loop` file a moment later, and the three exceptions between
  them are gone. If the two differ, there were at least two faults.
- **Five `log-line` reports per source per session** (`MaxLineReports` in `LogSource`).
  Later skips are still counted on the Sources row and in the journal, so the number of
  files is not the number of skipped lines.
- **Five `log-alert` reports per source per session**, counted separately, and **three
  `archive-save` reports per session**. A broken alert handler throws on every line it is
  shown and a full disk fails every save, so the files are a sample: `LogSource.AlertFaults`,
  `HistoryService.SaveFailures` and the journal hold the counts.
- **A second report of one kind in the same second** gets `-2`, `-3` and so on before
  `.txt`.
- **One dialog per thirty seconds.** A tester who "only saw one error" may have had many.
  Count the files.
- **Thirty files are kept.** Older `crash-*.txt` are deleted on the next write. Ask for the
  folder soon after the event.
- **`background-task` timestamps are late.** The report is written when the garbage
  collector notices the abandoned task, which can be minutes after the failure. Line the
  stack trace up with the feature, not the clock.
- **`crash-loop` means a repeating thrower**: something on the 100 ms UI tick
  (`MainViewModel.Tick`), an overlay tick, or a template that throws on every render pass.
  Start from the top stack frame.
- **A throw during startup is fatal.** `App.OnStartup` installs `CrashLog` and the journal,
  then runs the rest of startup (`App.Start`) inside one guard. A throw goes to
  `App.FailStartup`, which in this order writes the `fatal-startup` report, journals
  `startup FAILED (<exception type>) — exiting with code 71`, starts the update check (or
  keeps the one already running), shows one dialog, hides any window, waits up to 60 seconds
  for the update check and exits with code 71 without running `App.OnExit`. No process is
  left behind, the journal has no `clean-exit`, and the next launch's postmortem names the
  report. Because the update check still runs, an install whose startup fails every time can
  still receive the release that fixes it. A failure while `Program.Main` constructs the
  `App` takes the same exit.
- **On builds up to v0.5.7 a startup throw left a half-started app.** It was swallowed as a
  `ui-thread` report, so startup stopped at that statement: no window at all (and a process
  still running, holding the archive open), or a window with no overlays. The update check
  was the last thing startup did, so such an install also stopped updating itself: that
  tester needs the new installer by hand. (Read from that code. Confirm the symptom with the
  tester, for example a leftover process in Task Manager, before building on it.)

## `session.log`: the session journal

Written by `SessionJournal` (`src/EQ2Parser.App/Services/SessionJournal.cs`). It is
REWRITTEN at every start, so it holds one session only: the running one, or the last one if
the app is closed. Ask for it before the tester relaunches. After a relaunch the previous
session survives only if it ended badly (see the postmortem below).

Each line is `[yyyy-MM-dd HH:mm:ss.fff] <text>`, local time. What gets written:

| Line | Meaning |
| --- | --- |
| `session start — EQ2Parser <version> · <Windows version> · .NET <runtime>` | First line. The version, as above |
| `crash dumps ON — Windows local dumps are enabled on this machine; a native crash of <executable name> leaves <executable name>.<pid>.dmp in <folder>` | The machine has local dumps enabled (see "Crash dumps"); `<folder>` is where Windows would write one |
| `crash dumps OFF — Windows local dumps are not enabled on this machine ...` | It has not, so no dump can exist for this session. Neither line is written under a shared host such as `dotnet.exe`, or when the setting could not be read. Earlier builds wrote `crash dumps requested for <executable name>` instead, which says nothing about the machine |
| `archive QUARANTINED — history.db was damaged (<type and SQLite code>); renamed to <file>, a fresh archive started, nothing deleted` | The archive file was unreadable and was set aside (`app-data.md`, "The archive"). Written while `SourceManager` is constructed, so before the next line |
| `archive UNAVAILABLE — history.db ... (<type and SQLite code>); this session saves no fights` | The archive could not be opened and was left alone, or was damaged and could not be replaced. Same place |
| `settings + localization loaded` | Settings were loaded, the language dictionaries initialised, then `SourceManager` was constructed: triggers, timers and the archive file opened |
| `archive restore: <n> of <m> recent fights could not be loaded (<type>); history.db left as it is` / `archive restore FAILED: the recent fights could not be listed (<type>); ...` | The startup restore skipped fights it could not read, or could read none. Startup carries on either way |
| `history restored` | `SourceManager.RestoreHistory` finished (the archive's recent window is in the tree) |
| `sources restored + folder watch started` | Saved sources re-attached, watcher running |
| `startup complete — window shown, overlays restored` | The end of startup (`App.Start`) |
| `startup FAILED (<exception type>) — exiting with code 71` | Startup threw; `App.FailStartup` is ending the process |
| `source added: <log file name>` | A log started being tailed. The file name contains the character name |
| `log line skipped: <log file name> — <exception type> (<n> skipped this session)` | A line threw and was skipped. Written for the first one, then at most once per ten seconds per source (`LineFaultBreaker.JournalInterval`); `<n>` is the running total |
| `alert handler FAILED: <log file name> — <stage> (<exception type>, <n> this session); the line was still parsed` | An alert handler threw and was contained. `<stage>` is `triggers`, `trigger share`, `raid line`, `status callout` or `spell timers`. Written for the first one, then at most once per ten seconds per source; `<n>` is the running total |
| `source STOPPED: <log file name> — <n> lines in a row failed (<exception type>, <n> skipped this session)` | 200 consecutive lines failed; that source reads no further. Ends `; its fight in progress was ended` when it was in combat: that fight was announced and saved, cut short |
| `source STOPPED: <log file name> — the log reader failed (<exception type>)` | The tail reader threw; that source reads no further. The same ending when it was in combat |
| `fight consumer FAILED: <consumer> (<exception type>, <n> this session)` | One consumer of a finished fight threw. Written for the first ten, then every hundredth |
| `archive save FAILED: <type and SQLite code> (<n> this session) — the fight was parsed but is not in history.db` | A save to the archive failed. Written for the 1st, 10th, 100th, 1,000th failure; `<n>` is the running total |
| `archive: <n> fight saves failed this session (last: <type and SQLite code>)` | Written at exit when more than one save failed: the total the sparse lines above cannot give |
| `archive: exit did not wait for <n> queued fight saves — not in history.db` | The exit gave the archive writer five seconds and it was still busy (a large import just before closing). Those fights were dropped, not failed |
| `archive: exit backup FAILED (<type>) — the archive is unaffected, earlier backups were kept` | The exit snapshot could not be written |
| `raid file synced: <name> (<n> chars)` / `raid file sync FAILED: <name> (<exception type>)` | A raid command file write |
| `tts synthesis (sherpa-onnx native)` | About to call the native speech engine; at most one line per ten seconds |
| `heartbeat ws=<n>MB gc=<n>MB sources=<n>` | At most once a minute, written from the UI tick: working set, managed heap, tracked logs |
| `exit begin` then `clean-exit` | `App.OnExit` started, then finished. `clean-exit` is always the LAST line of a clean session: `SessionJournal.End` closes the journal after writing it, and nothing is appended afterwards |

How to read it:

- **Startup crash**: the last startup line names the stage that completed; the fault is in
  the next one, and `startup FAILED` follows it with the exception type. No stage line at
  all after `session start` means loading the settings, the localisation setup or
  `SourceManager`'s construction (triggers, timers). The archive is not a suspect in either
  place on the current build: opening it never throws (an `archive QUARANTINED` or
  `archive UNAVAILABLE` line says what happened instead) and neither does the restore
  (`HistoryService.RestoreInto` journals `archive restore ...` and carries on), so no
  `history restored` after `settings + localization loaded` points at the correlator
  replaying the restored fights. On builds without those lines both stages DO throw on a bad
  `history.db`: there, suspect the archive first.
- **Freeze**: heartbeats come from the UI thread's tick. A gap of several minutes between
  heartbeats is the UI thread not running, and its start and end bracket the freeze.
- **Memory**: a `ws=` figure that climbs heartbeat after heartbeat up to the last line is a
  leak ending in an out-of-memory kill.
- **Native speech crash**: a `tts synthesis` line as the very last line, with no crash file,
  points at the native engine.
- **No `clean-exit` as the last line**: the session did not shut down through `App.OnExit`.
  A failed startup is one such case. Only the final line counts
  (`SessionJournal.EndedCleanly`): the words inside another line (a log called
  `eq2log_clean-exit.txt` in a `source added` line) do not make a session clean. Earlier
  builds accepted the text anywhere in the file, so on those a session that died after such
  a line left no postmortem.

Not journalled: individual log lines, fights, uploads or trigger firings. A skipped line, an
alert-handler fault and a stopped source are journalled, but with the log file name and the
exception type only, never the line's text or the exception message. The archive lines carry
the exception type, SQLite's result code and file names, never a path. The journal says that
something failed; the crash report says why.

## Postmortems: `crashes\postmortem-<timestamp>.txt`

At startup, if the previous `session.log` does not END with a `clean-exit` line,
`SessionJournal` copies that journal into a postmortem and appends matching entries from the
Windows Application event log (last 48 hours; sources Application Error, .NET Runtime,
Windows Error Reporting; mentioning EQ2Parser; at most ten).

- The timestamp in the file name is the NEXT launch, not the death. The time of death is the
  last line of the journal inside.
- The header is built from what that session left behind (`SessionJournal.PostmortemHeader`).
  Crash reports are matched to the session by the time in their file names: from the first
  line of the journal up to the second this launch began. If one of them is of a kind that
  ends the process (`fatal`, `crash-loop`, `fatal-startup`), the header names it and says to
  start there. If none is, it says the session left no managed crash report that explains
  the exit: a native crash, a stack overflow, an out-of-memory kill or a kill from outside.
  Reports the app survived (`ui-thread`, `log-line` and the like) are listed separately as
  possibly related, never as the cause.
- When the journal's first line has no readable timestamp, the header says it cannot tell
  which reports belong to the session. Look for a `crash-*.txt` written shortly before the
  postmortem.
- The header's last paragraph is about dumps. A dump of the app written since that session
  started is named by its full path, wherever Windows put it (`SessionJournal.DumpsFromSession`
  has the rule; the folders are listed under "Crash dumps"). The file is only named, never
  copied or opened: ask the tester for it. With no dump and no process-ending report, the
  wording turns on what that session's journal recorded about the machine: `crash dumps ON`
  and the header says a missing dump leans toward a kill from outside or a forced end, and
  is not proof; `crash dumps OFF` and it says no dump could exist; neither line (an older
  build's journal) and it says a missing dump means nothing.
- A postmortem is written by the build that launched next. One written by a build up to
  v0.5.7 always says "left no managed crash report" without checking, so for those look for
  a `crash-*.txt` whose time matches the end of the journal before concluding the death was
  native. A postmortem from any earlier build reads "had asked Windows for a dump ... has no
  new .dmp" as evidence of a kill from outside. It is not: those builds asked in a place
  Windows does not look, and searched only `crashes\dumps`. Check `%LOCALAPPDATA%\CrashDumps`
  yourself.
- A forced end (Task Manager's End task, power loss) produces a postmortem too. A postmortem
  with an empty event-log section is consistent with those, and with antivirus.
- The event-log entries include the faulting module and exception code for native crashes,
  and full executable paths (Windows user name).

## Crash dumps

A Windows crash dump (a `.dmp` of the process, written by Windows Error Reporting) is the
only evidence of a native crash that has a stack in it. Whether one exists is decided by the
MACHINE, not by the app. This was tested on Windows 11 with a throwaway executable made to
die both by a fail-fast and by an unhandled exception:

- Windows reads its `LocalDumps` settings from `HKEY_LOCAL_MACHINE` only. The same settings
  under `HKEY_CURRENT_USER`, which is what every earlier build of the app wrote at launch
  (`DumpFolder` pointing at `crashes\dumps`, `DumpType` 1, `DumpCount` 5), are ignored:
  nothing arrived in the folder they named.
- When the machine-wide `LocalDumps` key exists (an administrator or other software created
  it), Windows writes a dump for the crash into `%LOCALAPPDATA%\CrashDumps`, named
  `<executable name>.<process id>.dmp`: `EQ2Parser.App.exe.1234.dmp` for the shipped app.
  The key is
  `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps`.
- When that key does not exist there is no dump anywhere, and creating it takes
  administrator rights, which a per-user install does not have.

So on most machines there is NO dump, and its absence proves nothing. Never read a missing
dump as "killed from outside" unless the session's journal says `crash dumps ON`.

What the current build does (`SessionJournal.NoteCrashDumps`, at every launch, and only when
the process is the app's own executable: `SessionJournal.DumpImageName`):

- It writes nothing to the registry. It deletes the two per-user keys earlier builds left
  (`...\LocalDumps\EQ2Parser.exe` from builds up to v0.5.7, and one named after the running
  executable), because a setting that does nothing but looks like the cause is worse than
  none.
- It reads the machine-wide key and journals `crash dumps ON — ...` with the folder a dump
  would land in, or `crash dumps OFF — ...`.
- At the next launch after a dirty exit, the postmortem looks for a dump of the app written
  since the dead session began in: `crashes\dumps` under the data folder (any `.dmp`),
  `%LOCALAPPDATA%\CrashDumps`, and the folder the machine's `LocalDumps` settings name if
  they name one. In the last two, which other programs share, only files named
  `<executable name>.<process id>.dmp` count. It names what it finds by full path and
  touches nothing.

That the machine key, or a per-application key beneath it, can send dumps to another folder
(`DumpFolder`) is from Microsoft's documentation of `LocalDumps`, not from the test above.

### Turning dumps on for a tester who can reproduce a native crash

Optional, and it NEEDS ADMINISTRATOR RIGHTS: offer it only to a tester who can make the
crash happen again and is willing to. Run once, from an elevated PowerShell ("Run as
administrator"):

```powershell
$k = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\EQ2Parser.App.exe'; New-Item -Path $k -Force | Out-Null; New-ItemProperty -Path $k -Name DumpFolder -PropertyType ExpandString -Value '%LOCALAPPDATA%\EQ2Parser\crashes\dumps' -Force | Out-Null; New-ItemProperty -Path $k -Name DumpType -PropertyType DWord -Value 1 -Force | Out-Null; New-ItemProperty -Path $k -Name DumpCount -PropertyType DWord -Value 5 -Force | Out-Null
```

Tell them what it does before they run it:

- It asks Windows for a minidump (`DumpType` 1) of `EQ2Parser.App.exe` whenever it crashes,
  in the app's own `crashes\dumps` folder, keeping the newest five. The next launch's
  postmortem names the file. A minidump holds the crashing process's stacks and some of its
  memory, which can include character names and log text: ask for it privately.
- `New-Item -Force` also creates the parent `LocalDumps` key when the machine does not have
  it, and that key existing is what turned dumps on for every program in the test above
  (theirs go to `%LOCALAPPDATA%\CrashDumps`). Say so. This command has not itself been run
  against a native crash of the app: the per-application folder is the documented behaviour,
  and the lookup also checks the default folder in case Windows uses that instead.
- To undo it, from an elevated PowerShell:
  `Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\EQ2Parser.App.exe'`.
  Leave the parent key alone unless the tester knows nothing else relies on it.

## What leaves no file at all

These fail quietly. Absence of a crash file says nothing about them.

- **A dead log source, on builds up to v0.5.7 only.** There the first exception on the line
  path ended the tail loop, and the only trace was that source's row on the Sources page
  reading `error: <message>` (message only, no stack trace). The current build leaves a
  crash report and a journal line for a skipped line and for a stopped source. See the next
  section for both.
- **A trigger disabled by a regex timeout.** `TriggerEngine` stops evaluating that trigger
  for the session and raises `MatchTimedOut`. No crash file, but `SourceManager.OnTriggerTimedOut`
  writes a journal line (`trigger DISABLED for this session`) and shows a toast on the
  Notifications overlay. Builds up to v0.5.7 did neither. "This trigger stopped firing until I
  restarted" fits.
- **A malformed user `source_overrides.json`.** It is skipped. No crash file; the journal gets
  `user override file IGNORED` with the reason (`SourceManager.SourceOverridesError`). Builds
  up to v0.5.7 recorded nothing.
- **A failed settings, trigger or timer save.** Swallowed by design (the `Save` methods of
  the stores); only the in-memory state changes. A failed ARCHIVE save used to be on this
  list. It now leaves an `archive-save` report and a journal line: see `app-data.md`,
  "The archive".
- **A fight in progress when the app is closed.** A clean exit does not end it, so it is
  not saved or uploaded. (A source that STOPS on an error does end its fight.)
- **Upload and update failures.** Status lines on the Settings page only
  (`UploadQueue.Status`, `UpdateService.Status`). Ask for the exact text or a screenshot.

## "It stopped parsing": skipped lines and stopped sources

An exception thrown while one line is processed costs that line, not the source.
`LogSource.PumpLine` catches it, skips the line and reads on. The source stops only when 200
lines in a row fail (`LineFaultBreaker`) or when the tail reader itself fails. So first find
out which of three things happened. The Sources page row and the `crashes` folder say:

| Sources row | Left behind | What happened |
| --- | --- | --- |
| The normal counters, still rising, ending `· N skipped on error (last: <exception type>) — see the crashes folder` | `crash-*-log-line.txt` (at most five per source per session, and none within five seconds of another report); `log line skipped: ...` in `session.log` | N lines threw and were dropped. The source is alive. The user's complaint is missing numbers, not a stopped log |
| `error: stopped after 200 lines in a row failed to process — last: <type>: <message>` | `crash-*-log-source-stopped.txt`; `source STOPPED: ... lines in a row failed` in `session.log`; usually `log-line` reports from just before | Every line was failing, which is a structural fault and not one bad line. That source reads nothing more this session |
| `error: <message>` that does not begin `stopped after` | `crash-*-log-reader.txt`; `source STOPPED: ... the log reader failed` in `session.log` | The reader failed mid-read (an IO error: a locked range, a drive that went away). That source reads nothing more this session |
| The normal counters, nothing about skips | `crash-*-log-alert.txt` (at most five per source per session); `alert handler FAILED: ...` in `session.log` | An alert handler threw on some lines. Every line was still parsed: no number is missing, but a trigger, timer, callout or roster update may not have happened. The row does not show these yet; `LogSource.AlertFaults` has the count |

`LogSource.Error` is set only when the source has STOPPED; `LogSource.SkippedLines` and
`LastLineError` carry the skips. Nothing restarts a stopped source, and because it is still
in the list the folder watcher will not re-add it.

**A stopped source ends its fight.** Before `Error` is set, the source ends the fight it was
in through the engine's normal path (`LogSource.EndFightInProgress`), so the fight reaches
the correlator, the archive and the upload queue once, like any other, and no row is left
showing combat for the rest of the session. The `source STOPPED` journal line then ends
`; its fight in progress was ended`. That fight is cut short: it holds what was parsed
before the stop and nothing invented, and it lacks the lines that failed or were never
read. So "the last pull of the night is short on every number, for one character" plus a
`source STOPPED` line is this, not a grammar bug. Builds without that journal ending left
the fight active instead: never saved, never uploaded.

What runs inside the per-line guard, on the pump thread (a throw here skips the LINE):

- `LogLine.TryParse`, the grammar (`EnglishGrammar.TryParse`) and its number parsing;
- the idle rule at the top of the line, which can end a fight and announce it;
- `ParserEngine.AddSwing`, which throws by design when no fight is active.

What no longer skips the line: the alert side. Trigger evaluation and every handler of
`TriggerEngine.Fired`, the handlers of `RaidLine`, `StatusApplied` and `TriggerShared`
(roster, DKP progress, loot, callouts, shared-trigger prompt) and the spell-timer
notifications each run in their own guard inside `LogLineProcessor.Process` (`AlertStage`).
A throw there is handed to `LogLineProcessor.AlertFaulted`, the rest of the line still runs,
and `LogSource` writes the `log-alert` report and the journal line. It is never counted as a
skipped line or toward the 200. Earlier builds ran those handlers unguarded, several of them
BEFORE the grammar, so a handler bug cost the line's damage; a tester's "numbers went short
when I turned that trigger on" on such a build is this.

The consumers of a finished fight are not on the first list either. `SourceManager` runs the
correlator, the archive queue, the upload gate and the raid-roster feed each in its own
guard (`OnEncounterEnded`, `Isolated`), and the correlator's `Merged` handler
(`HistoryService.CollapseToPrimary`, which writes to SQLite) the same way. A throw in one
leaves a `fight-consumer` report naming the consumer and a `fight consumer FAILED` journal
line; the others still run and the line is not skipped.

One more candidate: `App.OnStartup` sets a process-wide default regex timeout, the grammar's
generated regexes inherit it, and only `TriggerEngine.Process` catches the timeout exception.

Working it out:

1. Get the row text, the crash report and the journal lines. A `log-line` or
   `log-source-stopped` report has the stack trace and quotes the line that failed, which is
   usually all that is needed. That line is real log text and can be someone's chat: ask for
   the file privately and replace the names before any of it reaches a test or an issue.
2. If the row's count is larger than the number of `log-line` files, the cap or the rate
   limit dropped the rest. The reports that exist name one thrower; other shapes may be
   failing too, so also get the raw log lines from around the time the numbers went short.
3. A skipped line does not wedge anything. The reader has moved past it, and the saved resume
   position (`LogTailReader.ConsumedPosition`) passes it once its read batch is done. If a
   later launch does read it again (the session ended before the position was saved), it is
   skipped again. A source the breaker stopped did not finish its last read batch, so the
   next launch reads that batch again, and stops again once 200 lines in a row have failed.
4. Feed the line through `LogLineProcessor.Process` in a test until it throws. Examples:
   `Suffixless_Decimal_Amount_Parses_Instead_Of_Throwing` in
   `tests/EQ2Parser.Core.Tests/EnglishGrammarTests.cs`, and
   `tests/EQ2Parser.Core.Tests/EnglishGrammarTotalityTests.cs`.
5. Fix the thrower so the line parses or is dropped on purpose. The isolation is a net, not
   the fix: every skipped line is a number missing from someone's parse.

### On a build up to v0.5.7

There the first exception on the line path ended that log's tail loop for good, with no
crash file and no journal line: the only trace was the row reading `error: <message>`. The
handlers of `ParserEngine.EncounterEnded` and the correlator's `Merged` handler were on that
path too. With no stack trace, work from the row text, the raw log lines written around the
moment that character's numbers stopped, and whether it recurs on restart. The resume
position advances per read batch, not per line, so the next launch reads the offending line
again. If the throw came from the grammar, the engine or an `EncounterEnded` handler, the
source dies again at once, every launch. If it came from a live-only path (triggers, timers,
raid lines, callouts, shared triggers), the re-read line is older than
`LogLineProcessor.TriggerFreshness`, those paths are skipped, and the restart "fixes" it.
Either answer narrows the search.
