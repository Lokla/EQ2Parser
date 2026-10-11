# Add or fix a line shape

The grammar is `src/EQ2Parser.Core/Grammar/EnglishGrammar.cs`: one static class,
`EnglishGrammar`, whose `TryParse(message)` turns one log message (timestamp prefix already
removed) into a `GrammarEvent` or null. Everything here was checked against that file; name
symbols when you talk about it, because line numbers move.

## Which change is it

| The line is... | What changes |
|---|---|
| a variant of a shape already parsed (a new verb, outcome word, status word, an optional word) | A table or an alternation in `EnglishGrammar`, in every place listed under "Tables that change together" |
| a new shape that still means damage, a heal, a ward, power, threat, a cure, a status or a death | A new `[GeneratedRegex]` member, a branch in `TryParse`, and usually an existing builder (`Damage`, `Heal`, `Ward`, `Status`, ...) |
| something that needs its own stats | All of the above plus a new `SwingCategory`: use the `parser-checklists` skill (swing category) first, it is a stat-engine change |
| not a combat action at all (like the zone and lockout lines) | A new event type: see "A new event type" |

Most requests are the first two. Reach for a new event type only when the engine has to do
something a swing cannot express.

## Before editing

1. Collect the variants with `scripts/tools/logmine.ps1`, not just the one line in the bug
   report: third person and second person, critical and not, an amount large enough to be
   abbreviated (`140.6K`, `1.2M`), an attacker whose name ends in `s` (`Name' Ability`), names
   containing spaces and apostrophes, and a pet or weapon acting for its owner. Use
   `-Templates -CollapseNames -Pattern '<a literal from the line>'`.
2. Find out what the grammar does with the line today. Write the test first (see "Tests") and
   read the failure: null means the line falls through unmatched; a non-null result with the
   wrong attacker, ability or victim means an existing branch already claims it (a phantom
   parse), and your branch has to come before that one.

## How `TryParse` dispatches

- Every pattern is a `[GeneratedRegex]` partial method on the class, anchored `^...$`, with
  named groups (`attacker`, `ability`, `verb`, `victim`, `crit`, `amount`, `school`,
  `outcome`, `outcomeactor`, `effect`, `killer`, `zone`, `remaining`, `direction`).
- `TryParse` tries families in a fixed order. Each family sits behind a cheap literal test
  (`EndsWith`, `Contains`, `StartsWith`, all `StringComparison.Ordinal`). **The first regex
  that matches returns.** A family that matches nothing does not return null; the line
  carries on to the families below it.
- A branch that carries a number has a second condition. Its builder returns null when the
  amount cannot be represented (see "Amounts"), and the branch then falls through exactly as
  if its pattern had not matched.
- A guard may only skip work. It must be a literal that every line the family's regexes can
  match provably contains, so removing the guard would change speed and nothing else.

The order today:

| Guard | Patterns tried, in order |
|---|---|
| ends ` damage.` and ends ` but fails to inflict any damage.` | `YourNoDamage`, `PossessiveNoDamage`, `PlainNoDamage` |
| ends ` damage.` otherwise | `YourAbilityDamage`, `YouAutoDamage`, `AnonymousHit` (own guard ` is hit by `), `PossessiveAbilityDamage`, `PlainDamage` |
| contains ` tries to ` | `AvoidLine` |
| contains ` heals ` | `YourHeal`, `PossessiveHeal` |
| starts `YOU try to ` | `YouAvoid` |
| contains ` absorbs ` | `YourWardAbsorb`, `WardAbsorb` |
| contains ` refreshes ` | `YourPowerRefresh`, `PowerRefresh` |
| contains ` hate with ` | `YourThreatChange`, `ThreatChange` |
| contains ` relieves ` | `YourCureRelieves`, `CureRelieves` |
| ends `!` | `StatusApplied`, `StatusFrozen`, `StatusTerrified`, `StatusGloom` |
| contains ` is no longer ` | `StatusReleased`, `StatusAffectedReleased` |
| contains ` silences ` | `StatusSilences` |
| contains ` regains ` or ends ` returns to normal.`, and does not start `Your ` | `StatusGenericReleased` |
| contains ` no longer appears ` | `StatusAppearsReleased` |
| contains ` fades from ` | `StatusFadesFrom` |
| starts `You have killed ` | `YouKilled` |
| starts `Alas, ` | `AlasDied` |
| contains ` has been slain by ` | `SlainBy` |
| contains ` has killed ` | `HasKilled` |
| ends ` dies.` (returns null first for ` sputters and dies.`) | `Dies` |
| ends ` has been banished!` | `Banished` |
| starts `You have entered ` | `ZoneEntered` |
| starts `This instance will expire in ` | `ParseLockoutRemaining` (hand-written, not a regex) |

The members are declared in a different order from the one they are tried in (third person
is often declared first and tried second). Only the order inside `TryParse` matters.

## Where a new branch goes

1. **Pick the guard.** Take the most distinctive literal that every variant contains. If an
   existing family's guard already covers the line, the branch belongs inside that block.
   Otherwise add a new guarded block.
2. **Inside a block, most fixed text first.** `YOUR ...` and `YOU ...` forms (a fixed
   prefix), then the anonymous form (a fixed middle, ` is hit by `), then possessive
   (`'s? `), then plain. A looser pattern also matches the tighter line and splits it wrongly:
   `PlainDamage` reads `a creepfern is hit by Acid Spray for ...` as attacker `a creepfern is`,
   and `PossessiveAbilityDamage` reads `Bob's pet is hit by ...` as attacker `Bob`. Both are
   pinned by tests (`Anonymous_Hit_Attributes_To_Unknown`,
   `Anonymous_Hit_On_Possessively_Named_Victim_Is_Not_A_Phantom_Row`).
3. **Between blocks, earlier wins.** One line can pass several guards: `X has been banished!`
   also ends in `!` and goes through the status block before reaching `Banished`. Put a new
   block above any block whose regexes could also match your line, and make sure no block
   above it matches your line first.
4. **A special case that must not parse** is an early `return null` inside the block, the way
   ` sputters and dies.` is handled, with a comment saying what the line really is.

## Checking it steals nothing

"First match wins" cuts both ways, so check both.

- **Does the new pattern take lines it should not?** Run it over real logs and read every
  template it matches:
  `logmine.ps1 -Templates -CollapseNames -Pattern '<the new regex>' -Max 60`. Every template
  must be the shape you meant. Anything else is a line you would take from a later branch, or
  turn from unmatched into a wrong swing.
- **Is the new pattern starved?** Put your sample lines in a scratch file named
  `eq2log_<anything>.txt` outside the repo and run each earlier pattern of the block (and of
  the blocks above) over it with `-Path`. A hit means that branch gets the line before yours
  does.
- **Count.** `logmine.ps1 -Count` with the new regex gives N for a log. After the change,
  `Full_Pipeline_Over_A_Real_Log` on that same log should report N more matched lines if the
  shape was unmatched before, and the same number if you moved lines between branches (then
  compare the listed fights' damage instead).
- **Pin the split.** The test asserts attacker, ability and victim, not only that the line
  parsed. A phantom parse also "parses".

To paste a pattern from the source into `logmine.ps1`, copy the string inside the attribute,
replace `{DamageVerbs}` with the text of that constant, and turn each doubled `""` back into
one `"`.

## Tables that change together

- **A new auto-attack verb** (the multi attack, flurry, AoE family). Add both conjugations
  to the `DamageVerbs` constant, the third-person form before the second-person one
  (`flurries|flurry`), and add both to the `SpecialFromVerb` switch. `DamageVerbs` feeds four
  patterns at once (`YourAbilityDamage`, `YouAutoDamage`, `PossessiveAbilityDamage`,
  `PlainDamage`). `SpecialFromVerb` falls back to `"None"`, and `Damage` uses that to choose
  `SwingCategory.Melee` or `NonMelee`, so a verb missing from the switch parses but is
  counted as a skill with no special. The three no-damage patterns hard-code `hits` and do
  not use `DamageVerbs`.
- **A new avoid outcome.** Add it to the `outcome` alternation of both `AvoidLine` and
  `YouAvoid`, in both conjugations (`parries` and `parry`), and to the `AvoidFrom` switch.
  The switch's default arm is `Dodge`: an outcome present in the regexes and absent from the
  switch is silently counted as a dodge. A new sentinel value belongs in `DamageValue`
  (`src/EQ2Parser.Core/Combat/DamageValue.cs`), and those codes are part of the stat contract
  in `docs/engine-behaviour.md` section 3.
- **A new status word** (`X is <word>!`). Add it to the `StatusApplied` alternation, then
  decide what it means in `src/EQ2Parser.Core/Triggers/Vocabulary.cs`: map it in
  `CanonicalControlEffect` to one of `ControlEffects`, or leave it unmapped on purpose
  (flavour only, no callout). A brand-new canonical effect also needs an entry in
  `ControlEffects` and a spoken word in `CalloutWord`. Then add the word to
  `GrammarStatusWords` (and `KnownFlavourOnly` if unmapped) in
  `tests/EQ2Parser.Core.Tests/VocabularyTests.cs`: that list is a hand-kept copy of the
  grammar's words, so the test only guards a word you remembered to add. Releases need
  nothing: `StatusReleased` accepts any lower-case word. A status shape with its own wording
  (`StatusFrozen`, `StatusTerrified`, `StatusGloom`, `StatusSilences`) has its effect word
  written in the `TryParse` branch.
- **String tags are compared literally downstream.** `SwingEvent.DamageType` is the damage
  school for damage and one of `heal`, `none`, `avoided`, `power`, `threat`, `threat-reduce`,
  `applied`, `released`, `WardAbsorbType` or the cured effect otherwise; `Extra` carries
  `by=<actor>` on avoids and `remaining=<n>` on wards. `LogLineProcessor`,
  `src/EQ2Parser.Core/Analysis/AbilityMiner.cs` and the App's drill and report views test
  these exact strings. Reuse them; do not invent a near-synonym. Three of them are public
  constants on `EnglishGrammar` and are read through the constant, never retyped:
  `WardAbsorbType`, `ThreatIncreaseType` (`threat`) and `ThreatReduceType` (`threat-reduce`).
  `PayloadBuilder.ResistOf` in `src/EQ2Parser.Core/Upload/PayloadBuilder.cs` turns the ward
  and threat markers into the upload's `resist` text.

## Amounts

Every pattern with a number takes it from one constant, `Amount`
(`\d[\d,]*(?:\.\d+)?[KMB]?`), written as `(?<amount>{Amount})` inside an interpolated
`[GeneratedRegex($@"...")]`, and every builder converts it with
`TryParseAmount(m.Groups["amount"].Value, out var amount)`. `TryParseAmount` strips the comma
grouping, expands a trailing `K`, `M` or `B`, parses as decimal and rounds to a `DamageValue`.
The game abbreviates amounts of six digits and up (`140.6K`), so a pattern with its own
`[\d,]+` drops exactly the biggest hits and nothing fails. Use `{Amount}` and call
`TryParseAmount`; that way a fix to one is a fix to all. The ward pattern's `remaining` group
uses the same constant. An interpolated verbatim string needs any literal brace doubled.

An amount starts with a digit. The older group, `[\d,]+`, read `,5` as 5; no real line writes
one, and a digit-first amount matches exactly as it did before.

`TryParseAmount` is total: it returns false, and never throws, for text the group admits but
that is not a believable amount (too many digits, an amount that overflows once its suffix
scales it, non-ASCII digits, anything over `EnglishGrammar.MaxAmount`). `MaxAmount` is a
plausibility ceiling of ten trillion: about ten times the top of the K/M/B notation, and low
enough that it takes over 900,000 such amounts in one total to overflow a `long`. Without it
one forged line (a custom emote shaped like a damage line) carrying a number near
`long.MaxValue` wrapped a combatant's total negative. An amount over the ceiling is refused,
not clamped. The group is loose on purpose and a line's text is not
entirely the game's to choose, so this input is reachable. The builders with an amount
(`Damage`, `Heal`, `PowerReplenish`, `Threat`, `Ward`) therefore return `SwingEvent?`, null
on a false, and each `TryParse` branch is written
`if ((m = Pattern().Match(message)).Success && (swing = Builder(m, ...)) is not null) return swing;`.
A line with a bad amount is then a line the grammar does not know: no throw, and no
made-up zero. `Ward` also returns null when the optional `remaining` count is present and
unreadable. For a new shape, build the event through a builder of that kind and let the
branch fall through on null.

## Second person

The log owner's own lines are different shapes, not the third-person shapes with another name:

- Actor `YOU` with a bare verb: `YOU hit`, `YOU multi attack`, `YOU flurry`, and
  `YOU try to ... but miss`. Third person conjugates: `hits`, `multi attacks`, `flurries`,
  `tries to ... but misses`.
- Possessive `YOUR <ability>` at the start of the line, where third person has `Name's` or
  `Name'`.
- Victim forms: `YOU` (`tries to crush YOU, but YOU parry.`), `YOURSELF`
  (`being done to YOURSELF.`), lower-case `you` (`has killed you.`), and `THEIR` or `YOUR` in
  threat lines.
- System lines use `You` (`You have killed`, `You have entered`). The line
  `Your health returns to normal.` is excluded from the status release on purpose.

The grammar returns the literal marker `EnglishGrammar.You`; `LogLineProcessor.Resolve` turns
`you` and `yourself` (any case) into the owner's name, for the attacker, the victim and the
`by=` extra. So when you fix one person, find the other with `logmine.ps1` and fix it in the
same change, with its own test case. A player's own log is the authority for that player, so
a missing second-person shape hides exactly the numbers they look at first.

## A new event type

1. `src/EQ2Parser.Core/Grammar/GrammarEvents.cs`: a sealed record deriving from
   `GrammarEvent`, with a comment giving the log line it stands for.
2. `EnglishGrammar.TryParse`: the guarded branch that returns it.
3. `LogLineProcessor.Process` in `src/EQ2Parser.Core/Engine/LogLineProcessor.cs`: a `case` in
   the `switch (parsed)`. The switch has no default arm, so an event with no case is counted
   in `LinesMatched` and then does nothing. Decide here whether it acts on replayed history or
   only on `live` lines.
4. `LogLineHighlighter.Build` in `src/EQ2Parser.App/Services/LogLineHighlighter.cs` switches on
   the same event types to colour the log view. An unhandled type renders plain; add a case if
   the line has parts worth colouring.
5. If the app must know it when it attaches to a log mid-session (the line was written before
   the tail position): expose a public `TryParse...` helper beside `TryParseZoneEntered` and
   `TryParseInstanceLockout`, call it from `ZoneLookbehind.FindLastZoneSeed` in
   `src/EQ2Parser.Core/Logs/ZoneLookbehind.cs`, carry the value on `ZoneSeed`, and apply it in
   `LogSource.PumpAsync` (`src/EQ2Parser.App/Services/LogSource.cs`) where the zone seed is
   applied. The look-behind reads raw history that `TryParse` never sees, which is why the
   helper has to be public. Test it in `tests/EQ2Parser.Core.Tests/ZoneLookbehindTests.cs`.

For a new swing shape rather than a new type, the processor still matters: only `Melee` and
`NonMelee` swings whose attacker differs from the victim are `hostile`, and only hostile
swings start a fight or keep one alive. A non-hostile line outside a fight is dropped by
`SetEncounter`.

## Never throw

Whatever runs after a successful match must not throw. An exception here no longer ends the
log's tail loop: `LogSource.PumpLine` skips the line, writes a `log-line` crash report and
reads on, and only 200 failures in a row (`LineFaultBreaker.DefaultTripAfter`) stop the
source. But the skipped line's numbers are lost, so a throw is still a wrong total. Use
`TryParse` forms and return null (as `ParseLockoutRemaining` does; it also refuses a lockout
longer than `MaxLockoutMinutes`, because its caller adds the result to a timestamp), use
`TryParseAmount` for numbers and return null from the builder when it says false, and do not
index into a capture the regex does not guarantee is long enough. A shape with an amount
gets a line in `Unrepresentable_Amount_Is_A_Non_Match_On_Every_Shape`
(`tests/EQ2Parser.Core.Tests/EnglishGrammarTotalityTests.cs`).

## Tests

| File | Takes |
|---|---|
| `tests/EQ2Parser.Core.Tests/EnglishGrammarTests.cs` | Damage in every person and verb, avoids, heals, kill and death lines, the zone line, amount parsing, and chat or system lines that must not parse |
| `tests/EQ2Parser.Core.Tests/EnglishGrammarV2Tests.cs` | Wards, power, threat, cures, zero-damage hits, third-person death shapes, the comma-less first-person avoid. A brand-new family goes here |
| `tests/EQ2Parser.Core.Tests/TriggerEngineTests.cs` | Status apply and release shapes (`Status_Effect_Lines_Parse_As_Status_Swings`) |
| `tests/EQ2Parser.Core.Tests/ZoneInstanceTests.cs` | The instance lockout line and what the engine does with it |
| `tests/EQ2Parser.Core.Tests/VocabularyTests.cs` | Status words and their canonical effects |

Put a case beside the tests of the same family. Follow the local pattern: the `Swing(message)`
helper, one assertion on the `(Attacker, Ability, Victim)` tuple, then `Damage.Number`,
`Category`, `Special`, `Critical` or `DamageType` as the shape needs, and a `[Theory]` with
`[InlineData]` rows for the conjugations. Add `Assert.Null(EnglishGrammar.TryParse(...))` for
the nearest line that must stay unmatched. The line comes from a real log with player names
swapped for neutral ones; mob and ability names are game content and stay.

## The corpus and `GoldenLogTests`

`tests/logs/eq2log_Menludiir.txt` is copied next to the test binaries and run through the whole
pipeline by `Committed_Corpus_Full_Pipeline` in `tests/EQ2Parser.Core.Tests/GoldenLogTests.cs`.
A new family gets one line there. The owner is the name in the file name, so `YOU` in the file
is that character.

Put the line inside the boss fight (after the first `Malkonis D'Morte` hit, before the
`has been slain` line), give it the epoch and timestamp of the line above it, and keep the boss
as the enemy. Lines sharing a second are normal, and reusing an epoch leaves every gap alone;
fights are cut by a six-second gap between hostile lines. Then these assertions move:

- `Assert.Equal(30, total)` and `Assert.Equal(30, processor.LinesSeen)`: plus one per line.
- `Assert.Equal(25, processor.LinesMatched)`: plus one if the line parses. The comment above it
  lists the unmatched lines; update it if you add one that is unmatched on purpose.
- `Assert.Equal(169_313, boss.Damage)` and the sum in its comment: only if the line is damage
  dealt by the owner or an ally. Damage taken by allies, heals, wards, power, threat, cures and
  the anonymous `Unknown` damage do not count. `boss.EncDps` follows from it.
- `Assert.Equal(145_140, boss.Combatants["ASAME"].Damage)` and
  `Assert.Equal(300, boss.Combatants["MEXXY"].Healed)`: only if the line adds to that
  combatant.
- The `Deaths` assertions and `GetSuccessLevel()`: only if the line is a death.

These stay put if you followed the placement above, and each one moving is a sign the line
landed somewhere else:

- `Assert.Equal(2, engine.History.Count)`: a gap of more than six seconds, or a hostile line
  outside both fights, makes a third fight.
- `Assert.Equal(24, boss.Duration.TotalSeconds)`, together with the `24.0` in the `EncDps`
  assertion: an ally's damaging line after the slain line stretches the fight.
- `boss.Title`: an enemy that took more damage than the boss becomes the title.
- Every assertion on the trash fight.

## After

Go back to step 4 of the skill: the `parser-verify` skill for the gate, then the real-log
reports again. If a total on an existing log changed, say so to the user with the before and
after numbers.
