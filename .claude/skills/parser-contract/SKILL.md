---
name: parser-contract
description: Cross-repo procedure for anything EQ2Parser (client) and EQ2Lexicon (server) must agree on. It makes you name the repo each half lands in, fixes the server-first change order, and gives a payload-field checklist, a table of mirrored code and an end-to-end upload check against a local server. Use it before starting any EQ2Parser change that EQ2Lexicon must agree with, such as an upload or attendance payload field, client_warnings values, the HMAC signature or headers, a new endpoint the parser calls, pet-name or boss-name lists, trigger/timer pack fields, swingtype or success values, or regenerating spell_classes.json. Also use it when an EQ2Lexicon change to ingest, pet detection, rankings scopes, the ACT pack or the downloads page could break the parser, and again before releasing any upload change.
---

# Parser contract: EQ2Parser and EQ2Lexicon

## Which repo

This skill is for the EQ2Parser repository (`VortexUK/EQ2Parser`). It is often an additional
directory in a session rooted in another repo, and its `CLAUDE.md` is not loaded then, so:

- Run git as `git -C <EQ2Parser root>` and gh as `gh --repo VortexUK/EQ2Parser`. A bare `git`
  or `gh` acts on whichever repo the shell happens to be in.
- Before any tag or push, confirm `git -C <EQ2Parser root> remote get-url origin` names
  `VortexUK/EQ2Parser`.
- When a task spans this repo and EQ2Lexicon, say which repo each change lands in before starting.

## When it applies

The parser and the site (a sibling checkout, written here as `EQ2Lexicon/...`) agree by convention
only: no shared schema, fixture or cross-repo test. Two releases shipped broken with green unit tests
on both sides: a 500 on every upload from duplicate attack rows, then per-attack DPS and crit% of 0
from missing `dps` and `critperc`. Use this for any change both must agree on. It is not needed for
parser-only work such as grammar, UI, overlays or local history.

- Parser side: a payload field or value (parse or attendance), `client_warnings`, the signature,
  headers or gzip, an endpoint, pet-name or boss-name lists, the raid floor, pack fields, `swingtype`
  or `success` values, `spell_classes.json`, release asset names.
- Site side: ingest routes or models, plausibility, pet detection, rankings scopes or boss keys, the
  ACT pack, whoami, attendance, the downloads page.

## 1. Say which repo each half lands in

Before reading code, write one line per repo: what changes in EQ2Parser, what changes in EQ2Lexicon,
or "nothing". If the request did not name the repo, or a word like "the app" or "the site" could mean
either, ask the user and wait. Past sessions twice did sound work in the wrong repo: one hardened the
server when the desktop app was meant, one built a parser feature when a site feature was asked for.
Then read your row in `references/mirrors.md` and the transport rules in `references/wire.md`.

## 2. Change order

The site deploys on every push to its main branch. A parser release reaches a user only when their
copy updates, so old parsers (and the ACT plugin, which posts to the same route) stay in the field.
The server must keep accepting whatever any released client sends.

- **New payload field**: server first. It declares the field and a fallback for its absence
  (`_dps_or_encdps` and `_crit_perc_or_computed` in `EQ2Lexicon/backend/server/api/parses/ingest.py`
  are the pattern), deploys, and only then does the parser ship. The server never starts requiring it.
- **New endpoint or response field**: server first. The client reads a 404, a missing key or any
  failure as "feature absent" and stays silent, as `FetchRaidMainsAsync`, `ParseAttendanceAccess` and
  `RaidBossSyncService.SyncAsync` do.
- **Mirrored list or constant**: change every copy in one sitting and name the other half in each
  commit message. The site's copy is the original; the parser's is a port.
- **Stricter server** (a new rejection, cap or status code): first work out what released parsers
  send and how they read the answer. Parsers up to v0.5.7 read every 401 and 403 as a rejected token
  and pause the whole queue until the user steps in. The current `UploadQueue` pauses on a 401 and on
  a 403 that lacks the `X-Lexicon-Reject` response header; a 403 that carries the header fails that
  one upload and the queue carries on. So a new 403 that is about one upload sends the header with a
  short code, and one that is about the caller's account does not (`REJECT_HEADER` in
  `EQ2Lexicon/backend/server/api/parses/ingest.py` has the rule; `references/wire.md` the client side).
  A new limit answers 429, which the queue retries: send `Retry-After` with it and the queue waits
  that long (held to 1 to 120 s) instead of its fixed 30 s. A new shape rule on a field the parser
  fills from the user's machine (the server folder, the log's character name) means a doomed request
  per fight until the parser mirrors it: `LogPaths.ServerNamePattern` is the pattern for that, and
  `references/mirrors.md` has its row.
- **Rename or removal**: do not. Add the new name and keep reading the old one.

## 3. Payload-field checklist

1. DTO in `src/EQ2Parser.Core/Upload/LexiconPayload.cs` (or `src/EQ2Parser.Core/Upload/AttendancePayload.cs`):
   the wire key in `[JsonPropertyName]`, following the encodings listed in `references/wire.md`.
2. `PayloadBuilder.Build` fills it, one row per uniqueness key. Attack rows come from the reference
   bucket only (emitting them per bucket was the 500) and are split per category, so the key is
   (attacker, ability, `swingtype`); `references/wire.md` has the row rules.
3. The server model declares it in `EQ2Lexicon/backend/server/api/parses/models.py`. An undeclared
   or unmapped key is accepted and dropped, so a 201 proves nothing.
4. The server maps it: the `_*_from_payload` functions in the ingest module, the dataclass in
   `EQ2Lexicon/backend/server/parses/models.py`, a new migration for the column, the insert in
   `EQ2Lexicon/backend/server/parses/db.sql`.
5. The fallback for clients that omit it is written and tested.
6. Honest values cannot trip `evaluate` in `EQ2Lexicon/backend/server/parses/plausibility.py`.
7. Every reader of the column is updated: parse page, rankings, and the export API, whose v1 is frozen.
8. Tests on both sides: assert the key in `Payload_Wire_Shape_Matches_The_Ingest_Schema`
   (`tests/EQ2Parser.Core.Tests/UploadClientTests.cs`); cover present and absent in
   `EQ2Lexicon/tests/server/test_parses_ingest.py`.
9. Docs: `EQ2Lexicon/docs/architecture/parse-ingest.md`, and a `references/mirrors.md` row for a new mirror.
10. Run the end-to-end check. Release the parser only after the site's deploy of the server half is confirmed.

## 4. End-to-end check

Upload one fight from a parser build to a local server and read the stored row. Steps marked
`(unverified)` were worked out from code and have not been run; fix this file once you have run them.

**Server, from the EQ2Lexicon root**

1. Choose the database first: the backend applies migrations at startup and the upload writes rows.
   The DSN is the first of `DATABASE_URL`, `SUPABASE_DB_URL`, `POSTGRES_CONNECTION_STRING` that is
   set, and the local `.env` may name production. Set `DATABASE_URL` in the shell (it wins over `.env`)
   to a local scratch database, in the form `EQ2Lexicon/.env.example` shows for `TEST_DATABASE_URL`, and
   not the pytest one, which the suite wipes. Create it with `createdb eq2lexicon_contract` (unverified).
2. Start it with the command inside `EQ2Lexicon/scripts/dev_backend.ps1`:
   `uv run uvicorn backend.server.app:app --port 8000 --reload --reload-dir backend --timeout-graceful-shutdown 2`.
   Keep `--reload`: on Windows `open_pools` refuses to start without it. `ALLOWED_SERVERS` decides which
   `logger_server` passes; unset, it allows Varsoon and Wuoshi. A clean start on an empty database is (unverified).
3. Get a token. No seed script mints one: `POST /api/auth/tokens` needs a browser session, and the
   token is stored hashed in the `api_tokens` table of the `users` schema. Either sign in locally and
   use `/settings/tokens` (needs the Discord OAuth variables and `HTTPS_ONLY=false`) (unverified), or
   run the mint snippet in `references/wire.md` (unverified). No character claim is needed: an
   unclaimed logger is stored with `uploader_verified = 0`, gets no guild and never ranks.

**Parser, from the EQ2Parser root**

4. Make an empty scratch directory and put a `settings.json` in it holding
   `{ "LexiconBaseUrl": "http://localhost:8000", "FirstRunShown": true }` (a partial file like this
   loads; `scripts/tools/ui-check.ps1` seeds its scratch runs the same way). No Settings control edits
   the URL, and it is read at startup. `UrlProblem` allows plain http only for loopback.
5. In PowerShell set `$env:EQ2PARSER_DATA_DIR = '<scratch dir>'` (it keeps the real settings, token
   and history untouched), then run `dotnet run --project src/EQ2Parser.App`. In Settings, Parse
   uploads: paste the token, Save, Test. Test calls whoami and should name the local user.
6. Replay a short log. `logger_server` is the log's parent directory and `logger_name` the file name
   (`LogPaths.ParseServerName`, `LogSource.DeriveOwner`), so copy a log to
   `<scratch dir>/logs/<allowed server>/eq2log_<Letters>.txt`. The committed corpus in `tests/logs`
   replays into two fights. In Sources choose "Add + parse existing". A fight ends only when a
   later line falls outside the idle window, so the file needs a line after its last fight. Keep the
   log inside the scratch directory: the app writes its raid command files into the folder above
   `logs/`, and launching by hand leaves the Windows crash-dump registry value for the app pointing
   at the scratch directory until the real app is next started.
7. Upload. Right-click a fight and choose "Upload to EQ2Lexicon"; a manual upload carries no
   `client_warnings`. For the automatic path, tick "Upload finished fights to EQ2Lexicon" before
   step 6 instead: every fight in the file uploads (the server allows 60 a minute), stamped
   `log_writer_unverified` because no game holds the file. The Settings status line shows the
   server's `status`, or its `detail` on failure.

**Read it back**

8. Run the queries in `references/wire.md`. Check that the encounter row exists, that the new column
   holds the parser's value rather than the fallback, and that `swing_type`, `success_level` and
   `client_warnings` are what you sent. `quarantined` means the upload sits in `tamper_reports`
   instead; `skipped` means that encid is already stored, so use another fight or a fresh database.

**Cheaper, server half only.** Post the JSON `PayloadBuilder` produces with the hand-post snippet in
`references/wire.md`. No dump switch exists: add a temporary `File.WriteAllText` of `payload.ToJson()`
to the wire-shape test, run it, remove the line (unverified). It skips the client's gzip, signing and queue.
