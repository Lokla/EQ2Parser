# EQ2Parser — Engine Behaviour Reference

The authoritative description of how the Core engine behaves — algorithms,
thresholds, formulas, field names — in one place. Source comments across
`EQ2Parser.Core` cite this document by section (e.g. "§3") for the exact
counting rules, so the section numbering below is stable.

**Stat definitions are a contract.** The formulas in §3 (EncDPS, durations,
ally resolution, success level, the idle rule) feed the numbers shown in the
app and uploaded to EQ2Lexicon. They are kept **stable and consistent** on
purpose: changing a definition would fork the site's rankings into
incomparable eras, so improvements go into architecture, robustness,
performance and UX — never into a formula that moves a published number.
Correctness is validated by replaying real logs and checking the resulting
numbers against the community-standard values raiders expect.

**Design choices worth stating up front:**

- A shrunk/truncated log file is detected and the reader restarts from 0
  rather than stalling.
- Timing comes from the line's unix epoch prefix, not the bracketed
  local-time string — the epoch is unambiguous across locale/DST.
- Encounter identity is never index-based (no fragile "position in a list"
  handle that a mid-list insert can invalidate).
- One log source is parsed single-threaded (channel-fed), so consumers need
  no lock discipline.
- 1-second log granularity is disambiguated by a per-line monotonic
  `TimeSorter` — load-bearing for ordering and duplicate detection.

---

## Key capabilities (decided 2026-07-29)

1. **Multi-log parsing with encounter correlation** — the headline feature.
   One parse pipeline per log source: own tail reader, own grammar instance,
   own perspective state (owner name, "YOUR" resolution, zone, in-combat).
   An **encounter correlator** groups concurrent encounters across sources
   into one canonical encounter by zone identity + time overlap + shared
   enemy set, and keeps them separate when zones differ. Per-combatant
   authority within a merged encounter: a character's own log is
   authoritative for that character (an EQ2 log only fully records its
   owner); combatants no source owns go to the source that recorded them
   most completely. A configurable **primary character** scopes trigger
   audio/TTS/timers so multiple sources don't double-fire alerts.
2. **Catch-up on attach** — starting mid-raid rewinds a configurable window
   (or to the last zone change) and replays, instead of seeking to
   end-of-file and losing the current fight.
3. **Grammar as data** — the line grammar is pattern data per language
   (EN first, RU next), one engine for all languages.
4. **Trigger matching that scales** — source-generated regexes + a literal
   prefilter, not an every-trigger×every-line scan.
5. **Configurable magic numbers** — idle timeout (6 s), trigger audio
   rate-limit (1 s), spell-timer dedupe (2 s), sub-timer window (12 s) are
   all settings with sensible defaults.
6. **SQLite history from day one** — every swing queryable across sessions;
   encounter replay; survives crashes.
7. **Diagnostics as a feature** — structured event log, upload arrival
   verification, payload-size visibility.
8. **Live trigger reload + site trigger-pack subscription** — active
   triggers re-evaluate on edit (not only on zone change); trigger and
   spell-timer packs subscribe per-encounter from EQ2Lexicon.
9. **True owner death counts** (2026-08-04 — a deliberate, user-approved
   accuracy decision). In the owner's own log a real self-death is always
   second person ("has killed you."); a third-person death naming the owner
   can only be a temp pet dying under their name (e.g. the Templar hammer —
   verified: 82 own-name deaths were all hammer expiries vs 16 real
   "killed you" deaths, zero shape overlap). Own-name third-person kills are
   marked as pet deaths: excluded from Deaths / kill credit / success, shown
   separately in the death report. Third parties' deaths are left as-is —
   their shapes are genuinely indistinguishable from a single log
   (buff-fade correlation was tested and rejected: 44% coincidence vs 49%
   signal); per-combatant authority lets each character's own log correct
   their own count.

---

## 1. Log reading

- **Polling, not watchers** — no FileSystemWatcher. The reader thread checks
  `Length > Position` every 10 ms; a 1 s timer forces a read attempt
  regardless; a folder scan for a newer `eq2log*.txt` every 10 s handles
  rollover.
- Opens with `FileShare.ReadWrite`, seeks to end — history is never replayed
  on a live tail.
- Reads the whole pending tail, splits on CR/LF (empties removed), and holds
  back the final fragment unless the block ended with a terminator: **a line
  is never dispatched until its terminator is seen**.
- Default encoding UTF-8 (BOM detection can override). The timestamp prefix
  is stripped before trigger matching.
- Timestamp parse: the unix `(epoch)` prefix is authoritative; the bracketed
  `[Www Mmm dd hh:mm:ss yyyy]` local-time string is skipped (locale/DST
  ambiguous). `LastKnownTime` + a stopwatch give `LastEstimatedTime`
  (interpolated log time) — spell timers tick on THIS, not wall time.
- A file that has grown past its configured split size is treated as a fresh
  log (rollover).
- On attach mid-file, a zone look-behind scans a bounded window behind the
  start offset for the most recent "You have entered …" line (and its
  instance lockout), so zone-scoped timers and instance identity are known
  before the next zoning.

### Line pipeline (exact order, serialized under one lock)

1. Parse time; increment global `TimeSorter`.
2. **Idle-timeout check** (may end the current encounter — before anything
   sees the line).
3. Record the line into the active encounter's log (if in combat).
4. Evaluate **custom triggers** (timestamp-stripped text) — live lines only.
5. Chat trigger-share sniff (lines carrying `<Trigger …/>` / `<Spell …/>`).
6. Scripted-win say-line check (bosses that end by script, not death).
7. The grammar runs, turning the line into engine calls (`SetEncounter` +
   `AddSwing` per action) synchronously on the reader thread.

## 2. Encounter lifecycle

- **Explicit start contract**: `SetEncounter(time, attacker, victim)` is
  called before every action; `AddSwing` throws if not in combat.
  `SetEncounter` creates the encounter (and its `ZoneData`), fires
  combat-start, stamps `LastHostileTime`, sets `InCombat`.
- An encounter's official start time is set when its FIRST action arrives
  (not at `SetEncounter`).
- **Idle end rule**: in combat AND `now − LastHostileTime > idle limit` →
  end combat. Default **6 seconds**, on by default, checked on every new log
  line (log-time driven) with a wall-clock backstop at limit+2 s.
- **Silence cutting** (off unless configured): a gap larger than the limit
  closes the current time segment and opens a new one — the encounter gets
  multiple Start/End segments and dead air is excluded from Duration.
- `EndCombat`: drain pending actions → mark inactive → finalize (trim +
  title) → fire combat-end → history record → prune → exports.
- **Title** = "strongest enemy": among non-ally combatants, score
  `DamageTaken / max(Deaths, 1)` (integer division), take the highest. Until
  finalize the title is the placeholder "Encounter".
- **Success level**: 0 = indeterminate (no allies / no resolvable enemy);
  else `enemyDied` = strongest enemy has Deaths > 0, `allySurvived` = some
  ally with 0 deaths whose name has no space (the "is a player" heuristic
  used throughout). Both → **1** (win); one → **2** (partial); neither →
  **3** (loss). A curated scripted-win say line forces **1** for bosses that
  never die in the traditional sense.
- Zone changes do NOT end combat by themselves. A zone re-entered as a fresh
  instance (new lockout) is treated as a distinct zone run.

## 3. Stat definitions (the stable contract)

Object graph: Zone → Encounter → Combatant (keyed UPPERCASE name) →
damage-type buckets → AttackType (per ability + synthetic "All") → swings.

**Buckets** with ally-polarity values: outgoing — Auto-Attack(−1),
Skill/Ability(−1), Outgoing Damage(0), Healed(+1), Power Drain(−1),
Power Replenish(+1), Cure/Dispel(0), Threat(−1), All Outgoing (Ref)(0);
mirrored incoming set. Swing types: 1 melee, 2 non-melee, 3 healing, 10
power drain, 13 power replenish, 16 threat, 20 cure/dispel, plus our own 30
status effect (control-effect apply/release lines; no bucket link, so it files
only into the Ref buckets and carries no damage stats). **Outgoing
damage lands in TWO buckets** (its split bucket + aggregate "Outgoing
Damage"); incoming lands in one. Unknown swing types are silently ignored
except for the Ref buckets.

**Dnum** (damage value type): >0 real, 0 no-damage, −1 miss, −2 resist,
−3 parry, −4 riposte, −5 block, −6…−9 custom/unknown, −10 death. Traps:
long→Dnum conversion clamps <−10 to −9; equality compares the custom STRING
too (a `-1 "Dodge"` is NOT equal to Miss); sums ignore negative operands.

**Key stats** (per AttackType, cached incrementally):

- Damage = Σ swings > 0. Hits = count ≥ 0 (block-is-hit default) — a
  0-damage swing IS a hit. Crits require the hit threshold. Misses = count
  == Miss. "Blocked"/avoids = count in −2…−9 excluding death.
- Median = upper median of non-negative damages. MinHit uses the hit
  threshold; MaxHit strictly > 0.
- AverageDelay (default mode) = duration / (distinct timestamps − 1).

**Duration — the load-bearing definition**:

- Encounter StartTime = earliest outgoing action by ANYONE (any type — the
  Ref bucket).
- Encounter EndTime (default) = **the last DAMAGING swing by an ALLY**
  ("ShortEndTime": max over allies of their Outgoing Damage bucket end).
  Trailing heals/threat/enemy actions do NOT extend an encounter.
- Multi-segment (silence-cut) encounters sum segment durations; combatant/
  ability durations clip their own swing windows to the encounter segments.
- Combatant personal window = their own first→last outgoing action (Ref
  bucket); a combatant with only incoming actions has Duration 0.

**DPS family**: `EncDPS = Damage / encounter Duration` (this is what the
encounter tables and uploads show); `DPS = Damage / personal Duration`.
Healing mirrors as EncHPS. Encounter-level Damage/Healed sum over ALLIES
only. Allied deaths exclude space-containing names (pets/NPCs).

**Kills/Deaths**: from the "Killing" attack type (or death-coded swings).
Allies get kill credit for anything; non-allies only for space-free victim
names.

**Ally detection**: an incremental interaction graph — every action adds
the bucket's polarity (±1) between the two parties. Resolution seeds from
the player and sign-propagates until stable; allies = everyone whose final
sign matches the player's. **If the player isn't in the encounter, the ally
set is empty** → Damage 0, title "Encounter", success 0. (This is why an
observer-only log parses "empty".)

## 4. Triggers + timers

**CustomTrigger fields**: regex (compiled once at construction), category
(default "General"), restrict-to-category/zone flag, active, timer flag +
timer name, sound type (0 none / 1 beep / 2 wav / 3 TTS) + sound data,
tabbed + tab age. Key = `zone|category|regex`.

**Trigger share XML** — the format raiders paste into chat to hand triggers
around. This is the one place interop with ACT matters: the `<Trigger …/>`
/ `<Spell …/>` shape is ACT's, so a snippet copied from either tool imports
into the other. Trigger attributes, in order: `R` regex (escaped; `#`→
`&#35;`, `\\`→`&#92;&#92;`, `\s`→`&#92;s`), `SD` sound data, `ST` sound type
int, `CR` restrict T/F, `C` category, `T` timer T/F, `TN` timer name,
`Ta` tabbed T/F. On import `R` + `C` are required (they form the identity
key; existing triggers update in place); other attributes are individually
optional. Booleans are exactly "T".

**Evaluation**: independent of parsing, against the timestamp-stripped line,
EVERY active trigger per line, on a low-priority path. Match semantics: a
group literally named `YOU` must contain the player's name or the match is
dropped; a timer action calls the timer runtime (attacker/victim groups
default "None", Self forced true); audio is rate-limited to 1/second per
trigger; TTS text expands `$1` / `${name}` from the match. Imports and
replayed history never fire audio/timers. Restrict-to-category is a
case-insensitive SUBSTRING test of the category against the current zone
(instance numbers stripped), rebuilt on zone change.

**Spell timer definition fields** (share format `<Spell …/>`): `N` name,
`T` duration s (default 30), `WV` warning s (10), `RV` remove-at s (default
−15; negative lingers past zero), `A` absolute (one-only), `R`
restrict-to-me, `RC` restrict-to-category, `OM` only-master-ticks, `M`
modable, `RD` radial, `FC` ARGB colour, `Tt` tooltip, `C` category, optional
`SS`/`WS` start/warning sounds. Key = zone-qualified lowercase
`zone|category|name` (the same ability in two zones is two distinct timers).

**Runtime timer semantics**: `Notify(attacker, spell, self, victim, time,
zone)` is the single entry point — every combat action calls it with the
ability name, and triggers call it too. Selection prefers a definition whose
category matches the acting boss, then the current zone, so same-named
abilities across zones resolve correctly even before a zone line is seen.
Re-trigger within 2 s of the frame's newest timer → absorbed (tick chain);
within the sub-timer window → sub-bar (no sound reset); a hit after real
silence, or after the countdown expired, → new master timer (sounds reset).
Timers tick on interpolated log time. Recast/haste mods sum de-duplicated by
name; final = base × (1 + Σ mods). Owner death within 2 s of start removes
their mods; a dispel within 1 s drops the debuff mod.

## 5. Engine API surface

The grammar module turns each parsed line into calls on a small engine API:
`SetEncounter` then `AddSwing(swingType, critical, special, attacker,
attackType, dnum, time, timeSorter, victim, damageType)`. `special` is
normalized ("hit"/"hits"/blank → "None"); names are trimmed and upper-cased
for keying; "Unknown" (dumbfire/ground-effect damage with no real source) is
excluded from ally bookkeeping; the space-in-name heuristic marks NPCs/pets.
These heuristics are inherited from 20 years of EQ2 log conventions, not from
any one tool — the log format is the shared reality every EQ2 parser reads.

## 6. Concurrency model

One **writer** (each log source's pump thread) runs against many **readers**
(the parse grid, the mini-parse and timer overlays, clipboard/Discord export,
compare, reports, uploads, the history save). The combat model — `Encounter`,
`Combatant`, `Bucket`, and the `EncounterCorrelator` — is deliberately **not
internally synchronized**: it is plain mutable state with no locks of its own.
All thread-safety comes from a single lock.

**The one lock — `SourceManager.Sync`.** Everything that touches live encounter
data takes it:

- **Writer.** Every parsed line is applied under the lock —
  `LogSource` does `lock (Sync) { Processor.Process(line); }`. All mutation of
  encounters/combatants/buckets and all correlator create/merge/delete/restore
  is serialized through that one gate, so there is never more than one writer
  inside the model.
- **Readers.** Each consumer takes the same lock, copies what it needs into an
  **immutable snapshot record**, and releases the lock before rendering. The
  parse grid (`RebuildTree` / `SnapshotFight`), the ~4 Hz mini-parse overlay
  (`MiniParseSnapshot`), export / compare / reports, and the Sources status
  line all follow this shape. Readers hold the lock only long enough to
  snapshot, so the critical section stays short and the UI never observes a
  half-applied update — no torn reads, no "Collection was modified".

**The invariant.** Because the model does not guard itself, *any* code that
reads the live graph (`Engine.ActiveEncounter`, `Engine.History`,
`Correlator.History`, or a `CorrelatedEncounter`'s combatants) MUST hold
`SourceManager.Sync` and snapshot before use. New consumers follow the same
pattern; helper methods that assume the caller already holds the lock
(`GroupHistoryZones`, `LiveTable`, `LiveSummary`) say so in their doc comment.

**Outside the live model.** The archive (`HistoryService`) is a separate
SQLite-backed subsystem with its own lock; fights it loads or mines (e.g.
`AbilityMiner` on a background task) are fresh objects deserialized from disk,
not the live ones the pump is mutating, so they carry no race with the writer.
It has ONE connection to `history.db`, and every user of it — the background
writer saving finished fights, the UI thread searching, loading and purging,
the pump thread collapsing mirrors — takes that lock around each call. They
queue on the lock, so SQLite never sees two of them at once. A statement waits
at most two seconds for a database that ANOTHER process holds locked
(`HistoryStore.BusyTimeoutSeconds`, set as the connection's default timeout;
the provider's own default is thirty) and then fails as busy. That bound is
what a locked file costs at startup, where the store is opened on the UI
thread; it never applies between the app's own threads, and it limits waiting
only, never how long a statement may run.

**Per-line fault isolation.** The pump treats each line as its own unit of
failure (`LogSource.PumpLine`), in three sizes:

- **An alert handler throws.** `LogLineProcessor.Process` runs the alert side
  of a line — trigger evaluation and its `Fired` handlers, trigger-share
  detection, the `RaidLine`, `StatusApplied` and spell-timer notifications — in
  the same pass as the grammar, some of it before the grammar. Each of those
  stages (`AlertStage`) has its own guard: a throw is counted and handed to
  `LogLineProcessor.AlertFaulted`, and the rest of the line still runs, so a
  bug in a handler can cost an alert but never the line's damage, heal or
  death. The line counts as processed. `LogSource` writes a `log-alert` crash
  report (at most five per source) and an `alert handler FAILED` journal line
  (the first, then at most one per ten seconds), and counts it in
  `LogSource.AlertFaults`, which the Sources row shows after its counters; it
  is not a skipped line and never counts toward stopping the source. Inside
  the `RaidLine` stage the three raid consumers (roster tracker, DKP progress,
  loot tracker) each run in their own guard
  (`SourceManager.RunRaidLineConsumers`), so one that throws does not cost the
  other two that line; what failed is then rethrown, so it is still reported
  as one raid-line alert fault, with the line quoted.
- **The line itself throws** (the parse helpers, the idle rule's end-of-fight
  announcement, the grammar, `SetEncounter` / `AddSwing`). The line is skipped
  and the tail reads on: the engine is only mutated under the lock, the unwind
  releases it, and every step either completes or leaves a state the next line
  handles. Evidence: a `log-line` crash report quoting the line (five per
  source), a `log line skipped` journal line, `LogSource.SkippedLines`.
- **The source stops**, in two cases only: 200 lines in a row failed
  (`LineFaultBreaker`, a structural fault rather than a bad line), or the tail
  reader itself failed. Evidence: a `log-source-stopped` or `log-reader` report
  and a `source STOPPED` journal line. Before `LogSource.Error` is set the
  source ends its fight in progress through `ParserEngine.EndCombat`, so that
  fight is announced once and saved like any other instead of staying "in
  combat" for the rest of the session. It holds everything parsed before the
  stop and nothing invented; what it lacks is the lines that failed or were
  never read. `Error` is set last: whoever sees it finds the evidence written
  and the fight ended. The same publication carries the reason as facts
  (`LogSource.Stopped`: reader or breaker, the count, the exception
  underneath), which is what the Sources row words in the interface language;
  the exception's own message stays English for the crash report. A stopped
  source reports no pending bytes (`LogSource.PendingBytes`): nothing will
  read its log again, and counting the growing gap as backlog held
  `SourceManager.ImportBusy`, and with it the main page and the overlays, in
  import mode for the rest of the session.

**A source that goes away mid-fight.** The idle rule only runs when the next
line arrives, so a source that is removed, or is tailing when the app closes,
would leave its fight unended: never archived, never uploaded. Both end it
through the same call a stopping source uses (`LogSource.EndFightInProgress`),
in this order:

1. The pump is stopped (`LogSource.StopPump`: cancel, then wait at most two
   seconds). A pump finishes the read batch it is on before it ends, so once
   it has stopped, every line up to its position is in the engine and none
   after it.
2. The fight is ended under the lock while its consumers are still attached.
   `SourceManager.Remove` ends it and detaches the handlers inside one lock;
   `SourceManager.Dispose` ends every source's fight before it disposes
   anything, and never detaches at all.
3. On exit only: `App.OnExit` stops the pumps BEFORE it saves the resume
   positions, so the saved position is the last line that fight holds. Then
   the archive is disposed, which gives its writer up to five seconds to
   drain the queue (the fight just ended is in it) before the exit snapshot,
   and the upload queue, which cancels its drain and waits two. The upload of
   that last fight may well not complete: the queue is in memory, and it has
   had only as long as the archive took to close. The fight is in the archive
   and can be uploaded from the fight tree later.

No wait was added to the exit: steps 1 and 2 are the old "dispose the sources"
taken apart. A fight ended this way is not flagged. It is the fight a
link-death mid-pull leaves, which the idle rule has always ended and saved
unmarked, and its numbers are computed like any other's; the journal says it
happened (`exit: fight in progress ended`, `source removed: ...`).

**Fight-consumer fan-out.** `ParserEngine.EncounterEnded` fires on the pump
thread, under the lock, from inside the line that ended the fight. Every source
subscribes the same handler, `SourceManager.OnEncounterEnded`, which runs the
consumers in a fixed order — correlator (`EncounterCorrelator.Accept`), archive
(`HistoryService.QueueSave`), upload (`UploadService.OnEncounterEnded`), raid
roster — each inside its own guard (`SourceManager.Isolated`). A consumer that
throws leaves a `fight-consumer` crash report and a `fight consumer FAILED`
journal line; the consumers after it still run and the line is not skipped. As
chained delegates the first to throw starved the rest and failed the line. The
correlator's `Merged` event, which does archive work, goes through the same
guard. Two of the consumers only enqueue: the archive write and the upload
happen on their own background threads, outside the lock, and report their own
failures (`archive-save` reports and `HistoryService.SaveFailures`; the upload
status line).

This is the same guarantee ACT achieved with its internal table locks — a
single serialization point that keeps the combat data thread-safe across many
consumers — implemented here as one App-level lock plus copy-on-read snapshots.
