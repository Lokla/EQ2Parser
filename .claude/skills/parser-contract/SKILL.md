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

One command uploads real fights from this working tree to a local copy of the site and reads
back what it stored. It takes about a minute and it is the only check that runs the client's
gzip, signature and queue against the site's real ingest route:

```powershell
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/e2e-upload.ps1                      # site checkout beside this repo
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/e2e-upload.ps1 -Lexicon <EQ2Lexicon root>
pwsh -NoProfile -File <EQ2Parser root>/scripts/tools/e2e-upload.ps1 -Keep               # leave artifacts/e2e-upload to look at
```

It prints one `PASS` / `FAIL` line per check and ends `e2e-upload: OK` (exit 0), `FAIL` (1) or
`INCOMPLETE` (2: something it needs is missing and nothing was checked). What it does:

1. Creates a throwaway database on the local PostgreSQL the site's own tests use (the
   `TEST_DATABASE_URL` in the site's `.env`; it refuses any host that is not local) and applies
   the site's migrations. The site cannot even be imported against an empty database, so
   migrations come first.
2. Starts the site from its checkout, as it is on disk, with every database variable pointed at
   that throwaway database, so no other database is reachable from the process.
3. Mints an API token for a throwaway user in it.
4. Runs this repo's app through `scripts/tools/ui-check.ps1` on scratch data with the committed
   corpus under `logs/Varsoon` (accepted) and again under `logs/Kaladim` (not on the allow-list,
   so refused with `X-Lexicon-Reject`). The token is written DPAPI-protected, as the app stores it.
5. Reads the stored rows back and checks: fights stored for the accepted server and none for the
   refused one, nothing quarantined, the queue not paused, no status-effect rows, heal / power /
   threat rows carrying their `resist` label, cure rows carrying a count, no zero-activity rows.
6. Stops the site, drops the database and deletes the token, whatever happened.

It tests the site code that is on disk in the EQ2Lexicon checkout, uncommitted changes included,
which is what you want when both halves of a change are in flight. Run it before releasing any
change to the upload path, and after any change to the site's ingest route.

**When the change adds something the script does not look at** (a new payload field, a new
endpoint), run it with `-Keep`, then extend the checks in the script: `readback` in
`scripts/tools/e2e_upload_helper.py` returns the stored rows as JSON and the script asserts on
them. A check that exists only in your head is not run next time.

**By hand, server half only.** To post one payload at the route without the app, the snippets in
`references/wire.md` ("Exercising the wire locally") mint a token and sign a body. They skip the
client's gzip, signing and queue, so they do not replace the script.
