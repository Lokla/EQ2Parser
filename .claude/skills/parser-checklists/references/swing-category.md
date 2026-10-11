# Add a swing category or a stat bucket

Applies when `SwingCategory` gains a member, when a bucket is added to `BucketConfig`, or when the link between
a category and its buckets changes. This is the stat engine: a change here can move numbers the site compares
across uploads, so settle with the user what is meant to change before starting.

## Checklist

1. `src/EQ2Parser.Core/Combat/Swing.cs`: add the member to `SwingCategory` with a new explicit number. Never
   renumber or reuse a number.
2. `src/EQ2Parser.Core/Combat/Bucket.cs`, in `BucketConfig`, if the category has stats of its own: the
   bucket-name constants, an entry in `OutgoingAllyValues` and `IncomingAllyValues` for each new bucket, and
   the category's rows in `OutgoingLinks` and `IncomingLinks`. A category with no link files only into the two
   `(Ref)` buckets, as `StatusEffect` does.
3. `src/EQ2Parser.Core/Grammar/EnglishGrammar.cs`: emit the category from the line shape that produces it (the
   `SwingEvent` record is in `src/EQ2Parser.Core/Grammar/GrammarEvents.cs`).
4. `src/EQ2Parser.Core/Engine/LogLineProcessor.cs`: the `hostile` rule. Only `Melee` and `NonMelee` start a
   fight or keep one alive; decide deliberately whether the new category does.
5. The other places that repeat `Melee or NonMelee` as "this is damage": `Encounter.AddSwing` in
   `src/EQ2Parser.Core/Combat/Encounter.cs` (self-damage), `HistoryStore.UniqueSwings` in
   `src/EQ2Parser.Core/History/HistoryStore.cs`, and `src/EQ2Parser.Core/Analysis/AbilityMiner.cs`.
6. `src/EQ2Parser.Core/Combat/Combatant.cs`: a stat property for a new bucket, beside `Healed`.
7. App tables keyed by bucket name: `BucketOrder` and `IsIncomingBucket` in
   `src/EQ2Parser.App/ViewModels/MainParseViewModel.Drill.cs`, `DrillLineBuckets` in
   `src/EQ2Parser.App/ViewModels/MainParseViewModel.Charts.cs`, and the category tests in
   `src/EQ2Parser.App/ViewModels/MainParseViewModel.Reports.cs`.
8. `src/EQ2Parser.Core/Upload/PayloadBuilder.cs`: work out what the new category or bucket adds to the
   payload, then use the `parser-contract` skill.
9. `docs/engine-behaviour.md` section 3: the swing type list and the bucket list.

## Why these and what goes wrong

- The enum number is stored and sent. `SaveEncounter` writes `(int)swing.Category` into `swings.category` and
  `LoadSwings` casts it back; `PayloadBuilder` sends it as `swingtype`. A renumbered member silently re-labels
  every archived swing and every row already on the site.
- A category with no bucket link still reaches the site. `attack_types` are built from `All Outgoing (Ref)`,
  which receives every swing, one row per (ability, category) carrying that category's number. So a new
  category uploads the moment it exists unless `PayloadBuilder.IsUploaded` excludes it, as it does
  `StatusEffect` (30). Decide which it is, and if it uploads, give it a `resist` label in `ResistOf` and a
  reader on the site (the `parser-contract` skill).
- Bucket names are wire data too: `damage_types[].type` is the bucket name. Do not rename one.
- `Combatant.AddOutgoing` and `AddIncoming` index the ally-value dictionaries directly. A bucket named in a
  link table but missing from `OutgoingAllyValues` or `IncomingAllyValues` throws on the log pump thread at
  the first such swing.
- Every linked bucket adds its ally value between attacker and victim. That graph decides who is an ally, and
  from that the encounter's damage, EncDPS, title and success level. A wrong sign puts combatants on the wrong
  side; a new link changes ally detection in every fight that contains such swings.
- Encounter end time is the last swing in an ally's `Outgoing Damage` bucket (`Combatant.ShortEndTime`).
  Linking a category into `OutgoingDamage` changes durations and therefore every rate. Keep the asymmetry
  too: outgoing damage lands in two buckets (its split bucket and the aggregate), incoming in one.
- Archived fights are rebuilt by replaying swings through `Encounter.AddSwing`. A changed link therefore
  changes old fights too when they are restored, not only new ones.
- `IsIncomingBucket` recognises incoming buckets by the `(Inc)` name suffix. A new incoming bucket named
  differently is looked up among the outgoing buckets and the drill view never finds it.

## Tests

- `tests/EQ2Parser.Core.Tests/EnglishGrammarTests.cs` (or its `V2` sibling) for the line shape, and
  `tests/EQ2Parser.Core.Tests/EncounterTests.cs` for buckets, allies and durations.
- `tests/EQ2Parser.Core.Tests/GoldenLogTests.cs`: its totals must not move unless that is the intended change.
- `tests/EQ2Parser.Core.Tests/HistoryStoreTests.cs` (save and restore) and
  `tests/EQ2Parser.Core.Tests/UploadClientTests.cs` (the payload).
