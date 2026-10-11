# Change the history.db schema

Applies when a column, index or table is added to the local encounter archive, or when the shape of a stored
value changes. The archive is SQLite, opened by `HistoryStore`; every statement is inline in that one file.

## Checklist

1. `src/EQ2Parser.Core/History/HistoryStore.cs`: raise the `SchemaVersion` constant by one.
2. In `Migrate()`, add a step keyed on what the file HOLDS, not on its version stamp. Each table is asked
   separately (`ColumnNames("encounters")`, `ColumnNames("swings")`), and a table the file lacks is skipped:
   `if (swings.Count > 0 && !swings.Contains("new_column")) steps.Add("ALTER TABLE swings ADD COLUMN ...;")`.
   A column on `combatants` needs its own `ColumnNames("combatants")` block; there is none yet because that
   table has not changed. A backfill or any rewrite of stored values goes in the same step as the `ALTER`
   that introduces the column, so it runs exactly once, in the same transaction. Update the list of steps in
   `Migrate`'s doc comment.
3. In `CreateSchema`, add the same column, index or table to the DDL. A new database never runs `Migrate`; it
   gets its whole shape from here. A column added here WITHOUT step 2 is the mistake to avoid: it happened
   once (`swings.observed_at_ms`, made good in version 4), and a file created before it then failed every save
   and every restore.
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
   `V1_Database_Migrates_In_Place_With_Boss_Backfill` is the model for `encounters`, and
   `Swings_Table_From_Before_The_Arrival_Stamp_Gains_The_Column` for another table. Add a round-trip for the
   new value too. `Current_File_Stamped_Down_To_Any_Older_Version_Reopens_With_Its_Rows` loops over every
   older version by itself, so it covers the new step without an edit; run it.
   `First_Release_Shape_In_Every_Table_Migrates_In_One_Open` compares a migrated first-release file with a
   fresh one column for column, so it fails when `CreateSchema` gains a column that `Migrate` does not add.

## Why these and what goes wrong

- The constructor runs `Migrate()`, then `CreateSchema()`, then `StampVersion`. A brand-new file has no
  tables, so `Migrate` finds no step to run (an `ALTER TABLE` would fail on a table that does not exist) and
  `CreateSchema` builds the whole shape. If any of the three throws, the constructor closes its connection
  before the exception leaves, so the file is not left locked.
- Steps ask the file, never the stamp, because the stamp lies. Released older builds write their own lower
  version into a newer file every time they open it. Version-keyed steps then ran `ADD COLUMN` a second time
  on the user's return to the current build, SQLite answered "duplicate column name", the constructor threw
  while `SourceManager` was being built, and the app could not start with the archive intact on disk. Every
  step, present and future, must be safe on a file that already has the change.
- A step that throws no longer stops the app, and that is not a reason to be less careful. `HistoryService`
  catches a constructor failure: an SQL error (SQLite result code 1, which is what a wrong step produces)
  leaves the file untouched and runs the session WITHOUT an archive, so every fight of that session goes
  unsaved until a fixed build ships. Only SQLite's own "not a database" or "malformed" verdict moves the file
  aside (`HistoryStore.IsDamagedFile`). `.claude/skills/parser-triage/references/app-data.md` has the states,
  under "The archive".
- The stamp is only ever raised. A file from a NEWER build is opened read/write with its stamp left alone,
  which works because every statement names its columns. That holds only while changes stay additive: new
  nullable or defaulted columns, new tables, new indexes. Removing or renaming something, or changing what an
  existing column means, breaks every released build that opens the file afterwards; a version bump does not
  protect them. That kind of change needs its own plan (a new file name at the least) and the user's say-so.
- If a step is missing, older files never get the column. The `INSERT` that names it then throws inside the
  `HistoryService` writer on every save, and the `SELECT` that names it throws for every fight of the startup
  restore. Neither stops the app, and neither is silent any more: the saves leave `archive-save` crash reports
  and `archive save FAILED` journal lines and count in `HistoryService.SaveFailures`, the restore leaves an
  `archive-restore` report. But every later fight is still missing from the archive, so the test in step 7 is
  what prevents it.
- SQLite needs a `DEFAULT` on a `NOT NULL` column added by `ALTER TABLE`, as `is_boss` has. Prefer a nullable
  column whose null means "written before this existed", as `zone_instance_expiry` does.
- Some columns hold enum numbers: `swings.category` is `SwingCategory` and `encounters.success` is
  `SuccessLevel`. Renumbering either enum changes the meaning of every stored row; see `swing-category.md`.
- The index DDL runs after `Migrate`, so an index on a new column can be declared in `CreateSchema` alone.
- The migration runs on the user's only copy of their fight archive. `HistoryService` writes a backup snapshot
  on clean shutdown (to a temporary name first, rotating the earlier backups only once it is complete), but
  treat that as a last resort: backfill inside the migration (as the `is_boss` step does) and never drop or
  rewrite rows to make a change fit.
- Restored fights are rebuilt by replaying stored swings through `Encounter.AddSwing`. A value that the replay
  cannot reproduce (a scripted win, for example) has to be stored and carried across in `RestoreEncounter`.

## Tests

- `tests/EQ2Parser.Core.Tests/HistoryStoreTests.cs`: the new migration test and the round-trip, plus the
  stamped-down, stamped-higher and brand-new-file tests already there, which prove the step is repeatable.
- `tests/EQ2Parser.Core.Tests/EncounterCorrelatorTests.cs` and `tests/EQ2Parser.Core.Tests/ZoneInstanceTests.cs`
  if the new column feeds grouping, as `zone_instance_expiry` does.
