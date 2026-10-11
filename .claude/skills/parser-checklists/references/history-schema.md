# Change the history.db schema

Applies when a column, index or table is added to the local encounter archive, or when the shape of a stored
value changes. The archive is SQLite, opened by `HistoryStore`; every statement is inline in that one file.

## Checklist

1. `src/EQ2Parser.Core/History/HistoryStore.cs`: raise the `SchemaVersion` constant by one.
2. In `Migrate()`, add a step keyed on what the file HOLDS, not on its version stamp:
   `if (!columns.Contains("new_column")) steps.Add("ALTER TABLE ... ADD COLUMN ...;")`. A backfill or any
   rewrite of stored values goes in the same step as the `ALTER` that introduces the column, so it runs exactly
   once, in the same transaction.
3. In `CreateSchema`, add the same column, index or table to the DDL. A new database never runs `Migrate`; it
   gets its whole shape from here.
4. Writers: in `SaveEncounter`, the `INSERT` column list, the `VALUES` list and the matching
   `Parameters.AddWithValue` call.
5. Readers: the `SELECT` list and the positional `reader.Get...(n)` calls in `SearchEncounters`,
   `LoadCombatants` or `LoadSwings`, plus the record that carries the value (`EncounterSummary`,
   `CombatantSummary` or `Swing`) and `RestoreEncounter` if a restored fight needs it. Append new columns at
   the end of a `SELECT` so the existing ordinals do not shift.
6. App consumers of the changed record: `src/EQ2Parser.App/Services/HistoryService.cs` and
   `src/EQ2Parser.App/ViewModels/ArchiveViewModel.cs`.
7. `tests/EQ2Parser.Core.Tests/HistoryStoreTests.cs`: a migration test that builds a database in the previous
   shape, stamps its `user_version`, opens it with `HistoryStore`, and then reads and writes.
   `V1_Database_Migrates_In_Place_With_Boss_Backfill` is the model. Add a round-trip for the new value too.
   `Current_File_Stamped_Down_To_Any_Older_Version_Reopens_With_Its_Rows` loops over every older version by
   itself, so it covers the new step without an edit; run it.

## Why these and what goes wrong

- The constructor runs `Migrate()`, then `CreateSchema()`, then `StampVersion`. A brand-new file has no
  tables, so `Migrate` returns at once (an `ALTER TABLE` would fail on a table that does not exist) and
  `CreateSchema` builds the whole shape.
- Steps ask the file, never the stamp, because the stamp lies. Released older builds write their own lower
  version into a newer file every time they open it. Version-keyed steps then ran `ADD COLUMN` a second time
  on the user's return to the current build, SQLite answered "duplicate column name", the constructor threw
  while `SourceManager` was being built, and the app could not start with the archive intact on disk. Every
  step, present and future, must be safe on a file that already has the change.
- The stamp is only ever raised. A file from a NEWER build is opened read/write with its stamp left alone,
  which works because every statement names its columns. That holds only while changes stay additive: new
  nullable or defaulted columns, new tables, new indexes. Removing or renaming something, or changing what an
  existing column means, breaks every released build that opens the file afterwards; a version bump does not
  protect them. That kind of change needs its own plan (a new file name at the least) and the user's say-so.
- If a step is missing, older files never get the column. The `INSERT` that names it then throws inside the
  `HistoryService` writer, which swallows the error by design, so every later fight is lost without a message.
- SQLite needs a `DEFAULT` on a `NOT NULL` column added by `ALTER TABLE`, as `is_boss` has. Prefer a nullable
  column whose null means "written before this existed", as `zone_instance_expiry` does.
- Some columns hold enum numbers: `swings.category` is `SwingCategory` and `encounters.success` is
  `SuccessLevel`. Renumbering either enum changes the meaning of every stored row; see `swing-category.md`.
- The index DDL runs after `Migrate`, so an index on a new column can be declared in `CreateSchema` alone.
- The migration runs on the user's only copy of their fight archive. `HistoryService` writes a backup snapshot
  on clean shutdown, but treat that as a last resort: backfill inside the migration (as the version
  1 block does for `is_boss`) and never drop or rewrite rows to make a change fit.
- Restored fights are rebuilt by replaying stored swings through `Encounter.AddSwing`. A value that the replay
  cannot reproduce (a scripted win, for example) has to be stored and carried across in `RestoreEncounter`.

## Tests

- `tests/EQ2Parser.Core.Tests/HistoryStoreTests.cs`: the new migration test and the round-trip, plus the
  stamped-down, stamped-higher and brand-new-file tests already there, which prove the step is repeatable.
- `tests/EQ2Parser.Core.Tests/EncounterCorrelatorTests.cs` and `tests/EQ2Parser.Core.Tests/ZoneInstanceTests.cs`
  if the new column feeds grouping, as `zone_instance_expiry` does.
