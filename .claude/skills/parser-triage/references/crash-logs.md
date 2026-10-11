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
| `log-source-stopped` | 200 lines in a row failed for one source (`LineFaultBreaker.DefaultTripAfter`) | Nothing; the Sources row reads `error: stopped after 200 lines in a row failed to process ...` | Keeps running; that source has stopped |
| `log-reader` | The tail reader itself failed, for example an IO error mid-read | Nothing; the Sources row reads `error: <message>` | Keeps running; that source has stopped |
| `fight-consumer` | One consumer of a finished fight threw (`SourceManager.Isolated`: correlator, archive save, upload, raid roster, archive collapse) | Nothing | Keeps running; the other consumers still ran |

The last four are failures the app caught where they happened and reported through
`CrashLog.Report`. Builds up to v0.5.7 have none of them, nor `fatal-startup`.

Layout of each file:

1. `EQ2Parser <version> — <kind> exception at <local time>`
2. `<Windows version> · .NET <runtime version>`
3. A blank line. For a caught kind, a context block and another blank line: the log file
   name for the three `log-` kinds, plus the skipped-line count and the failing line itself
   (its first 300 characters) for `log-line` and `log-source-stopped`; the consumer name for
   `fight-consumer`.
4. `Exception.ToString()`: type, message, inner exceptions, stack trace.

What a crash file does NOT contain: what the user was doing or any setting. A `log-line` or
`log-source-stopped` report DOES quote one log line, which can be another player's chat; the
report says so and tells the user to delete the line before sharing if they prefer. Other
kinds hold a log line only if the exception message quotes one. Nothing is redacted, so file
paths in a stack trace or message can show the Windows user name and character names. By
design the API token never reaches a log string (`SECURITY.md`).

Reading traps:

- **One file per five seconds.** Reports closer together than that are dropped, except the
  ones that always land: `fatal`, `crash-loop`, `fatal-startup`, `log-source-stopped` and
  `log-reader`. A crash loop therefore usually leaves one `ui-thread` file and one
  `crash-loop` file a moment later, and the three exceptions between them are gone. If the
  two differ, there were at least two faults.
- **Five `log-line` reports per source per session** (`MaxLineReports` in `LogSource`).
  Later skips are still counted on the Sources row and in the journal, so the number of
  files is not the number of skipped lines.
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
| `crash dumps requested for <executable name>` | The crash-dump registration was written (see "Crash dumps"). Missing on builds up to v0.5.7 and when the process is a shared host such as `dotnet.exe` |
| `settings + localization loaded` | Settings were loaded, the language dictionaries initialised, then `SourceManager` was constructed: triggers, timers and the archive file opened |
| `history restored` | `SourceManager.RestoreHistory` finished (the archive's recent window is in the tree) |
| `sources restored + folder watch started` | Saved sources re-attached, watcher running |
| `startup complete — window shown, overlays restored` | The end of startup (`App.Start`) |
| `startup FAILED (<exception type>) — exiting with code 71` | Startup threw; `App.FailStartup` is ending the process |
| `source added: <log file name>` | A log started being tailed. The file name contains the character name |
| `log line skipped: <log file name> — <exception type> (<n> skipped this session)` | A line threw and was skipped. Written for the first one, then at most once per ten seconds per source (`LineFaultBreaker.JournalInterval`); `<n>` is the running total |
| `source STOPPED: <log file name> — <n> lines in a row failed (<exception type>, <n> skipped this session)` | 200 consecutive lines failed; that source reads no further |
| `source STOPPED: <log file name> — the log reader failed (<exception type>)` | The tail reader threw; that source reads no further |
| `fight consumer FAILED: <consumer> (<exception type>, <n> this session)` | One consumer of a finished fight threw. Written for the first ten, then every hundredth |
| `raid file synced: <name> (<n> chars)` / `raid file sync FAILED: <name> (<exception type>)` | A raid command file write |
| `tts synthesis (sherpa-onnx native)` | About to call the native speech engine; at most one line per ten seconds |
| `heartbeat ws=<n>MB gc=<n>MB sources=<n>` | At most once a minute, written from the UI tick: working set, managed heap, tracked logs |
| `exit begin` then `clean-exit` | `App.OnExit` started, then finished |

How to read it:

- **Startup crash**: the last startup line names the stage that completed; the fault is in
  the next one, and `startup FAILED` follows it with the exception type. No
  `history restored` after `settings + localization loaded` means the archive restore
  (`HistoryService.RestoreInto`, `HistoryStore`). No stage line at all after `session start`
  means loading the settings, the localisation setup or `SourceManager`'s construction
  (triggers, timers, opening the archive).
- **Freeze**: heartbeats come from the UI thread's tick. A gap of several minutes between
  heartbeats is the UI thread not running, and its start and end bracket the freeze.
- **Memory**: a `ws=` figure that climbs heartbeat after heartbeat up to the last line is a
  leak ending in an out-of-memory kill.
- **Native speech crash**: a `tts synthesis` line as the very last line, with no crash file,
  points at the native engine.
- **No `clean-exit`**: the session did not shut down through `App.OnExit`. A failed startup
  is one such case.

Not journalled: individual log lines, fights, uploads or trigger firings. A skipped line and
a stopped source are journalled, but with the log file name and the exception type only,
never the line's text or the exception message. The journal says that parsing failed; the
crash report says why.

## Postmortems: `crashes\postmortem-<timestamp>.txt`

At startup, if the previous `session.log` has no `clean-exit` line, `SessionJournal` copies
that journal into a postmortem and appends matching entries from the Windows Application
event log (last 48 hours; sources Application Error, .NET Runtime, Windows Error Reporting;
mentioning EQ2Parser; at most ten).

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
- The header's last paragraph is about dumps. A `.dmp` written since that session started is
  named. With no dump and no process-ending report, the wording turns on whether the journal
  holds `crash dumps requested for`: if it does, the header reads the missing dump (together with
  an empty event-log section) as pointing to a kill from outside; if it does not, it says a
  missing dump means nothing. Read the first of those with the caveat under "Crash dumps".
- A postmortem is written by the build that launched next. One written by a build up to
  v0.5.7 always says "left no managed crash report" without checking, so for those look for
  a `crash-*.txt` whose time matches the end of the journal before concluding the death was
  native.
- A forced end (Task Manager's End task, power loss) produces a postmortem too. A postmortem
  with an empty event-log section is consistent with those, and with antivirus.
- The event-log entries include the faulting module and exception code for native crashes,
  and full executable paths (Windows user name).

## Crash dumps: `crashes\dumps\`

At every launch `SessionJournal.RegisterCrashDumps` asks Windows Error Reporting for local
dumps (minidump, five kept) under a per-user registry key named after the running
executable's file name (`SessionJournal.DumpImageName`). For the shipped app that is
`EQ2Parser.App.exe`, the name the release is packed with (`--mainExe`). Windows matches the
key against the crashing executable's file name. Builds up to v0.5.7 registered
`EQ2Parser.exe`, a name no shipped executable has, so they could not have produced a dump;
the current build removes that stale key. A shared host (`dotnet.exe`, `testhost.exe`) is
never registered. The journal line `crash dumps requested for <executable name>` records
that the request was written.

Caveat: Microsoft documents `LocalDumps` under `HKEY_LOCAL_MACHINE`. This registration is
under `HKEY_CURRENT_USER`, which needs no administrator rights, and whether Windows honours
a per-user key is unverified. Until a real native crash has been seen to leave a `.dmp` in
`crashes\dumps`, the absence of a dump is not evidence of anything, whatever the postmortem
header suggests.

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
- **A failed archive save or settings save.** Swallowed by design (`HistoryService`'s
  writer, the `Save` methods of the stores).
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

`LogSource.Error` is set only when the source has STOPPED; `LogSource.SkippedLines` and
`LastLineError` carry the skips. Nothing restarts a stopped source, and because it is still
in the list the folder watcher will not re-add it.

What runs inside the guard, on the pump thread:

- `LogLine.TryParse`, the grammar (`EnglishGrammar.TryParse`) and its number parsing;
- `ParserEngine.AddSwing`, which throws by design when no fight is active;
- trigger evaluation and every handler of `TriggerEngine.Fired`;
- the handlers of `RaidLine`, `StatusApplied` and `TriggerShared` (roster, DKP progress,
  loot, callouts, shared-trigger prompt).

The consumers of a finished fight are not on that list any more. `SourceManager` runs the
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
