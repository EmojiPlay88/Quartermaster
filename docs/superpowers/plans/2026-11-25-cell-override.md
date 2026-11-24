# Table Cell Override Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A piece property can override one table column for the piece that holds it, wherever a table read looks up that piece's own row.

**Architecture:** Rules gains `PropertyDef.OverridesTable/OverridesColumn`, validator checks and a helper listing overrides per table. Core applies overrides at one choke point (`CellOverrides.Read`) used by the Table binding and the inline `lookup()` of piece contexts. Masking treats table reads overridden by a non-Public property like Private Unit reads. Workbench gets the two pickers; docs.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-25-cell-override-design.md`

## Global Constraints

- Public repo: invented test data only. No CNA text.
- Old packs keep bytes and SHA-256: both fields omitted when null/empty (blank → null on load); `CurrentFormat` stays 4.
- No new NuGet packages. Never open visible windows. One test project at a time, `-c Release`, `--filter` while iterating.
- Never kill processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity; message ends with a blank line then a `Co-Authored-By:` trailer naming the model that wrote it. Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `cell-override` (from master `357042d`).
- Exact texts:
  - working source when overridden: `{table}, row {r}, column {c}: {property} of {piece} (printed {cell})` (1-D: `{table}, row {r}: {property} of {piece} (printed {cell})`)
  - validator: `Property "{name}" overrides a table that no longer exists`; `Property "{name}" overrides column "{c}", which table "{t}" does not have`; `Property "{name}" overrides a table but is not a piece property`; warning `Properties "{a}" and "{b}" both override {t}, column {c}; "{a}" wins` (1-D: `both override {t}; "{a}" wins`)
  - breakdown line: `overrides {table}, column {c}` (1-D: `overrides {table}`)

## Review Focus

1. **Lookup by another piece's name while evaluating this piece** (`lookup(Units, "Raft B", "Speed")` on Raft A, A has the override) → printed cell, not A's value. Test in Task 2 (`Lookup_of_other_row_uses_cell`).
2. **Piece without a value for the property** (Applies to false, or no Ledger value and not computed) → printed cell, never the property Default. Test in Task 2 (`No_value_uses_cell_not_default`).
3. **Game-level / phase-condition lookup of the same table** (no piece) → printed cell. Test in Task 2 (`Game_level_lookup_uses_cell`).
4. **Row key comparison** (name "Raft  A" vs key "Raft A", numeric-looking names) → same normalisation as table keys. Test in Task 2 (`Row_match_uses_table_key_normalisation`).
5. **Guest view of a Private override on an enemy piece** → the formula's table input is hidden (host decides), working redacted; Public override shown. Test in Task 3 (`Guest_private_override_masked`).

---

### Task 1: Rules — fields, validator, helper

**Files:**
- Modify: `src/Quartermaster.Rules/Model/RulesPack.cs` (`PropertyDef.OverridesTable : string?`, `OverridesColumn : string?`), `src/Quartermaster.Rules/Io/RulesPackIo.cs` (omit/normalise as `Expr`)
- Create: `src/Quartermaster.Rules/Tables/CellOverrideFacts.cs`: `public static IReadOnlyList<PropertyDef> For(RulesPack pack, string tableId, string? columnKey)` (pack order; 1-D tables ignore the column; column compared with `TableLookup.Normalise`), `public static bool HiddenOverride(RulesPack pack, string tableId)` (any overriding property with `!PropertyFacts.IsPublic`)
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (the four texts)
- Test: `tests/Quartermaster.Rules.Tests/CellOverrideTests.cs`
- [ ] **Step 1: Failing tests**: `Fields_round_trip_and_absent_keep_bytes`; `Validator_missing_table`; `Validator_missing_column`; `Validator_holder_scope`; `Validator_duplicate_warning`; `For_orders_by_pack_and_ignores_column_on_1d`; `HiddenOverride_true_for_private`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~CellOverride"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests; build Core, Render, Desktop, App → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): properties that override a table column`

### Task 2: Core — apply overrides at the table read

**Files:**
- Create: `src/Quartermaster.Core/CellOverrides.cs`: `internal static (Value Value, PropertyDef? By, Value Printed) Read(TableDef t, int row, int col, PieceInstance? piece, GameState s, RulesPack pack, ModuleModel? model)` — no piece → cell; row key (`TableLookup.Normalise(t.RowKeys[row])`) ≠ normalised `PieceInfo.Name(piece)` → cell; first property of `CellOverrideFacts.For(pack, t.Id, colKey)` that the piece has a value for (`PropertyValues.Get` non-null; a stored value or applying computed) → its value; else cell. Missing/hidden values (EvalError MissingValue/HiddenData) propagate as they do for a Unit binding.
- Modify: `src/Quartermaster.Core/FormulaRunner.cs` (Table binding uses `Read`, description per constraints when `By != null`; `RunContext.Lookup` uses it with the run piece), `src/Quartermaster.Core/PieceFilter.cs` (`FilterContext.Lookup` with its piece; `GameContext` unchanged), `PieceFilter.LookupTable` gains an overload taking the piece (Locate then `Read`)
- Modify: `src/Quartermaster.Core/LedgerBreakdown.cs` (the breakdown line)
- Test: `tests/Quartermaster.Core.Tests/CellOverrideRunTests.cs`
- [ ] **Step 1: Failing tests**: `Binding_uses_override_when_piece_has_value`; `Inline_lookup_uses_override`; `Lookup_of_other_row_uses_cell`; `No_value_uses_cell_not_default`; `Game_level_lookup_uses_cell`; `Row_match_uses_table_key_normalisation`; `Other_column_uses_cell`; `Computed_override_applies`; `Unit_filter_sees_override`; `Working_source_names_override`; `Breakdown_line`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~CellOverride"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests → PASS.
- [ ] **Step 5: Commit** `feat(core): table reads use a piece's override`

### Task 3: Masking, Workbench, docs, full runs

**Files:**
- Modify: `src/Quartermaster.Render/FormulaMasking.cs` (a Table binding, or the formula's inline lookups via a new `FormulaVariables.TablesRead(f)`, of a table with `CellOverrideFacts.HiddenOverride` is hidden when `UnitHidden` would hide a Private Unit read — i.e. the run piece is of a side the viewer does not hold, or cannot be seen), `src/Quartermaster.Core/PlayerView.cs` (same rule for working redaction, mirroring how Private Unit inputs are redacted), `src/Quartermaster.Desktop/Workbench/PropertiesTab.cs` (two pickers; column disabled for 1-D; blank clears), `src/Quartermaster.App` (build only)
- Modify: `docs/wiki/Formula-Reference.md` (section "A piece's own value for a table rating", invented Riverland example, error rows), Workbench page (Properties tab fields), `docs/superpowers/RESUME.md` (new CURRENT STATE, old under Previous)
- Test: Render `Guest_private_override_masked`, `Guest_public_override_shown`; Core `PlayerView_redacts_private_override_working`; Desktop `PropertiesTab_override_pickers`
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~Override|FullyQualifiedName~Mask"`, Core `--filter "FullyQualifiedName~PlayerView"`, Desktop `--filter "FullyQualifiedName~PropertiesTab"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME.
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, Desktop `MainWindowOnlineTests.Host_players_dialog_and_guest_tooltip`, Desktop `TurnsEndToEndTests.Round_trip_through_mailbox` — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat: table overrides masked, Workbench pickers; docs`
