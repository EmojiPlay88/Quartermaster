# Authoring and Tooling (B1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rule refs on properties, holders, alerts, warnings and checklist items; checklist items with ids and links; number labels; holder properties kept by some holders; a log failure flag and a stable pack test harness; Clone copies ledger values.

**Architecture:** Rules gains optional fields on `PropertyDef`, `HolderDef`, `AlertDef`, `PhaseWarning`, plus a `ChecklistItem` record (string-compatible converter), `PropertyLabels`, `ChecklistText` and validator checks. Core ticks checklist items by id, flags failure lines (`LogEntry.Error`), enforces kept holders at run time, labels working, and seeds clones from the source's ledger. Render adds `PackHarness`, flags the controller's failure lines, wires clone seeding (local and guest). Desktop/Workbench gets the editors and displays; docs.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-25-b1-authoring-design.md`

## Global Constraints

- Public repo: invented test data only (Riverland demo, TEST names). No content from the private game, no CNA text, no rule numbers from it.
- Old packs keep bytes and SHA-256: every new pack field omitted when null/empty/default (blank → null on load; `Holders` empty → null; `CloneAs` omitted when `Copy`); a text-only `ChecklistItem` is written as a JSON string. `CurrentFormat` stays 4. Proved by `Text_only_items_and_absent_fields_keep_bytes` (byte-equal `pack.json` and equal `ComputeSha256`).
- Old saves keep bytes: `LogEntry.Error`, `AddLogLine.Error` omitted when false; `TickChecklist.ItemId`, `AddPiece.CloneOf`, `GameState.TickedIds`, `SeedLedger.CloneOf` omitted when null.
- No new NuGet packages. No new projects. Never open visible windows (headless Avalonia tests only). One test project at a time, `-c Release`, `--filter` while iterating.
- Never kill processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity (`EmojiPlay88`); message ends with a blank line then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (the model that wrote it). Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `b1-authoring` (from master `ace76fc`).
- Hidden information (rules, each with a test):
  - a label is computed only from values the viewer's state already holds (`PlayerView.For`) and lives inside working that `PlayerView` already redacts — no new reveal path;
  - `PlayerView` clears `Error` on a redacted line (`Outcome.Redact`); dropped lines take it with them; kept lines keep it;
  - clone copies nothing when the clone's side (with copied values) differs from the source's; a guest `AddPiece` with `CloneOf` is refused unless the guest may act on the source (`Actable`) and the type string equals the source's;
  - `PackHarness.Log` is the full host log; `PackHarness.ViewFor(sides)` returns `PlayerView.For` for checking masks.
- Exact texts:
  - validator (warning) dangling rule ref: `{who}: rule reference "{id}" does not exist`, `{who}` ∈ `Property "{name}"`, `Holder "{name}"`, `Alert "{name}"`, `Phase "{name}": warning {n}`, `Phase "{name}": checklist item {n}` (n 1-based)
  - validator checklist: error `Phase "{p}": checklist item {n} has an unusable id "{id}"; use letters, digits, '-', '_' or '.'`; error `Phase "{p}": checklist item id "{id}" is used more than once`; warning `Phase "{p}": checklist item {n} names item "{id}", which does not exist`; warning `Phase "{p}": checklist item {n} names formula "{id}", which does not exist`
  - validator labels: `Property "{name}" takes its labels from a table that no longer exists`; `Property "{name}" takes its labels from table "{t}", which has more than one column`
  - validator holders: `Property "{name}" names holder "{id}", which does not exist`; `Property "{name}" lists holders but is not a holder property`; binding `{variable} reads "{p}", which holder "{h}" does not keep`; output `output {n} writes "{p}", which holder "{h}" does not keep`; `holder()` tail `refers to "{p}", which holder "{h}" does not keep` (`{h}` = holder name, `Global` for the global holder)
  - run failure and Ledger edit refusal: `{holder} does not keep {property}`
  - label display: `{number} ({label})`
  - checklist links: `{item:x}` → `"{text}"` (text cut to 40 characters + `…`), `{formula:x}` → formula name; missing → token unchanged
  - clone diagnostic (warning, category `rules`): `Clone of "{name}" belongs to another side, so its values were not copied`
  - Workbench: column headers `Rule ref`, `Labels`, `Holders`, `Clone as` (items `Copy`, `Default`); picker items `(none)` and `{label}, page {page}`; unknown holder status `No holder named "{name}"`; checklist detail labels `Id:`, `Rule ref:`, `Notes:`, `Links:`; link entries `{target}` or `{token} (missing)`; checklist list row `{id}: {shown text}` (no id: shown text); checklist id status `Use letters, digits, '-', '_' or '.' in an id`, `Another checklist item already has the id "{id}"`
  - Workbench tooltips: Labels `A one-column table whose row for a value gives the text shown next to it, e.g. "3 (Fog)". Values stay numbers. Blank = none.`; Holders `Holders that keep this property, comma-separated names. Blank = every holder.`; Clone as `What a copy made with the counter's Clone command gets: Copy = this counter's current value; Default = what a new counter starts with.`
  - harness: `No setup named "{name}"`; `No piece named "{name}"`; `{n} pieces are named "{name}"; use a unique name`; `No holder or piece named "{name}"`; `No property named "{name}"`; `The harness ran out of dice (formula "{name}")`

## Review Focus

1. **Old packs keep bytes** — an invented format-4 `pack.json` literal (checklist strings, every pre-B1 field) loads and saves byte-equal, same SHA-256. Test in Task 1 (`Text_only_items_and_absent_fields_keep_bytes`).
2. **Tick by id survives reorder** — tick item `fuel` (id) and item 2 (no id), reorder the pack's items: `fuel` stays ticked, index ticks stay by index. Test in Task 2 (`Tick_by_id_survives_reorder`).
3. **Failure flag does not leak** — another side's failed auto-run line is redacted for the viewer and carries `Error == false`; the viewer's own failed line keeps `Error == true`. Test in Task 2 (`Redacted_failure_line_drops_error_flag`).
4. **Label inside hidden working** — an enemy run reading a Private labelled property: the guest's log line shows neither the number nor the label. Test in Task 2 (`Guest_hidden_working_with_label_redacted`).
5. **Guest clone of an unheld piece** — guest sends `AddPiece` with `CloneOf` = another side's piece (same type): refused with `Actable`'s text, no piece, no seeds; with a different type on its own piece: `That piece can't be placed.` Test in Task 3 (`Guest_clone_of_unheld_piece_refused`).

---

### Task 1: Rules — fields, checklist items, labels, holders, validator

**Files:**
- Modify: `src/Quartermaster.Rules/Model/RulesPack.cs` — `PropertyDef.RuleRefId : string?`, `Labels : string?`, `Holders : List<string>?` (all `[JsonIgnore(WhenWritingNull)]`), `CloneAs : CloneValue` (`[JsonIgnore(WhenWritingDefault)]`), `public enum CloneValue { Copy, Default }`; `HolderDef.RuleRefId`, `AlertDef.RuleRefId` (`WhenWritingNull`)
- Modify: `src/Quartermaster.Rules/Model/PhaseDefs.cs` — `PhaseWarning.RuleRefId` (`WhenWritingNull`); `PhaseDef.Checklist : List<ChecklistItem>`
- Create: `src/Quartermaster.Rules/Model/ChecklistItem.cs` — `[JsonConverter(typeof(ChecklistItemConverter))] public sealed record ChecklistItem { string? Id; string Text = ""; string? RuleRefId; string? Notes; public static implicit operator ChecklistItem(string text); public bool IsTextOnly }`; `ChecklistItemConverter` (string → text-only item; object → item; text-only → string; else object without null fields, camelCase)
- Create: `src/Quartermaster.Rules/Validation/ChecklistText.cs` — `public const int CutAt = 40`; `public static bool IsUsableId(string id)` (`^[A-Za-z0-9._-]+$`); `public static IReadOnlyList<(string Kind, string Id, string Token)> Tokens(string text)` (`{item:…}`, `{formula:…}`); `public static string Render(RulesPack pack, string text)`; `public static ChecklistItem? ItemById(RulesPack pack, string id)` (with its phase)
- Create: `src/Quartermaster.Rules/Tables/PropertyLabels.cs` — `public static string? Label(RulesPack pack, PropertyDef p, double value)` (table by `p.Labels`, OneD/Dice only, `TableLookup.Locate`, trimmed cell, null when none/blank/error); `public static string Text(RulesPack pack, PropertyDef p, double value)` (`Value.FormatNumber` + ` ({label})`)
- Modify: `src/Quartermaster.Rules/Validation/PropertyFacts.cs` — `public static bool KeptBy(PropertyDef p, string holderId)` (null list = all; ordinal)
- Modify: `src/Quartermaster.Rules/Io/RulesPackIo.cs` — `Normalise`: blank → null for every new string; `Holders` trimmed, blanks dropped, distinct, empty → null; checklist items `Text ?? ""`, blank `Id`/`RuleRefId`/`Notes` → null; warnings' `RuleRefId`
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` — all validator texts in the constraints (rule refs in `CheckPhase`, a new `CheckRuleRefs`, `CheckChecklists` pack-wide for duplicates, `CheckLabels`, `CheckHolders`; kept checks in `CheckBinding` Holder/Global, `CheckOutput` Holder/Global, `HolderCallProblems`); `CheckRuleRefPaths` lists the new users
- Modify (compile only, no behaviour change): `src/Quartermaster.Core/Decisions.cs`, `src/Quartermaster.Render/PhaseController.cs` (`Tick`), `src/Quartermaster.Render/PhaseEdit.cs`, `src/Quartermaster.Desktop/Phases/PhasePanel.cs`, `src/Quartermaster.Desktop/Workbench/PhasesTab.cs`, `src/Quartermaster.App/PhasePanel.cs`, `src/Quartermaster.App/Workbench/PhasesTab.cs`, `tests/Quartermaster.TestFixtures/PhaseFixture.cs` and test files reading `Checklist[i]` as a string (`.Text`)
- Test: `tests/Quartermaster.Rules.Tests/AuthoringFieldsTests.cs`
- [ ] **Step 1: Failing tests**: `Text_only_items_and_absent_fields_keep_bytes`; `Checklist_item_with_fields_round_trips_as_object`; `Blank_fields_load_as_null`; `Implicit_string_item`; `Validator_dangling_rule_refs_warn` (all five `{who}` forms, severity Warning); `Validator_checklist_unusable_and_duplicate_ids`; `Validator_checklist_links_dangling`; `ChecklistText_renders_links_cuts_and_keeps_missing`; `PropertyLabels_text_range_key_and_no_match`; `Validator_labels_table_missing_and_two_d`; `Validator_holders_unknown_and_pieces_scope`; `Validator_holder_binding_output_and_call_not_kept` (named holder and global); `KeptBy_null_means_all`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~AuthoringFields"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests; build `src/Quartermaster.Core`, `src/Quartermaster.Render`, `src/Quartermaster.Desktop`, `src/Quartermaster.App` (`-c Release`) → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): rule refs, checklist items, labels, kept holders, clone mode`

### Task 2: Core — ticks by id, failure flag, kept holders, labelled working

**Files:**
- Modify: `src/Quartermaster.Core/Commands.cs` — `TickChecklist(..., [property: JsonIgnore(WhenWritingNull)] string? ItemId = null)`; `AddLogLine(..., [property: JsonIgnore(WhenWritingDefault)] bool Error = false)`
- Modify: `src/Quartermaster.Core/GameLog.cs` — `LogEntry(..., [property: JsonIgnore(WhenWritingDefault)] bool Error = false)`; `LogBuilder.Make`/`Line` take `error`
- Modify: `src/Quartermaster.Core/GameState.cs` — `List<string>? TickedIds` (`WhenWritingNull`); `TickChecklist` reducer: `ItemId` → `TickedIds` (null when emptied), else `Ticked`; `DoAdvance` sets `TickedIds = null`; `AddLogLine` passes `Error`
- Create: `src/Quartermaster.Core/ChecklistState.cs` — `public static bool IsTicked(GameState s, ChecklistItem item, int index)`
- Modify: `src/Quartermaster.Core/Decisions.cs` — `IsTicked`; decision text `ChecklistText.Render(pack, item.Text)`
- Modify: `src/Quartermaster.Render/PhaseController.cs` — `Tick(index, ticked)` sends `ItemId: item.Id` and `Item: item.Text`
- Modify: `src/Quartermaster.Core/PlayerView.cs` — `Outcome.Redact` lines get `Error = false`; class summary gains the rule
- Modify: `src/Quartermaster.Core/FormulaRunner.cs` — Holder/Global binding read, `RunContext` `holder()` read and Holder/Global output check `PropertyFacts.KeptBy` → `EvalError("{holder} does not keep {property}")`; working uses labels
- Modify: `src/Quartermaster.Rules/Expr/WorkingLine.cs` — overload `Render(AstExpr e, string source, Func<string, Value?> values, Func<string, Value, string?>? label)` (a `VarRef` with a label writes `{value} ({label})`); FormulaRunner passes a label function for Unit/Global/Holder/Piece bindings of a property with `Labels`, and the Set-output part becomes `{prop} = {PropertyLabels.Text(...)}`
- Test: `tests/Quartermaster.Core.Tests/AuthoringCoreTests.cs`
- [ ] **Step 1: Failing tests**: `Tick_by_id_survives_reorder`; `Tick_without_id_uses_index`; `TickedIds_cleared_on_advance`; `Decision_text_renders_links`; `Old_commands_and_log_serialise_unchanged` (TickChecklist without ItemId, AddLogLine/LogEntry with Error false, GameState with TickedIds null: JSON has no `itemId`/`error`/`tickedIds`); `Redacted_failure_line_drops_error_flag`; `Kept_failure_line_keeps_error_flag`; `Holder_output_not_kept_fails_run`; `Holder_binding_not_kept_fails_run`; `Global_output_not_kept_fails_run`; `Holder_call_not_kept_fails_run`; `Working_shows_label_for_variable_and_set_output`; `Add_output_stays_bare_number`; `Guest_hidden_working_with_label_redacted`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~AuthoringCore"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests; Rules.Tests `--filter "FullyQualifiedName~WorkingLine"`; build Render, Desktop, App → PASS.
- [ ] **Step 5: Commit** `feat(core): checklist ticks by id, log failure flag, kept holders, labels in working`

### Task 3: Clone copies ledger values

**Files:**
- Modify: `src/Quartermaster.Core/Commands.cs` — `AddPiece(..., [property: JsonIgnore(WhenWritingNull)] string? CloneOf = null)`; `SeedLedger(Seeds, [property: JsonIgnore(WhenWritingNull)] string? CloneOf = null)`
- Modify: `src/Quartermaster.Core/PieceActions.cs` — the `CloneDef` action emits `AddPiece(..., CloneOf: id)`
- Modify: `src/Quartermaster.Core/Ledger.cs` — `public static SeedLedger? BuildCloneSeeds(GameState s, RulesPack pack, ModuleModel? model, string newPieceId, string sourcePieceId, DiagnosticLog? log = null)`: stored values of the source for Pieces/Both, non-computed properties with `CloneAs == Copy`, pack order; side check with `Ownership.PieceSides` on `s.DeepClone()` with the seeds applied vs. the source's side → mismatch: null + the clone diagnostic
- Modify: `src/Quartermaster.Core/GameSession.cs` — `IsFolding`: a `SeedLedger` with `CloneOf` is not folding (it must replay on redo; `HistoryController`'s redo `SeedLedgers` cannot rebuild it)
- Modify: `src/Quartermaster.Render/ModuleSession.cs` — `public void SeedAfterAdd(AddPiece added)` (same guards as `SeedLedgers`; executes `BuildCloneSeeds` when `CloneOf` is set and the source exists, then `SeedLedgers()`)
- Modify: `src/Quartermaster.Render/Actions/LocalActions.cs` (`PieceCommand`, `PieceCommands`: `SeedAfterAdd` for each `AddPiece`, inside the call's action), `src/Quartermaster.Render/Online/IntentExecutor.cs` (`CheckAdd`: `CloneOf` set → `Actable(g, a, CloneOf)` text when not actable/visible; type string ≠ source's → `That piece can't be placed.`; then `SeedAfterAdd`)
- Test: `tests/Quartermaster.Core.Tests/CloneSeedTests.cs`, `tests/Quartermaster.Render.Tests/CloneIntentTests.cs`
- [ ] **Step 1: Failing tests**: Core `Clone_copies_stored_values`; `CloneAs_default_seeds_as_new_counter`; `Computed_and_holder_properties_not_copied`; `Clone_changing_side_copies_nothing`; `Clone_action_sets_CloneOf`; `AddPiece_and_SeedLedger_without_CloneOf_serialise_unchanged`; `Clone_values_hidden_from_other_side` (PlayerView); Render `Local_clone_copies_in_one_action`; `Undo_redo_of_clone_keeps_copied_values`; `Guest_clone_of_unheld_piece_refused`; `Guest_clone_of_own_piece_copies`.
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~CloneSeed"`, Render `--filter "FullyQualifiedName~CloneIntent"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests, full Render.Tests → PASS.
- [ ] **Step 5: Commit** `feat: Clone copies the counter's ledger values`

### Task 4: Failure flags in the controller and the stable PackHarness

**Files:**
- Modify: `src/Quartermaster.Render/PhaseController.cs` — `Calc(text, pieceId, formulaId, bool error = false)`; `error: true` on `{f.Name}: {u.Label}: {trace.Error}` (failed and refused), `… not found — skipped`, `… is not valid — skipped`
- Create: `src/Quartermaster.Render/PackHarness.cs` — `public sealed class PackHarness : IDisposable` (doc comment: stable API for packs tested outside this repo):
  - `public static PackHarness Open(string vmodPath, string rulesPath, string? cacheDir = null)` (null: a temp folder deleted on `Dispose`)
  - `public PackHarness NewGame(string? setup = null, Difficulty difficulty = Difficulty.FullyAuto)` (`LoadSetup` by `SetupDef.Name`, case-insensitive; else `StartNewGame`)
  - `public HarnessRun Run(string formulaId, string? pieceName = null, IReadOnlyDictionary<string, string>? inputs = null, IReadOnlyList<int>? dice = null)` (piece by unique `PieceInfo.Name`; `RunRequest.Dice` serves `dice` in order, running out throws; applies with `PhaseController.ApplyFormula(null, pieceId, trace, false)`)
  - `public sealed record HarnessRun(bool Applied, bool Refused, string? Error, string? Result, string Working, IReadOnlyList<string> Missing, int DiceUsed)`
  - `public string? Advance()`; `public string? Phase { get; }`; `public IReadOnlyList<PendingRun> Pending { get; }`
  - `public double? Value(string holderOrPieceName, string propertyName)` (holder by name, `Global` = global holder; else unique piece; `DisplayValues.Get`)
  - `public IReadOnlyList<LogEntry> Log { get; }`; `public GameState ViewFor(params string[] sideIds)` (`PlayerView.For`); `public ModuleSession Session { get; }` (documented as not stable)
- Test: `tests/Quartermaster.Render.Tests/PhaseControllerErrorFlagTests.cs`, `tests/Quartermaster.Docs.Tests/PackHarnessTests.cs` (Riverland demo)
- [ ] **Step 1: Failing tests**: Render `Failed_auto_run_line_has_error_flag`; `Refused_auto_run_line_has_error_flag`; `Missing_formula_line_has_error_flag`; `Ordinary_calc_line_has_no_flag`; Docs `Harness_runs_demo_formula_and_reads_value`; `Harness_inputs_and_missing`; `Harness_dice_pinned_and_run_out_throws`; `Harness_advance_queues_and_flags_errors`; `Harness_unknown_and_ambiguous_names_throw` (all harness texts); `Harness_ViewFor_hides_other_side`; `Harness_public_surface_is_pinned` (reflection list of public members).
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~ErrorFlag"`, Docs `--filter "FullyQualifiedName~PackHarness"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Render.Tests → PASS.
- [ ] **Step 5: Commit** `feat(render): failure flags on run lines; stable PackHarness`

### Task 5: Displays, Workbench, docs, full runs

**Files:**
- Modify: `src/Quartermaster.Core/LedgerTable.cs` (`LedgerRow.NotKept : IReadOnlySet<int>?` — holder cells of properties the holder does not keep: value null), `src/Quartermaster.Core/LedgerTabs.cs` (Holders and user tabs drop a holder-only column no shown row keeps), `src/Quartermaster.Core/LedgerBreakdown.cs` (title value with label)
- Modify: `src/Quartermaster.Render/LedgerViewRules.cs` (manual edit of a not-kept cell refused `{holder} does not keep {property}`, `Reload: true`), `src/Quartermaster.Render/OutlinerViewRules.cs` and `src/Quartermaster.Desktop/Outliner/OutlinerPanel.cs` (value text via `PropertyLabels.Text`), `src/Quartermaster.Render/CounterPopupLayout.cs` (`LedgerText` with label), `src/Quartermaster.Desktop/Ledger/LedgerWindow.cs` (cell text via `PropertyLabels.Text`; not-kept cells blank and read-only)
- Modify: `src/Quartermaster.Desktop/Workbench/PropertiesTab.cs` (property columns `Rule ref`, `Labels` (1-column tables), `Holders`, `Clone as` with the tooltips; holder grid `Rule ref` column; unknown holder status), `AlertsTab.cs` (`Rule ref:` picker), `PhasesTab.cs` (warning grid `Rule ref` column; checklist list rows `{id}: {shown text}`; details `Id:`, `Rule ref:`, `Notes:`, `Links:`; id statuses), `FormulasTab.Grids.cs` (holder binding/output property items filtered by `KeptBy`); `src/Quartermaster.Desktop/Phases/PhasePanel.cs` (rendered checklist text); a shared `RuleRefPicker` helper in `src/Quartermaster.Desktop/Workbench/RuleRefRow.cs` (items `(none)`, `{label}, page {page}`)
- Modify: `src/Quartermaster.App/PhasePanel.cs` (rendered text; build only otherwise)
- Modify: `docs/wiki/Making-a-Rules-Pack.md` (sections: "Rule references", "Checklist item ids and links", "Labels for numbers", "Properties some holders keep", "Clone and ledger values", "Testing a pack outside the engine (stable)" with the `PackHarness` API; invented Riverland examples: Weather + "Weather names" 1 Clear/2 Rain/3 Fog, "Ferry tolls" kept by the North depot, a "Spare boats" item linked from another), `docs/wiki/Window-Rules-Workbench.md` (new columns, checklist details), `docs/wiki/Window-Ledger.md` (labels, blank kept cells), `docs/wiki/Window-Phases.md` (checklist links); `docs/superpowers/RESUME.md` (new CURRENT STATE for B1, old under Previous)
- Test: Core `LedgerTable_not_kept_cell_blank`, `LedgerTabs_drop_unkept_holder_column`, `Breakdown_title_label`; Render `Ledger_edit_not_kept_refused`, `Outliner_value_shows_label`, `Popup_ledger_line_shows_label`; Desktop `PropertiesTab_new_columns_commit` (rule ref, labels, holders, clone as), `PropertiesTab_unknown_holder_refused`, `HolderGrid_rule_ref_column`, `AlertsTab_rule_ref_picker`, `PhasesTab_warning_rule_ref_column`, `PhasesTab_checklist_details_and_links`, `PhasesTab_checklist_id_statuses`, `FormulasTab_holder_property_items_filtered`, `PhasePanel_shows_rendered_links`, `LedgerWindow_label_and_not_kept_cell`
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Core `--filter "FullyQualifiedName~LedgerTab|FullyQualifiedName~LedgerTable|FullyQualifiedName~Breakdown"`, Render `--filter "FullyQualifiedName~LedgerView|FullyQualifiedName~Outliner|FullyQualifiedName~Popup"`, Desktop `--filter "FullyQualifiedName~PropertiesTab|FullyQualifiedName~AlertsTab|FullyQualifiedName~PhasesTab|FullyQualifiedName~FormulasTab|FullyQualifiedName~PhasePanel|FullyQualifiedName~LedgerWindow"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME.
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, Desktop `MainWindowOnlineTests.Host_players_dialog_and_guest_tooltip`, Desktop `TurnsEndToEndTests.Round_trip_through_mailbox` — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat: B1 editors and displays; docs`
