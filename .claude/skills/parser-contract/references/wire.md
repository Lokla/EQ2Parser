# Wire facts a change must not break

Every statement here names the symbol that enforces it. Read the symbol before relying on the
sentence: this file describes the code, it does not replace it.

Parser code is under `src/EQ2Parser.Core/Upload` and `src/EQ2Parser.App/Services`. Server code is in
the sibling checkout, written as `EQ2Lexicon/...`.

## Endpoints the parser calls

| Request | Auth | Parser caller | Server handler | Client on failure |
| --- | --- | --- | --- | --- |
| `POST /api/parses/ingest` | bearer, signature, gzip | `LexiconUploadClient.UploadAsync`, driven by `UploadQueue` | `ingest_parse` in `EQ2Lexicon/backend/server/api/parses/ingest.py` | network error or 5xx: retry after 5 s, then 30 s. 429: wait the response's `Retry-After` (held to 1 to 120 s), or 30 s when it has none, and retry, inside the same three attempts. 401, or 403 without `X-Lexicon-Reject`: pause the queue. 403 with that header: fail this upload and carry on. Any other status: drop the fight, show `detail`. No client at all (the token was cleared with fights queued): nothing is sent, the fight is dropped at once. See "Rate limits and Retry-After" and "Refusals and the reject header". |
| `POST /api/attendance/ingest` | bearer, signature, gzip | `LexiconUploadClient.UploadAttendanceAsync`, from `UploadService.TickAttendance` | `ingest_attendance` in `EQ2Lexicon/backend/server/api/attendance.py` | result ignored |
| `GET /api/attendance/mains?character=&server=` | bearer | `LexiconUploadClient.FetchRaidMainsAsync` | `get_raid_mains` in the same module | null, so DKP falls back to the bulk grant |
| `GET /api/auth/whoami` | bearer | `LexiconUploadClient.WhoAmIAsync` (token test, Raid-tab entitlement probe) | `whoami` in `EQ2Lexicon/backend/server/api/auth_tokens.py` | status 0 means "unknown, probe again"; any HTTP error means "no access" |
| `GET /api/zones/raid-bosses` | none | `RaidBossSyncService.SyncAsync` | `list_raid_bosses` in `EQ2Lexicon/backend/server/api/zones.py` | keep the last list |
| `GET /api/act/pack` | none | `LexiconSyncService.SyncAsync` | `get_act_pack` in `EQ2Lexicon/backend/server/api/act/pack.py` | keep the cached pack |

The base URL is `AppSettings.LexiconBaseUrl`. Released parsers carry its default host, so that host
must keep serving these paths.

The ACT plugin posts to the same ingest route with the same signing rules. A server change has to
keep it working too; `EQ2Lexicon/tests/server/_parses_ingest_fixtures.py` holds an ACT-shaped payload.

## Signing, compression and headers

- Order is fixed: serialise once (`LexiconPayload.ToJson`), sign that string, gzip the same bytes.
  `LexiconUploadClient.PostSignedAsync` does all three from one `json` variable.
- The signature is lowercase hex HMAC-SHA256. Key: the UTF-8 bytes of the API token. Message: the
  UTF-8 bytes of the JSON before compression. `LexiconUploadClient.Sign` computes it;
  `_validate_payload_signature` recomputes it over the body that `GzipRequestMiddleware`
  (`EQ2Lexicon/backend/server/core/gzip_request.py`) has already inflated. Re-serialising,
  re-encoding or adding a byte-order mark between signing and sending gives a 401.
- Headers: `Authorization: Bearer <token>`, `X-Lexicon-Signature`, `Content-Encoding: gzip`,
  `Content-Type: application/json; charset=utf-8`, `User-Agent: EQ2Parser/<major.minor.patch>`.
- Response header `X-Lexicon-Reject` (`REJECT_HEADER` in the ingest module, mirrored by
  `LexiconUploadClient.RejectHeader`): the server sets it, with a short lowercase code, on a refusal
  that is about this one upload and not about the caller's credentials or account. Only 403s carry
  it. The client reads it into `UploadResult.RejectCode`; a blank value counts as absent.
- A token request without the signature header is a 401. Mains and whoami are bearer-only.
- Tokens start with `eq2c_`; the server looks up their SHA-256 (`TokensStore.lookup_api_token`).
  A token from an account that is not approved is a 403, without the reject header.
- Size: the raw body may be at most 12 MiB and the inflated body 16 MiB, else 413. Bad gzip is a 400.
  Plain bodies are also accepted.
- The User-Agent is the only client identification. There is no version field in the body and no
  version gating. `client_throttle.match` (`EQ2Lexicon/backend/server/core/client_throttle.py`)
  prefix-matches it against `INGEST_CLIENT_LIMITS`, so renaming the product token changes which
  budget applies. The two public GETs go through a different `HttpClient` and send no User-Agent.
- The client waits 60 s. The server's response path never waits on Census.
- Rate limits are per token: 60 a minute on parse ingest, 30 a minute on attendance ingest. See
  "Rate limits and Retry-After".
- `LexiconUploadClient.UrlProblem` refuses plain http except for loopback, so the token never
  travels in clear text.

## Rate limits and Retry-After

Parse ingest can answer 429 from two places, and only one of them says how long to wait.

| 429 from | Body | `Retry-After` |
| --- | --- | --- |
| The per-token limit: the `@limiter.limit` decorator on `ingest_parse`, answered by `_rate_limit_handler` in `EQ2Lexicon/backend/server/app.py` with slowapi's stock response | an `error` key and no `detail`, so the client's message is `Server responded 429` | none: the limiter in `EQ2Lexicon/backend/server/limiter.py` is built without `headers_enabled` |
| The per-client flood budget: `_client_flood_gate` in the ingest module, counted by `EQ2Lexicon/backend/server/core/client_throttle.py` | a `detail` naming the budget | delta-seconds, at least 1 (`ThrottleVerdict.retry_after`); `_http_exception_handler` keeps the header |

The flood budget applies only to a User-Agent prefix named in `INGEST_CLIENT_LIMITS`
(`EQ2Lexicon/backend/server/config.py` holds the default; the deployment may set its own). Read it
to see whether a prefix matches `EQ2Parser/<version>` today. A proxy in front of the site may add
the header, in either form, to a 429 of its own.

The client reads the header in `LexiconUploadClient.ReadRetryAfter`, in both forms HTTP allows
(delta-seconds and an HTTP-date), into `UploadResult.RetryAfter`. A header that does not parse, or a
date that is not in the future, reads as none. `UploadQueue.RateLimitWait` then decides the wait:
the header's value held between `MinRetryAfter` (1 s) and `MaxRetryAfter` (120 s), or
`RateLimitDelay` (30 s) when there is none. The drain is held for the whole wait, `Dispose` cancels
it, and a 429 still draws on the same three attempts as a transient failure. Only a 429 is treated
this way: a `Retry-After` on any other status is read and not used.

## Encodings in the parse payload

| Thing | Parser | Server reads it with |
| --- | --- | --- |
| Keys | lowercase ACT names in `[JsonPropertyName]`; null members are omitted | field names on the `Ingest*` models |
| Timestamps | `PayloadBuilder.Ts`: `yyyy-MM-ddTHH:mm:ssZ`, UTC | `_to_ts`; a naive string is read as UTC |
| Booleans | `"T"` or `"F"` (`PayloadCombatant.Ally`) | `_to_bool_tf` |
| Percentages | numbers from 0 to 100 | `_to_perc`, which also takes ACT's `"93%"` strings |
| Rates | numbers rounded to 2 places (`PayloadBuilder.Rate`) | `_to_float` |
| `encid` | 8 lowercase hex characters, a hash of the fight (`PayloadBuilder.ComputeEncId`) | `IngestEncounter.encid`, 1 to 16 characters |
| `logger_name` | the log file's character name (`LogSource.DeriveOwner`) | `CHARACTER_NAME_RE`: letters only, 1 to 15 |
| `logger_server` | the log's parent directory (`LogPaths.ParseServerName`); never empty on the wire, and never a name that fails `LogPaths.IsServerNameAccepted` (see below) | required, `WORLD_NAME_RE` through `sanitize_world`, then `ALLOWED_SERVERS` |

The `_to_*` helpers are in `EQ2Lexicon/backend/server/parses/models.py`; the name patterns are in
`EQ2Lexicon/backend/server/core/validation.py`.

**A log outside a server folder is not uploaded.** `ParseServerName` returns an empty string for a
log that sits directly in the game's `logs` folder instead of `logs/<server>/`. The server has no
default world and answers 400 to an empty `logger_server`, so the parser sends nothing for such a
log. `UploadService.ShouldAutoUpload` skips the fight, counts it (`SkippedNoServer`) and puts the
reason in the status line (string `UploadSvc_NoServerFolder`). `UploadService.UploadFight` refuses
a manual upload of it with the same text. `UploadQueue.Enqueue` drops a blank server as the
backstop. `UploadService.BuildAttendancePayload` returns null, so neither the attendance snapshot
nor the mains request is made. Parsers up to v0.5.7 send the empty value and get the 400 for every
fight.

**A log in a folder that is not named like a server is not uploaded either.** The site checks the
shape of `logger_server` before it looks at the allow-list: `sanitize_world` strips surrounding
whitespace and matches `WORLD_NAME_RE`, a letter followed by up to 30 letters, digits, spaces,
apostrophes, underscores or hyphens (ASCII, either case, 1 to 31 characters). Anything else is a
400, "logger_server '…' is malformed.", for every fight from that folder. The parser holds the same
pattern as `LogPaths.ServerNamePattern` and asks `LogPaths.IsServerNameAccepted`, which trims the
same way. A folder that fails it (`logs/2nd account/`, a name over 31 characters) is handled like
the missing folder: `ShouldAutoUpload` skips and counts it (`SkippedServerName`) with its own reason
(string `UploadQ_ServerFolderName`), `UploadFight` refuses it, `Enqueue` drops it and
`BuildAttendancePayload` returns null. The length is the pattern's. The request model's
`max_length` on the field is a separate number: read both before relying on either. Parsers up to
v0.5.7 send such a name and get the 400.

## What parse ingest checks

Roughly in this order; the status is what the parser sees.

1. Per-client flood budget (`_client_flood_gate`): 429.
2. Body schema (`IngestRequest`): 422 when `logger_name`, `encounter.encid`, `title`, `starttime` or
   `endtime` is missing, when a list is over its cap (512 combatants, 8000 damage types, 16000
   attack types) or when `client_warnings` has more than 32 entries.
3. Token (401), account approval (403), signature (401).
4. `logger_name` shape: 400.
5. `logger_server`: missing or empty is a 400, malformed is a 400, not on the allow-list is a 403
   that carries `X-Lexicon-Reject: server-not-allowed`. There is no fallback to a default world on
   this route. The accepted value becomes the row's world.
6. Unparseable `starttime` or `endtime`: 400.
7. Plausibility (`evaluate`). REJECT is a 400: a negative duration, damage or encdps, start after
   end, a start before 2015 or more than a day ahead, a duration longer than the timestamps allow,
   negative ally damage, or a percentage outside 0 to 100. QUARANTINE is a 201 with
   `status: "quarantined"` and no encounter row: a fight over two hours, an absurd rate, or one ally
   with more damage than 105% of the encounter total.
8. No named combatant: 400.
9. Uniqueness. Damage types are unique on (combatant, type) and attack types on (attacker,
   swingtype, type). Identical repeats collapse; repeats that differ are a 422
   (`_collapse_duplicate_rows`). Combatant names are unique per encounter. Attack rows named `All`
   are dropped.
10. Idempotency on (`encid`, world): a repeat is a 201 with `status: "skipped"` and a null
    `encounter_id`. It never un-hides a moderated parse.
11. Claim binding (`_uploader_claimed`): the upload is stored either way. Without an approved claim
    on the logger it is `uploader_verified = 0`, has no guild and never ranks.

## Refusals and the reject header

What the client does with a refusal is decided in `UploadQueue.SendWithRetryAsync`:

- **401** always pauses the queue. It is the token, or the signature made from it.
- **403 without `X-Lexicon-Reject`** pauses the queue. That is the account-level refusal (the auth
  layer's "Account not approved") and everything a server older than the header sends. The status
  line shows the server's `detail`.
- **403 with the header**, whatever the code: that one upload fails with the server's `detail` in
  the status line, and the queue carries on. The client keeps no list of known codes, so the site
  can add one without a parser release.
- **The code `server-not-allowed`** (`LexiconUploadClient.RejectServerNotAllowed`) does one thing
  more. The queue stops sending for that `logger_server` for the rest of the session and counts
  what it holds back (`SkippedRefusedServer`), so an alt on a server the site does not accept
  cannot spend the token's upload budget. That memory is cleared whenever
  `UploadService.Configure` runs (`UploadQueue.ConfigurationChanged`) and on a manual upload
  (`ForgetRefusedServers`).
- A pause ends when `UploadService.Configure` runs (`ConfigurationChanged`), when the Test button
  succeeds, or when the user uploads a fight by hand (`ResetAuthPause`). Code reads the pause as
  `UploadQueue.AuthPaused` or `UploadService.UploadsPaused`. The status line is translated, so no
  word in it is a signal.
- **An answer from before a reconfigure is stale.** `ConfigurationChanged` starts a new
  configuration generation, and a send notes the generation it went out under. When a 401 or 403
  comes back to a send from an older generation, it was the old token's or the old site's answer:
  it does not pause, does not mark a server refused and does not fail the upload. The upload is
  sent again under the current configuration, outside the retry budget. `Configure` swaps the
  client first and calls `ConfigurationChanged` second; that order is what makes every answer from
  the old client carry an older generation.
- **No client is not a failure.** When the token is cleared, or the site address fails
  `UrlProblem`, `UploadService` has no client and its send returns `UploadResult.NoClient` with the
  reason. Nothing is sent. The queue drops the fight at once, counts it (`SkippedNotConfigured`)
  and shows the reason; it does not retry, and it does not hold the fight for a later token, which
  might belong to another account. The fight stays in local history and can be uploaded by hand.

The status lines themselves come from `UploadQueueMessages` in
`src/EQ2Parser.Core/Upload/UploadQueueMessages.cs` (English, and what the Core tests assert). The
app passes `UploadService.LocalizedUploadMessages`, which reads each one from an `UploadQ_*` string
in `src/EQ2Parser.App/Localization/strings.en.json` and its three siblings; the site's `detail` and
status word are inserted as they arrived. A new line is a new method there, an override in the
app's class and a key in all four dictionaries
(`tests/EQ2Parser.App.Tests/UploadServiceStatusLanguageTests.cs` fails when one is missed).

Codes the server sends:

| Code | Route | Refusal |
| --- | --- | --- |
| `server-not-allowed` | parse ingest | `logger_server` is not on the allow-list |
| `server-not-tracked` | attendance ingest, mains | the server is not on the allow-list |
| `no-claim` | attendance ingest | the account has no approved claim on the logging character |
| `no-guild` | attendance ingest, mains | the logging character has no guild |

Left untagged on purpose: "Account not approved" and the attendance `subscriber` gate
(`_ensure_subscriber`). No other request from that account could succeed, so a pause is right.

Only parse uploads go through `UploadQueue`. The attendance result is discarded and a failed mains
fetch is null, so the three attendance codes change nothing in the client today. Parsers up to
v0.5.7 never read the header and pause on every 401 and 403.

## What the server does with fields

- The row models (`IngestCombatant`, `IngestDamageType`, `IngestAttackType`) give every field a
  default and allow unknown keys. `IngestRequest` and `IngestEncounter` ignore unknown keys. Either
  way a key that the `_*_from_payload` functions do not map is not stored.
- A missing number becomes 0. `dps` falls back to `encdps` (`_dps_or_encdps`). `critperc` falls
  back to `crithits / hits` (`_crit_perc_or_computed`). A `dps` of 0 sent on purpose is replaced too.
- `client_warnings`: over 32 entries rejects the whole upload (422). Entries are trimmed, cut to
  64 characters and de-duplicated in `_insert_encounter_rows_sync`, stored as JSON text and shown
  as raw strings to admins. The values are fixed identifiers (constants in
  `src/EQ2Parser.Core/Upload/LogProvenance.cs`). Privacy rule: a warning never carries a process
  name, a path, a user name or anything else read from the user's machine.
- `success`: readers treat exactly 1 as a kill (rankings, fight primaries, retention). Other values
  are only labelled.
- `swingtype`: readers select rows by value (`_DAMAGE_SWING_TYPES` and its siblings in
  `EQ2Lexicon/backend/server/parses/db.py`). Threat is the one value the two clients number
  differently, and `_THREAT_SWING_TYPES` selects both: 100 (the ACT plugin's) and 16 (the parser's
  `SwingCategory.Threat`, which is ACT's own number). A value no reader selects is stored and never
  shown: 13 (power replenish) today. The parser's own 30 (status effects) is not uploaded.
- Attack rows are one per (attacker, ability name, `swingtype`), which is the site's uniqueness key.
  An ability that logs in two categories (damage and a heal, an ability and its threat) is two rows,
  each computed only from that category's swings (`SplitByCategory` in `PayloadBuilder`). Builds up
  to v0.5.7 sent one merged row per name under the first swing's category, which put heal and threat
  amounts on the Damage tab; rows already stored from those builds stay as they are.
- `resist` on an attack row is what the site shows as the heal type and the threat kind: "Hitpoints"
  or "Absorption" (a ward) on heals, "Increase" or "Decrease" on threat, "Power" on power replenish,
  and the damage type on a damage row. It is sent only when every swing in the row agrees, otherwise
  omitted (`ResistOf`).
- A cure row carries `damage` = the number of cure lines, because the Cures tab reads it as effects
  removed. The engine's amount for a cure swing is still 0; this is the payload only.
- A row with no damage, hits, misses or blocks is not sent (the ACT plugin's rule). In practice that
  is the "Killing" marker; kills and deaths reach the site in the combatant columns.
- A combatant row is not sent for a combatant that exists only through swings the upload leaves
  out (`HasUploadedSwing` in `PayloadBuilder`): every swing it made or received is a status-effect
  swing. That is the "Unknown" attacker of "X is stunned!", a mob that was only ever stunned, a
  name whose only logged act was a silence. Builds up to v0.5.7 sent such a row with every figure
  zero. Allies always keep their row, and so does any combatant that healed, was healed, was hit or
  attacked, died or cured, so no `damage_types` or attack row is left without its combatant. The
  combatant stays in the encounter: `encid` is hashed from the encounter's own combatant count
  (`ComputeEncId`), so the same fight keeps the id it had.

## Attendance ingest

- Model `AttendanceIngestRequest`: `logger_name`, `logger_server` and `sent_at` are required.
  `raid_members` holds at most 100 entries, `online_guildies` 1000, `zones` 20. A member is
  `name`, `first_seen`, `last_seen` in unix seconds.
- Stricter than parse ingest: 403 without the `subscriber` role (admins pass), 403 unless the token's
  account has an approved claim on the logger (admins pass), 403 when the logger has no guild, 503
  when the guild cannot be resolved. A server off the allow-list is a 403 here too. All of these
  403s except the role gate carry `X-Lexicon-Reject` (see "Refusals and the reject header").
- 400 when no member survives cleaning, 422 when the snapshot spans more than 12 hours. Times are
  clamped to between 36 hours ago and 1 hour ahead.
- The client sends its whole session state every five minutes, and only while
  `UploadService.AttendanceAccess` is true, the roster tracker is armed, someone is in the raid and
  the logging source's log is in a server folder.

## Exercising the wire locally

These support the end-to-end check in `SKILL.md`. Each is (unverified): written from the code, not
yet run. Keep them in a scratch directory and never commit a token.

**Mint a local token.** Run from the EQ2Lexicon root with `DATABASE_URL` set to the local scratch
database, after the backend has started once (it creates the schemas): `uv run python <scratch file>`.

```python
import asyncio

from backend import pg
from backend.server import db as users_db


async def main() -> None:
    # open_signup=True makes a brand-new user 'approved'; an unapproved account's token is a 403.
    await users_db.upsert_user("local-contract-check", "Local Check", "localcheck", None, open_signup=True)
    raw, _row = await users_db.mint_api_token("local-contract-check", "contract check")
    print(raw)  # shown once; only its hash is stored


pg.ensure_selector_event_loop_policy()
asyncio.run(main())
```

**Post a payload by hand.** `python <scratch file> <payload.json> <token>`. It signs and sends the
file's exact bytes, uncompressed, so it tests the server against the parser's JSON and nothing else.

```python
import hashlib
import hmac
import sys
import urllib.error
import urllib.request

body = open(sys.argv[1], "rb").read()
token = sys.argv[2]
request = urllib.request.Request(
    "http://localhost:8000/api/parses/ingest",
    data=body,
    method="POST",
    headers={
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json",
        "X-Lexicon-Signature": hmac.new(token.encode("utf-8"), body, hashlib.sha256).hexdigest(),
        "User-Agent": "EQ2Parser/0.0.0",
    },
)
try:
    with urllib.request.urlopen(request) as response:
        print(response.status, response.read().decode("utf-8"))
except urllib.error.HTTPError as error:
    print(error.code, error.read().decode("utf-8"))
```

**Read the stored row.** Table and column names are from `EQ2Lexicon/db/migrations/0002_parses.sql`
and `EQ2Lexicon/db/migrations/0015_parses_uploader_verified.sql`.

```sql
SELECT id, world, act_encid, title, zone, duration_s, success_level,
       uploaded_by, uploader_verified, guild_name, client_warnings
FROM parses.encounters ORDER BY id DESC LIMIT 3;

SELECT c.name, c.ally, c.is_player, c.dps, c.encdps, c.enchps
FROM parses.combatants c WHERE c.encounter_id = <id> ORDER BY c.damage DESC;

SELECT c.name, a.swing_type, a.attack_name, a.damage, a.encdps, a.dps, a.crit_perc, a.hits, a.crit_hits
FROM parses.attack_types a JOIN parses.combatants c ON c.id = a.combatant_id
WHERE c.encounter_id = <id> ORDER BY a.damage DESC LIMIT 20;

SELECT id, reason, act_encid, title FROM parses.tamper_reports ORDER BY id DESC LIMIT 3;
```

To tell a stored value from a server fallback, pick a row where the two differ: an attack whose
`dps` is not its `encdps`, or one whose `crit_perc` is not `100 * crit_hits / hits`.
