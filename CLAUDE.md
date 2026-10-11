# CLAUDE.md — EQ2Parser

## What this is

A modern Windows combat parser for EverQuest II TLE raiding and the EQ2
Lexicon site. .NET 10, WPF (Fluent
theme), Velopack distribution, MIT. See README.md for the decision table.

Sibling repos: `EQ2Lexicon` (the website this app uploads to — checked
out beside this repo in the same `git` folder; the drive letter varies
per machine, so resolve it relative to this repo's parent),
`EQ2LexiconACTPlugin` (the ACT plugin this app will eventually replace —
its UploadClient/PayloadSigner are the reference for the upload protocol),
`AdvancedCombatTracker` (EQAditu's companion repo — localization + plugin
sources; the app itself is closed source). The latter two are on GitHub
(VortexUK/EQ2LexiconACTPlugin, EQAditu/AdvancedCombatTracker) — look for
them beside this repo first and clone as needed.

This file is NOT loaded when the repo is an additional directory in a
session rooted elsewhere (the usual case: sessions run from EQ2Lexicon).
The skills are, so anything an agent must know in that setup lives in a
skill — see "Skills" below.

## Hard rules

- **Cleanroom parsing**: `Plugins/Standalone/ACT_English_Parser.cs` (and the
  Russian variant) in the AdvancedCombatTracker repo are UNLICENSED — behavioural
  reference only, never copy code from them. Correctness is proven by diffing
  our output against ACT's on real logs.
- **Core stays UI-free**: `EQ2Parser.Core` must never reference WPF or any
  Windows-only UI API. Everything testable lives there; the App project is a
  thin shell.
- **Upload protocol compatibility**: same contract as the ACT plugin — bearer
  token, HMAC-SHA256 over the UNCOMPRESSED JSON in `X-Lexicon-Signature`,
  body gzipped (`Content-Encoding: gzip`). Server side lives in
  EQ2Lexicon `backend/server/api/parses/ingest.py` + `core/gzip_request.py`.
- ACT **trigger XML share-format import** is a compatibility promise; binary
  ACT plugin compatibility is explicitly NOT.
- **Keep the numbers stable, improve everything around them**: stat definitions
  that feed visible numbers (EncDPS, durations, ally graph, success level, 6 s
  idle rule) stay stable so site rankings remain comparable. Improvements
  (multi-log, catch-up, grammar-as-data, etc.) live in
  docs/engine-behaviour.md → "Key capabilities".
- **Multi-log architecture**: one parse pipeline per log source (own tail
  reader / grammar / perspective state), feeding an encounter correlator that
  merges concurrent encounters across sources (zone + time overlap + shared
  enemies) with per-combatant authority — a character's own log wins for that
  character. A single configurable "primary character" scopes trigger
  audio/TTS/timers. Design every engine type source-aware from the start.

## Build / test

```
pwsh -NoProfile -File scripts/tools/verify.ps1     # the CI gate, failures only
pwsh -NoProfile -File scripts/tools/ui-check.ps1   # launch on scratch data, screenshot + rendered text
dotnet run --project src/EQ2Parser.App             # run from source
dotnet format EQ2Parser.slnx                       # fix a format failure
```

`verify.ps1` is `dotnet build`, `dotnet format --verify-no-changes` and
`dotnet test` in Release, as CI and `.githooks/pre-push` run them (the
hook is active only after `git config core.hooksPath .githooks`).

.NET 10 SDK pinned via global.json. `Directory.Build.props` sets nullable,
analyzers and warnings-as-errors, so the build is also the linter. There
is NO version number in any file: the version is the git tag (`vX.Y.Z`),
injected at publish time by `.github/workflows/release.yml`. Pushing a
tag publishes a release to every installed copy; nothing is bumped.

## Layout

| Path | Purpose |
|---|---|
| `src/EQ2Parser.Core` | Parse engine: log tailing, line grammar, encounter model, stats, trigger + spell-timer engine, upload client. No UI deps. |
| `src/EQ2Parser.App` | WPF shell (net10.0-windows): main window, overlay, tray, settings, Velopack bootstrap. |
| `tests/EQ2Parser.Core.Tests` | xunit. Golden-file tests use real EQ2 log excerpts under `tests/logs/`. Also the source-tree guards (localization, layering, agent docs). |
| `tests/EQ2Parser.App.Tests` | xunit for App services; each class gets its own data directory, parallelism off. |
| `scripts/tools` | Dev helpers: `verify.ps1`, `ui-check.ps1`, `logmine.ps1`. ASCII-only. |
| `docs` | `engine-behaviour.md` (the stat and pipeline contract; source comments cite its sections), `code-signing.md`. |
| `.claude/skills` | Agent skills, all named `parser-*`. |

## Skills

| Skill | Use it to |
|---|---|
| `parser-verify` | Run the CI gate and see only the failures |
| `parser-ui-check` | See a UI change: launch on scratch data, read the rendered text, open the screenshot |
| `parser-release` | Cut a release: preflight, local publish that is looked at, tag, watch, check assets |
| `parser-grammar` | Find lines in real logs, measure grammar coverage, add a line shape |
| `parser-checklists` | Multi-file changes: setting, string, column, overlay, page, trigger field, schema, curated data |
| `parser-contract` | Anything EQ2Lexicon must agree with: payloads, mirrored lists, the end-to-end upload check |
| `parser-triage` | A bug report or crash: pin the build, reproduce, fix every path, regression test |

`tests/EQ2Parser.Core.Tests/AgentDocsTests.cs` fails when this file or a
skill names a repo path that does not exist.

## Upload wiring (parser → EQ2Lexicon)

The vertical (2026-08-03): `Core/Upload/` holds the testable pieces —
`LexiconUploadClient` (transport: bearer + HMAC + gzip, injectable handler,
plus the `UrlProblem` https-only guard with a loopback dev exception),
`LexiconPayload` (wire DTOs), `PayloadBuilder` (Encounter → payload; 8-hex
content encid so re-uploads dedupe server-side), `LogPaths.ParseServerName`
(logger_server = the log's parent dir; "" for the legacy `logs/` root.
The server requires `logger_server` and has no default world, so the
parser sends NOTHING for such a log: the auto path skips the fight with a
reason in the status line (`UploadService.SkippedNoServer`), a manual
upload is refused locally, `UploadQueue.Enqueue` drops a blank server and
no attendance snapshot is built — only logs under `logs/<server>/`
upload, and the server must be on the site's allow-list),
and `UploadQueue` (channel drain off
the pump thread, bounded retry on network/5xx and on the site's 429. A 401,
or a 403 WITHOUT the `X-Lexicon-Reject` response header, sets AuthPaused so
a bad token or an unapproved account is never hammered. A 403 WITH the
header is about that one upload: it fails alone and the queue carries on;
the code `server-not-allowed` also stops sending for that logger_server
for the session (`SkippedRefusedServer`). The header mirrors `REJECT_HEADER`
in `EQ2Lexicon/backend/server/api/parses/ingest.py` — keep them in step).

App side: `UploadService` (thin adapter on SourceManager:
`SourceManager.OnEncounterEnded`, the one handler Add/Remove subscribe to
`Engine.EncounterEnded`, calls it beside History.QueueSave, each consumer
in its own guard so one failing cannot starve the rest;
`Configure` re-reads settings), `TokenProtector` (DPAPI CurrentUser — only
ciphertext ever reaches settings.json; ProtectedData is in-box on the
windows TFM, no package), Settings → "Parse uploads" card (PasswordBox is
read+cleared in the command, never bound), and a fight-tree "Upload to
EQ2Lexicon" context item that sends every source's view of the fight
(manual upload works with the auto toggle off and clears an auth pause —
the explicit click is the consent/retry).

Uploads follow a fleet model: every finished encounter per
source uploads (trash included — the site's retention sweep handles it);
multi-log mirrors of one fight are mirror-grouped server-side by distinct
logger_names, longest duration wins as primary. Token test button hits
`/api/auth/whoami` (accepts bearer) and shows the Discord name.

**Raid-only option (2026-09-30, `AppSettings.UploadRaidOnly`, off by
default)**: with it on, `UploadService.ShouldAutoUpload` passes a fight only
when `Core/Upload/RaidUploadFilter.IsRaidEncounter` says so — its title is
on the site's curated raid-boss list OR it had ≥ 7 player allies
(`RaidMinPlayers`, the site's own raid scope; the count comes from
`CombatantClassifier.PlayerAllyNames`, pets out). The list is
`GET /api/zones/raid-bosses` (public, `{version, bosses[]}`), synced by
`App/Services/RaidBossSyncService` (cache `raid_bosses.json`, 6 h refresh
on the shell tick, offline = last good list, none = headcount alone).
`RaidUploadFilter.NormaliseBossName` mirrors the site's `boss_key` in
`EQ2Lexicon/backend/server/parses/boss.py` (NFC, lowercase, apostrophe and
space look-alikes folded; `rankings.py` keeps the old name
`_normalise_boss_key` as an alias) — keep them in step. Skipped fights
are counted (`SkippedNonRaid`) and named in
the Settings status line. Manual fight-tree uploads ignore the rule (the
click is the consent).

Log-writer provenance (2026-08-03): `Core/Logs/LogFileHolders` probes who
holds the log file via the Windows **Restart Manager** (the supported
"which processes are using this file" API — write-handle-specific
enumeration would need undocumented NtQuerySystemInformation; rejected as
fragile). EQ2 keeps its log open for append while /log is on, so
"EverQuest2 among the holders" is the live-writer signal. `LogProvenance`
(pure, tested) turns a probe into `client_warnings`: `log_writer_eq2` /
`log_writer_unverified` + a single bare `log_foreign_holder` marker when any
process other than EQ2, ACT ("Advanced Combat Tracker" — the normal
co-holder) or us has the log open. Since v0.5.6 the upload never names a
process (privacy: earlier builds stamped `log_foreign_holder:<name>`). The
probe runs on the UploadQueue drain thread seconds after the fight ends
(~ms cost, once per fight, never per line); manual re-uploads of archived
fights skip it (`withProvenance: false`) — probing NOW says nothing about
who wrote the log THEN. Explicit decision: NO server-side stat recompute
from raw swings — needlessly expensive; provenance stamping is the
mechanism.

## EQ2 log format notes

Log lines are `(epoch)[local timestamp] message`, e.g.
`(1753738000)[Mon Jul 28 22:26:40 2026] You hit a training dummy for 100 points of crushing damage.`
Log path shape: `<install>/logs/<server>/eq2log_<character>.txt` (the ACT
plugin's LogPathParser documents the variants). Files are ANSI/UTF-8 mixed
historically — the tail reader must handle encoding defensively.

Owner-name aliasing: some temp pets act entirely under the OWNER's name —
the Templar hammer's swings log as "Menludiir's Divine Smash …" AND its
expiry logs as "Menludiir has died", indistinguishable from a real player
death (indistinguishable in the log — owner deaths are corrected via the
second-person "has killed you" signal, third parties left as-is). Consequences:
pet abilities need source_overrides.json entries, and death counts/lines
are NOT a trustworthy signal for heuristics (this killed the death-window
idea for two-dirge raid-buff disambiguation — see RaidBuffAttributor: an
even estimated split is the proven ceiling, the log carries no caster
identity for granted buffs anywhere).
