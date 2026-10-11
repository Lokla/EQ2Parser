# App data: every file EQ2Parser keeps, and how to reset it safely

Read this before answering "where does the app keep X", before asking a tester for a file,
and before deleting or copying anything.

## Where the data lives

`AppSettings.Directory` (in `src/EQ2Parser.App/Services/AppSettings.cs`) is the single
accessor. It returns the `EQ2PARSER_DATA_DIR` environment variable when that is set, and
`%LOCALAPPDATA%\EQ2Parser` otherwise. Every file below hangs off it, so setting the variable
before launch moves ALL app state. The App tests rely on this (`TestDataDir` in
`tests/EQ2Parser.App.Tests/TestDataDir.cs`).

To find any new file a later change adds, grep `src/` for `AppSettings.Directory`.

### The install shares that folder

Releases are packed with `vpk pack --packId EQ2Parser` (`.github/workflows/release.yml`), and
Velopack's Windows installer puts an app under `%LOCALAPPDATA%\<packId>`. Nothing in this repo
overrides that, so the installed program and its data are expected to sit in the same
`%LOCALAPPDATA%\EQ2Parser` folder: Velopack's own entries (`current\` with the binaries,
`packages\`, `Update.exe`) beside the data files listed here. This is Velopack's default, not
something the repo's code states, so confirm it with a directory listing from a real install
before relying on the exact names.

What follows from it:

- "Delete the EQ2Parser folder to reset" also deletes the program. Never give that advice.
  Name the individual files.
- Assume an uninstall removes the whole folder, the fight archive included, until someone has
  checked. Tell a tester to copy `history.db` and `backups\` out first if they care about them.
- A from-source run (`dotnet run --project src/EQ2Parser.App`) uses the same data folder as
  the installed app unless `EQ2PARSER_DATA_DIR` is set. Running your working tree against it
  changes the real archive and settings.

## The files

"Quarantine" means the file is loaded through `PersistedJsonFile.Load`: if it cannot be read
it is renamed `<name>.corrupt-<yyyyMMdd-HHmmss>` and the app continues with defaults.

| File (under the data folder) | Owner | Holds | Quarantine | Deleting it (app closed) |
| --- | --- | --- | --- | --- |
| `settings.json` | `AppSettings` | Every option, the source list with each log's resume byte position, watched folders, overlay positions, export presets, and the API token as a DPAPI blob (`LexiconApiTokenProtected`) | yes | Back to defaults: first-run wizard again, sources and watched folders gone, token must be pasted again, upload off |
| `triggers.json` | `TriggerService` | The user's OWN triggers only | yes | Own triggers are lost; Lexicon triggers come back from the pack |
| `timers.json` | `TimerService` | The user's OWN spell timers only | yes | Own timers are lost; Lexicon timers come back |
| `lexicon_pack.json` | `LexiconSyncService` | The curated trigger and timer pack, cached as the server sent it | yes | Safe: fetched again at next start (needs the network) |
| `lexicon_overrides.json` | `LexiconSyncService` | The user's enable/disable flips on Lexicon rows | yes | Every Lexicon row returns to the curator's default |
| `raid_bosses.json` | `RaidBossSyncService` | Cached raid-boss list for the raid-only upload rule | no (a malformed file is ignored) | Safe: fetched again when the rule is in use |
| `source_overrides.json` | read once by `SourceManager` through `SourceOverrides.MergeFile` | OPTIONAL hand-made corrections merged over the embedded `src/EQ2Parser.Core/Resources/source_overrides.json`; the app never writes it | no | Embedded rules only |
| `history.db`, `history.db-wal`, `history.db-shm` | `HistoryService` over `HistoryStore` | SQLite archive of every finished fight with its full swing list | its own: see "The archive" below | The whole archive is gone. Delete all three together, never one |
| `history.db.corrupt-<yyyyMMdd-HHmmss>`, with `-wal` and `-shm` after that name | `HistoryStore.Quarantine`, called by `HistoryService` | A damaged archive that was renamed aside at startup, byte for byte. The fights saved before that launch are in it | n/a | Those fights are gone for good. Never suggest it; nothing prunes these files |
| `backups\history.db.bak`, `backups\history.db.bak2` | `HistoryService.BackupOnClose` | Snapshot of the archive taken at each clean exit, unless the archive is empty; the previous one rotates to `.bak2`. The snapshot is written to `backups\history.db.bak.tmp` first and the rotation happens only once it is complete, so a failed snapshot leaves both backups as they were | no | Loses the safety net only |
| `dkp-ledger.log` | `DkpLedger` | Append-only record of every confirmed DKP award and loot charge; the app never trims it | no | Do not delete: it is the only record for rebuilding a raid night's points |
| `session.log` | `SessionJournal` | Breadcrumbs for the current session; rewritten at every start | no | Safe |
| `crashes\crash-*.txt`, `crashes\postmortem-*.txt`, `crashes\dumps\` | `CrashLog`, `SessionJournal` | See `crash-logs.md` | no | Safe, but it is the evidence: copy before deleting |
| `voices\<pack>\` | `PiperVoiceCatalog` | Downloaded neural voice models; `*.tar.bz2.partial` and `.staging-*` are a download in progress | no | Safe: download again from Settings. A selected voice whose pack is missing falls back to a Windows voice |

Other names you may see:

- `<name>.tmp` beside a JSON file is an atomic write in progress (`PersistedJsonFile` writes
  the temp file, then replaces the real one). A leftover is overwritten by the next save.
- `<name>.corrupt-<timestamp>` is a quarantined file. For the JSON files the five newest per
  file are kept. A quarantined archive (`history.db.corrupt-...`) is never pruned.
- `backups\history.db.bak.tmp` is an exit snapshot in progress, or one left by an exit that
  died while writing it. The next clean exit deletes it before writing its own; it is never
  promoted to `.bak`.
- Anything else, such as a `.bak` beside a JSON file, is a leftover from an early build. No
  current code reads or writes it.

There is no on-disk speech cache. Synthesised phrases are cached in memory by
`AlertAudioService` and die with the process, so "wrong voice until I restarted" points at
that cache, not at a file.

### State outside the data folder

- Raid command files (`eq2lexicon-raid-list.txt`, `eq2lexicon-raid-dkp.txt`,
  `eq2lexicon-raid-loot.txt` by default; the names are settings) are written by
  `RaidViewModel` into the EQ2 install folder, which `LogPaths.ParseInstallDir` derives from
  a tracked log's path (`<install>/logs/<server>/eq2log_<character>.txt`), or into
  `RaidCommandDirOverride` when set. The writes happen from the UI tick as soon as a source
  resolves an install folder, with no button press.
- The registry. The current build WRITES nothing there. At every launch
  `SessionJournal.NoteCrashDumps` deletes two keys earlier builds created under the current
  user, `Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\EQ2Parser.exe` (builds
  up to v0.5.7) and `...\LocalDumps\<executable name>` (`EQ2Parser.App.exe` for the shipped
  app). Those keys asked Windows for crash dumps in `crashes\dumps`, and Windows ignores them:
  it reads `LocalDumps` under `HKEY_LOCAL_MACHINE` only (verified, see "Crash dumps" in
  `crash-logs.md`). The same method READS the machine-wide `LocalDumps` key to journal
  whether dumps are enabled. Neither happens when the process image is a shared host
(`dotnet.exe`, a test host): `SessionJournal.DumpImageName` decides.
- Windows crash dumps of the app, when the machine has local dumps enabled, are in
  `%LOCALAPPDATA%\CrashDumps` (Windows' own folder, shared by every program) as
  `EQ2Parser.App.exe.<process id>.dmp`, unless the machine's settings name another folder. The
  app never copies, opens or deletes them; the postmortem only names them.
- The EQ2 log files are only read.

## Signatures worth knowing

- "My settings (or triggers, or timers) reset themselves after the update." Look for a
  `.corrupt-` sibling of that file. If there is one, the file failed to parse and the bytes
  are still there: read the quarantined copy to learn why, and add the shape to
  `tests/EQ2Parser.App.Tests/AppSettingsCompatTests.cs`. If there is none, the file loaded,
  so the cause is a field the new build ignores or defaults differently, or a save that
  failed (failed saves are swallowed and leave only in-memory state).
- "My own triggers vanished but the Lexicon ones are there": `triggers.json` holds only the
  user's own; the Lexicon rows are rebuilt from `lexicon_pack.json`.
- "It re-read (or skipped) a chunk of my log after restarting": resume positions live in
  `settings.json` under `Sources`, written by `SourceManager.PersistSources` at exit and
  when a source is added or removed by hand. A session that did not exit cleanly keeps the
  positions from the last of those.
- "Old fights disappeared": startup deletes trash fights older than `HistoryTrashDays` and
  loads only the recent window into the tree (`HistoryService.RestoreInto`). Boss fights
  outside the window are still in the archive.
- The archive has a schema version (`HistoryStore.SchemaVersion`, stored in SQLite's
  `user_version`), but `HistoryStore.Migrate` does not trust it. It reads the columns the
  file actually has and adds the ones this build needs, and `StampVersion` only ever raises
  the stamp. So a file from a newer build opens read/write with its stamp left alone (safe
  while schema changes stay additive), and a file whose stamp an older build lowered opens
  normally. Builds up to v0.5.7 are the other half of this: they key their upgrade steps on
  the stamp and write their own version on every open. On those builds, running an older
  copy once and then going back to the newer one fails at startup with SQLite's "duplicate
  column name", the archive intact on disk. That tester needs a build with the column-keyed
  `Migrate`, not a reset. Still use a copy when you point any other build at someone's
  archive.

## The archive: what happens when it cannot be used

`HistoryService` never stops a launch over the archive. What it does instead is in
`HistoryService.State` (`ArchiveState`), with the reason as English text in
`HistoryService.DegradedReason`; `HistoryService.Problem` adds failed saves and restores to
that. Builds up to v0.5.7 threw instead, which since the fatal-startup change meant exit code
71 on every launch until the file was removed by hand.

| What the session found | State | What it did | Left behind |
| --- | --- | --- | --- |
| The file opens | `Healthy` | Nothing | Nothing |
| SQLite calls the FILE bad: result code 26 "file is not a database" or 11 "database disk image is malformed" / "malformed database schema" (`HistoryStore.IsDamagedFile`) | `Quarantined` | Renamed `history.db` to `history.db.corrupt-<yyyyMMdd-HHmmss>` (a `-wal` / `-shm` beside it follows, as `...corrupt-<timestamp>-wal`), then started a fresh, empty archive. Nothing is deleted | `crash-*-archive-quarantined.txt`; journal `archive QUARANTINED — history.db was damaged (SqliteException <code>); renamed to <file>, a fresh archive started, nothing deleted` |
| Anything else: locked or held by another process, access denied, disk full, path missing, an SQL error from this build's own migration, a missing native library | `Unavailable` | Left the file exactly as it is. The session has NO archive: fights parse, show and upload; none is saved; the archive window is empty | `crash-*-archive-unavailable.txt`; journal `archive UNAVAILABLE — history.db could not be opened; the file was left untouched (<exception type and SQLite code>); this session saves no fights` |
| The file is damaged but will not rename (something else has it open), or it was renamed and a fresh one could not be created | `Unavailable` | As above; in the second case `HistoryService.QuarantinedPath` names the renamed file | `crash-*-archive-unavailable.txt`; journal `archive UNAVAILABLE — history.db is damaged and could not be moved aside ...` or `... was damaged ... and renamed to <file>, but a fresh archive could not be created ...` |

Reading it:

- **"My history is empty after an update or a crash."** Look for `history.db.corrupt-*` in the
  data folder and `archive QUARANTINED` in `session.log`. The old fights are in that file, not
  lost. Ask for it privately (it names characters), work on a COPY, and try sqlite3's
  `.recover` on it: the reason it was set aside is in the crash report. Tell the tester to
  keep the file. To put a repaired one back: app closed, move the current `history.db*` files
  aside, name the repaired file `history.db`.
- **`archive UNAVAILABLE`** is about the moment, not the file. The usual cause is another
  program holding it: a second EQ2Parser process (check Task Manager), a backup or sync client,
  an antivirus scan. The next launch tries again by itself. Do NOT tell the tester to delete or
  rename `history.db`: it is very likely a healthy archive. The `SqliteException` number in the
  journal line says which: 5 busy, 14 cannot open, 8 read-only, 13 disk full, 10 IO error.
- **The split is deliberately lopsided.** Renaming a healthy archive looks, to its owner,
  exactly like losing it, so only SQLite's own verdict on the file moves it. A file SQLite can
  open but this build's SQL trips over (result code 1) is left alone, and the session has no
  archive: that is a bug in the build, and a later build can still open the file.
- **Damage SQLite only finds later** (one unreadable page in the middle of a large file) does
  not quarantine anything. The startup restore loads each fight in its own guard
  (`HistoryService.RestoreInto`): a fight that cannot be read is skipped and the rest load.
  Evidence: `crash-*-archive-restore.txt`, `HistoryService.RestoreFailures`, and a journal
  line that begins `archive restore: <n> of <m> recent fights could not be loaded` (some
  fights) or `archive restore FAILED` (the list itself could not be read).
- **A save that fails** loses that fight from the archive, never the session, and is no longer
  silent. It leaves `crash-*-archive-save.txt` (at most three per session, naming the fight's
  title and zone) and a journal line
  `archive save FAILED: <type and SQLite code> (<n> this session)` for the 1st, 10th, 100th
  failure and so on, then the total at exit, `archive: <n> fight saves failed this session`.
  `HistoryService.SaveFailures` counts every one. A full disk fails one save per fight, which
  is why the journal thins out.
- **A failed exit backup** leaves `history.db` and both existing backups untouched. Evidence:
  `crash-*-archive-backup.txt` and the journal line `archive: exit backup FAILED (...)`.

## Resetting, for real

Resetting a real data folder destroys things that cannot be rebuilt (own triggers, the fight
archive, the DKP ledger). Before any reset:

1. Say exactly which files will be removed and what the person loses with each (the last
   column of the table), and wait for a yes.
2. Have the app closed. A running app rewrites `settings.json` at exit and holds
   `history.db` open.
3. Prefer renaming to deleting (`settings.json` to `settings.json.keep`), so the step can be
   undone.
4. Remove the smallest thing that tests the theory. One JSON file, not the folder.
5. For the archive, copy `backups\` somewhere else BEFORE the next launch.
   `HistoryService.BackupOnClose` skips an EMPTY archive, so an empty one cannot rotate a
   good backup out. But once the new archive holds a single fight the rotation resumes: the
   next clean exit pushes the old archive to `.bak2` and the one after that drops it. Builds
   up to v0.5.7 rotate at every clean exit, empty or not, so there both backups hold the
   new, empty archive two launches after a reset. The same clock runs after a quarantine
   (the fresh archive is a new archive), but there the old fights are also in the
   `history.db.corrupt-` file, which nothing rotates or prunes.
6. Never reset an archive the app has already set aside or declined to touch. A
   `history.db.corrupt-` file is the tester's fights; an `archive UNAVAILABLE` session left a
   probably healthy `history.db` in place. See "The archive" above before removing either.

To put an archive back: with the app closed, move the three `history.db*` files aside (the
glob also matches a quarantined `history.db.corrupt-` file, so name the three, do not use
`history.db*`) and copy `backups\history.db.bak` to `history.db`. A backup written by a
build up to v0.5.7 can be a partial file: there the previous backup was rotated out BEFORE
the new one was written, so a snapshot that failed part-way replaced the good one. If
`.bak` will not open, try `.bak2`.

## Working on a copy

Reproducing against data should never touch the real folder.

**Use `scripts/tools/ui-check.ps1` when it fits** (the sibling `parser-ui-check` skill): it
builds the app, launches it on a scratch folder, and handles every hazard listed below by
itself. `-Log <file>` copies a log excerpt into a game-shaped scratch tree and parses it from
the first line; `-DataDir <copy>` runs on a copy of a data folder after clearing its sources
and turning uploads off. It also saves and puts back the per-user crash-dump registry value
that builds before this one wrote at launch (the current build writes none and removes the
key, which the script leaves removed).

To drive the app by hand instead (clicking through something the script cannot reach), point
it at a scratch directory:

```powershell
$env:EQ2PARSER_DATA_DIR = "$env:TEMP\eq2parser-triage\data"
dotnet run --project src/EQ2Parser.App
```

Run that from the EQ2Parser root. The variable is read by the app's own process, so set it
in the shell that launches it.

What goes in the scratch directory decides how safe the run is:

- Start empty when you can. To skip the first-run wizard (a modal dialog shown when there
  are no sources and no watched folders), create `settings.json` containing only
  `{ "FirstRunShown": true }`. Property names are case-sensitive.
- Do not copy a `settings.json` unchanged. On the same Windows account the token blob still
  decrypts, so with `UploadEnabled` true the scratch run uploads every fight it parses to
  the live site. The copied `Sources` and `WatchedFolders` also make it tail the real logs
  and rewrite the raid command files in the real EQ2 folder. If the bug needs those
  settings, edit the copy first: set `UploadEnabled` to `false`, remove
  `LexiconApiTokenProtected`, and empty `Sources` and `WatchedFolders`.
- Put a log excerpt at `<scratch>\eq2\logs\<Server>\eq2log_<Character>.txt`. The character
  comes from the file name (`LogSource.DeriveOwner`), the server from the parent folder
  (`LogPaths.ParseServerName`), and that layout keeps the raid command files inside the
  scratch tree. Add it on the Sources page with the add-and-parse-existing button
  (`SourcesViewModel.AddWithHistory`); the plain add starts at the end of the file and
  shows nothing.
- `history.db` can be gigabytes. Copy it only when the bug involves the archive or the
  startup restore, and copy all three `history.db*` files with the app closed.
- A scratch run still fetches the Lexicon pack from `LexiconBaseUrl` (read-only). Like any
  launch it deletes the two per-user crash-dump registry keys earlier builds left, and writes
  nothing to the registry. An earlier build run this way repoints that per-user value at the
  scratch folder instead, until its next normal launch.

A replayed excerpt is not "live": see the reproduction step in `SKILL.md` for what that
does and does not exercise.

## What not to ask a tester for

`SECURITY.md` is the source for this.

- Never the API token, in any form. It is read from the password box and never logged.
- Not `settings.json` as a matter of routine. The token inside is DPAPI-encrypted for their
  Windows account and useless elsewhere, but the file also lists their log paths (Windows
  user name, character names). Ask for the one value you need, or a screenshot of the page.
- `history.db`, `dkp-ledger.log`, `session.log` and the crash files all name characters and
  guild activity. Ask for them privately, not in a public issue, and replace the names before
  anything from them reaches a test, a commit message or an issue.
- A report that looks like a vulnerability goes to the repository's private security
  advisories, never a public issue.
