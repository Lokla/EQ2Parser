# Add a grid or export column

Applies when a column is added to the encounter grid (the main combatant table), the drill table, the swing
log, or the Discord clipboard export. The first three are `ColumnSets.Encounter`, `.Drill` and `.Swing`; the
export has its own catalogue.

## Checklist: encounter grid

1. `src/EQ2Parser.App/ViewModels/EncounterColumnsVm.cs`: add `new("Key", Loc.Get("Cols_Key"), defaultVisible)`
   to `ColumnSets.Encounter`. List the key in the core-key array only if its value is computed on every tick;
   leave it out if it reads `ExtStats`.
2. `src/EQ2Parser.App/ViewModels/CombatantRow.cs`: an `[ObservableProperty]` string for the cell.
3. `src/EQ2Parser.App/ViewModels/MainParseViewModel.Grid.cs`: add the value to `RowData`, or to `ExtStats`
   with `ExtOf` and `SumExt`; add an arm to the `Sort` switch; assign the cell in `Apply`.
4. `src/EQ2Parser.App/Views/MainParseView.xaml`, in two grids that must stay identical in order:
   - `GridRowTemplate`: a `ColumnDefinition` bound to `Data.ByKey[Key].Visible` through `ColWidth`, and a cell
     `TextBlock` with the same binding through `BoolToVis`;
   - the header grid: the same `ColumnDefinition` with the same `ConverterParameter` width, and a header
     `Button` with `SortByCommand` and a `CommandParameter` that matches your `Sort` arm.
5. Strings: `Cols_Key` (the toggle label) and `Main_ColKEY` (the header), in all four dictionaries.

For the drill table or swing log, do the same against `ColumnSets.Drill` (`DrillColsProxy`) or
`ColumnSets.Swing` (`SwingColsProxy`): each has its own header grid and row template in the same XAML file.

## Checklist: Discord export

1. `src/EQ2Parser.App/ViewModels/MainParseViewModel.Export.cs`: add the field to the `ExportRow` record and
   fill it in `RenderTable`, where the one `new ExportRow(...)` is positional.
2. Add an `ExportColumnDef` to `ExportColumnDefs` (key, label, header, alignment, cell renderer).
3. Add an arm to `SortRows`. The export window offers every column as a sort choice.
4. Reuse the `Cols_` and `Main_Col` strings of the grid column where one exists.

## Why these and what goes wrong

- The row template and the header each declare their own `ColumnDefinition` list, and cells are placed by
  `Grid.Column` number. Inserting mid-list means renumbering every later `Grid.Column` in both grids; a miss
  puts a value under the wrong heading with no error.
- `ByKey[Key]` is a dictionary indexer in a binding. A key that is not in `ColumnSets` does not fail the
  build: the binding fails at runtime and `ColumnWidthConverter` returns width 0, so the column never shows.
- Column keys are persisted (`EncounterColumns`, `CombatantColumns`, `AttackTypeColumns`, `ExportColumns`,
  `ExportSortKey`, and inside `ExportPresets`). `ColumnSetVm` applies a saved list as "visible only if listed",
  so a renamed key becomes hidden for every user who customised, and a new column is hidden for those users
  even when `defaultVisible` is true. Never rename a key; reword its `Cols_` string instead.
- Sort names are not column keys. The `Time` header sends `Duration` and the `Percent` header sends `Damage`.
  `Sort` and `SortRows` both end in a `_ =>` arm that sorts by damage, so a header with no matching arm appears
  to work and sorts by the wrong thing.
- `ExtStats` is only computed while `Columns.Extended` is true, meaning a non-core column is visible. A column
  that reads `ExtStats` but is listed as core stays blank.
- The grid (`ExtOf`) and the export (`RenderTable`) compute the same stats separately from the same buckets.
  Give a new stat one definition and use it in both, or the copied table disagrees with the screen.
- A number shown here is a stat definition. Derive it from existing `Combatant` and `Bucket` values; if it
  needs new engine maths, read `swing-category.md` and the stable-numbers rule first.
- The mini parse meter columns are a different set (`OverlayWindowSettings.MeterColumns`); see `overlay.md`.

## Tests

- No test covers the column tables or the XAML. Use the `parser-ui-check` skill: toggle the column, sort by
  it, and check a row against the export.
- `tests/EQ2Parser.Core.Tests/LocalizationTests.cs` for the strings and
  `tests/EQ2Parser.Core.Tests/TableExportTests.cs` for table rendering.
