# Engine Quick Wins (filter defaults, table index, validator fixes, always-ask inputs, choices with commas) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A batch of small engine fixes found while writing a large rules pack: filters read a declared property's Default on an untouched counter; table lookups stop re-parsing every key; the validator understands table names in filter `lookup(...)`, auto values and input-driven on-move formulas; an Input can be marked "always ask"; Workbench choices may contain commas; the property-sheet/ledger rule is pinned and explained.

**Architecture:** Engine-side changes live in `Quartermaster.Rules` (model, table lookup, validator), `Quartermaster.Core` (filter context, runner, player view flag) and `Quartermaster.Render` (Workbench details text, formula dialog model). UI changes are limited to hint/tooltip text in the Avalonia app (`src/Quartermaster.Desktop`) plus the shared Details hint text in both Workbench grids. No new commands, no new save sections.

**Tech Stack:** C# .NET 10, System.Text.Json, xUnit (+ Avalonia.Headless `[AvaloniaFact]` for the one dialog test).

**Spec:** the engine wishlist (kept outside the repo), items 4 (second paragraph only), 14, 22, 23, 24, 25, 38 and 58, as summarised in each task below. The plan argues from that summary; the wishlist text is not needed to execute it.

## Global Constraints

- Public repo, hard boundary: no content from any real game anywhere (code, tests, docs, commit messages). Tests use invented TEST data (`RunnerFx`, `PhaseFixture`, the validator's `Clean()` pack, invented table rows such as `Unit-0001`).
- Old saves and old rules packs must still load, and an unchanged pack must keep its SHA-256 (`RulesPackIo.ComputeSha256`; a changed hash makes every existing save warn "Save was made with a different version of the rules pack"). Every new serialized member is `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]`. `RulesPackIo.CurrentFormat` stays 4.
- No new NuGet packages. Never open visible windows: UI tests are `[AvaloniaFact]`.
- Disk is tight (~4.5 GB): run one test project at a time with a `--filter` while iterating, `-c Release`; build only affected projects; no full-solution rebuild loops.
- Never kill/stop/close a process you did not start (the user runs games). Never `git stash`. Never push.
- Commits: repo-local noreply identity; message ends with a blank line then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never commit `startapp.cmd` or `.superpowers/`.
- Branch `wishlist-quick-wins` (from master `cb65458`).

## Scheduling (file overlaps)

| Task | Main files | Can run with |
|---|---|---|
| 1 Filter defaults | `Core/PieceFilter.cs`, `Core/GameState.cs`, `Core/PlayerView.cs` | 2, 3, 4, 5 |
| 2 Table key index | `Rules/Tables/TableLookup.cs`, `Rules/Model/TableDef.cs` | 1, 3, 4, 5 |
| 3 Validator: tables in filters | `Rules/Validation/RulesValidator.cs`, `ValidatorTests.cs` | 1, 2, 4, 5 |
| 4 Choices with commas | `Render/FormulaEdit.cs`, both `Workbench/FormulasTab.Grids.cs` | 1, 2, 3, 5 |
| 5 Property sheet rule | `Desktop/Shell/PropertySheetDialog.cs`, `Render/PieceMenuModel.cs` | 1, 2, 3, 4 |
| 6 Validator: binding use | `RulesValidator.cs`, `ValidatorTests.cs`, `FormulaEdit.cs` | after 3 and 4 |
| 7 Always-ask inputs | `Rules/Model/FormulaDef.cs`, `Core/FormulaRunner.cs`, `FormulaEdit.cs`, `Render/FormulaDialogModel.cs`, both grids | after 4 and 6 (and after 1: both touch Core) |
| 8 Docs | `docs/wiki/*.md`, `docs/superpowers/RESUME.md` | last |

Tasks in the same wave touch different files, but parallel implementers in **one** working tree share `bin/`/`obj/`: give each its own worktree, or run them one after another.

## Design decisions (made here; implementers do not revisit)

1. **Filter Default fallback (task 1).** In a filter (`PieceFilter`'s context: applies-to, side unit filters, phase entry filters, warning and alert conditions, Ledger tab filters), a bare property name with no ledger value on the piece reads the property's `Default` **when the property is declared on that piece**: scope `Pieces` or `Both`, and its own `AppliesTo` is empty or matches the piece. `has(x)` keeps meaning "has a ledger value" (unchanged). Formula bindings already use `Ledger.GetOrDefault` (`FormulaRunner.Bind`), so they only get a pinning test. In a player's filtered view (hot-seat view, online guest snapshot, viewer copy) a missing **Private** value may be hidden rather than unset, so it does not fall back there (it errors as today); views get a serialized-when-true flag `GameState.IsPlayerView` set by `PlayerView.For` when it actually filters.
2. **Table index (task 2).** Parsed keys are cached per key list instance (`ConditionalWeakTable<List<string>, KeyIndex>`, validated by `Count`). This is sound because nothing in `src/` edits `RowKeys`/`ColumnKeys` in place: `TableEdit` always builds new lists (checked with grep; the `TableDef` doc comment will say so). Text keys go in a case-insensitive dictionary (first index wins); non-text keys are scanned only up to the dictionary hit, so "first matching key wins" is preserved exactly.
3. **No `intable(...)` function.** It would need a new `IEvalContext` member and the table-name special case repeated in `Parser.Variables`, `WorkingLine`, `FormulaLayout`, `KeyChoices`, `LedgerTabs`, `FormulaMasking` and the validator. With the index, `lookup(...)` in a filter is fast and task 3 makes it validate, so `intable` is not cheap and not needed.
4. **Property sheet vs ledger (task 5): keep the read-only rule, do not re-seed.** Both property sheet dialogs already show a ledger-managed row read-only with "(managed by ledger: X)" and write its old text back (`PieceMenuModel.ManagedSheetValue`, WinForms `PropertySheetDialog.cs:27`, Avalonia `Shell/PropertySheetDialog.cs`), so a sheet edit cannot change a managed row. A re-seed on sheet write would be unsound: after the first seed the ledger is the record (formula results, manual edits, history, undo), the sheet text is never written back and goes stale, so re-seeding from it would overwrite formula results with stale text whenever any other row of that sheet is edited, or, with a diff, never fire at all because managed rows are read-only. The task pins this with a test and makes the read-only row say where to change the value.
5. **Always ask (task 7).** `VariableBinding.AlwaysAsk` (bool, Input only). The runner never fills an always-ask input from its `AutoValue`, so Fully automatic queues the unit and the missing-input dialog asks; the Formula dialog's "Calculate" leaves it empty. It is edited in the Workbench Details cell as a `| always ask` part (both Workbench apps share `FormulaEdit.FormatDetails`/`ParseDetails`, so no new grid column), and the Formula dialog shows " (always asked)" after the input's caption.
6. **Choices with commas (task 4).** The Details cell stays one line. A choice that contains `,`, `|`, `..`, starts with `"`, or equals `always ask` is written in double quotes with `"` doubled (`"North, coast"`, `"Say ""hi"""`). Only a token that starts with `"` is a quoted token, so every Details text written before this change parses exactly as before.
7. **On-move warning (task 6).** "on-move formula has no Moved binding" is raised only when the formula has no Moved, no Input and no Unit/Global/Holder binding.
8. **Auto values count as used (task 6).** Variables in a binding's `AutoValue` count as used (validator and Workbench "(unused)"/"Remove unused bindings"), except the binding's own variable; an auto value naming a variable with no binding is an error like any other used name; `lookup` table names in auto values are checked.

## Review Focus

1. **A property whose own applies-to reads itself (or two properties reading each other's) in a filter** → no stack overflow; the property counts as not declared while its own applies-to is being checked, so the filter gives false, as before. Test in task 1 (`Self_referencing_applies_to_does_not_recurse`).
2. **Hidden values in a player's view** → a hot-seat/online view must not turn another side's hidden Private value into its Default (it would misplace counters in side filters). Test in task 1 (`Player_view_does_not_default_private_values`) and the flag's save round-trip test.
3. **Old Details text and old packs** → `low, mid, high`, `0..20 | auto: 2`, and a choice with a quote in the middle (`say "hi"`) parse as before; an unchanged pack hashes the same after task 7. Tests in tasks 4 and 7.
4. **A table edited while a game is open** → lookups see the edit at once (new key lists, new index); keys that mix ranges, numbers and text still pick the first matching row. Tests in task 2.
5. **Fully automatic with an always-ask input and several units** → one queued run, the missing-input dialog asks once and applies to every unit; the auto value is not used silently. Test in task 7.

---

### Task 1: Filters read a declared property's Default

**Files:**
- Modify: `src/Quartermaster.Core/PieceFilter.cs` (`FilterContext`), `src/Quartermaster.Core/GameState.cs` (new member), `src/Quartermaster.Core/PlayerView.cs` (`For`, `Lockdown`)
- Test: `tests/Quartermaster.Core.Tests/PieceFilterTests.cs`, `tests/Quartermaster.Core.Tests/PlayerViewTests.cs`, `tests/Quartermaster.Core.Tests/FormulaRunnerTests.cs`

**Interfaces:**
- Produces: `public bool IsPlayerView { get; set; }` on `GameState`, attributed `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]`, doc: "True for a player's filtered view (PlayerView.For); a missing Private value there may be hidden, so filters do not read its Default."
- `PieceFilter.HasProperty` unchanged (no default fallback).

- [ ] **Step 1: Write the failing tests** in `PieceFilterTests.cs` (fixture: `RunnerFx.Pack()` with extra invented properties added in the test; `Marked()` piece; no ledger values unless stated):
  - `Declared_property_without_value_reads_its_Default`: add `new PropertyDef { Id = "owner", Name = "Owner", Scope = PropertyScope.Pieces, Default = 0, Visibility = Visibility.Public }`; `Assert.True(M("Owner == 0", p, s))`, `Assert.False(M("Owner == 1", p, s))`, `Assert.False(M("has(Owner)", p, s))`; after `s.Ledgers[LedgerKey.Piece("m1")] = new() { ["owner"] = 1 }` → `Assert.True(M("Owner == 1", p, s))`. Also `Fuel == 0` is true on the untouched piece (Fuel: Pieces, Default 0).
  - `Holder_property_has_no_default_in_a_filter`: `Assert.False(M("Supply == 0", p, s))` (Supply is Holders-scoped).
  - `Property_not_applying_to_piece_has_no_default`: property `Ammo` (Pieces, Default 3, `AppliesTo = "name() == \"Tank\""`) → `Assert.False(M("Ammo == 3", p, s))` for the "Rifle" piece; with `AppliesTo = "name() == \"Rifle\""` → true.
  - `Applies_to_reading_another_default_works`: `Kind` (Pieces, Default 2) and `Load` (Pieces, Default 5, `AppliesTo = "Kind == 2"`) → `Assert.True(M("Load == 5", p, s))`.
  - `Self_referencing_applies_to_does_not_recurse`: `Loop` (Pieces, Default 1, `AppliesTo = "Loop == 1"`) and `A`/`B` whose applies-to read each other → `M("Loop == 1", p, s)` and `M("A == 0", p, s)` return false without throwing.
  - `Player_view_does_not_default_private_values`: `s.IsPlayerView = true`; a Private `Secret` (Default 0) → `Assert.False(M("Secret == 0", p, s))`; a Public one → true.
- [ ] **Step 2: Write the failing view/save tests** in `PlayerViewTests.cs`:
  - `Filtered_view_is_marked_and_full_state_is_not`: with the file's two-side `Pack()`, `PlayerView.For(full, pack, Model, Red).IsPlayerView` is true; `PlayerView.For(full, pack, Model, Both).IsPlayerView` is false; `full.IsPlayerView` stays false.
  - `Player_view_flag_is_written_only_when_set`: `Json(NewState())` does not contain `"isPlayerView"`; a view's JSON does, and `JsonSerializer.Deserialize<GameState>(json, QuartermasterJson.Options)!.IsPlayerView` is true.
  - The lockdown path (`PlayerView.FailForTest = true`) also returns `IsPlayerView == true` (reset the hook in `finally`).
- [ ] **Step 3: Pinning test for bindings** in `FormulaRunnerTests.cs`: `Unit_binding_reads_Default_on_untouched_piece` — a `Unit` binding on `fuel` (Default 0, change the def's Default to 4 in the test pack) on a piece with no ledger value gives 4. (Expected to pass already; it guards the documented behaviour.)
- [ ] **Step 4: Run to see the new filter/view tests fail**
  Run: `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~PieceFilterTests|FullyQualifiedName~PlayerViewTests|FullyQualifiedName~Unit_binding_reads_Default"`
  Expected: the new PieceFilter and PlayerView tests FAIL (compile error on `IsPlayerView` first).
- [ ] **Step 5: Implement.**
  - `GameState.IsPlayerView` as above.
  - `PlayerView.For`: set `view.IsPlayerView = true` after `Filter.Apply` and in the lockdown branch; the two early returns (no pack, every side held) leave it false.
  - `FilterContext.GetVariable`: when `Ledger.Get` is null, return `Value.Of(def.Default)` if `Declared(def)` and not (`s.IsPlayerView && def.Visibility == Visibility.Private`); else throw the existing `"{name} has no value on this piece"`.
  - `Declared(PropertyDef def)`: false for `PropertyScope.Holders`; true for blank `AppliesTo`; otherwise evaluate the parsed applies-to with **this same context** and return its truthiness (any `EvalError`/`ParseError` → false). Guard with a per-context `HashSet<string>` of property ids being checked (re-entry → false) and cache results per id in a per-context dictionary. Cache parsed applies-to expressions per `PropertyDef` instance in a static `ConditionalWeakTable<PropertyDef, Expr?>` (records are replaced on edit, so the cache never goes stale).
- [ ] **Step 6: Run the focused tests, then the whole Core project once**
  Run: the Step 4 command, then `dotnet test tests/Quartermaster.Core.Tests -c Release`
  Expected: PASS (existing `PieceFilter_MarkerAndName` still passes: `Supply` is Holders-scoped).
- [ ] **Step 7: Run the Render tests that use filters** (seeding, side ownership, alerts): `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~Alert|FullyQualifiedName~Ledger|FullyQualifiedName~HotSeat|FullyQualifiedName~Guest|FullyQualifiedName~Phase"` → PASS. If a test relied on "no value → filter false" for a declared property, report it rather than weakening the new rule.
- [ ] **Step 8: Commit**
  ```bash
  git add src/Quartermaster.Core/PieceFilter.cs src/Quartermaster.Core/GameState.cs src/Quartermaster.Core/PlayerView.cs tests/Quartermaster.Core.Tests/PieceFilterTests.cs tests/Quartermaster.Core.Tests/PlayerViewTests.cs tests/Quartermaster.Core.Tests/FormulaRunnerTests.cs
  git commit -m "feat(core): filters read a declared property's default"
  ```

### Task 2: Table lookups parse keys once

**Files:**
- Modify: `src/Quartermaster.Rules/Tables/TableLookup.cs` (`FindIndex`), `src/Quartermaster.Rules/Model/TableDef.cs` (doc comment only)
- Create: `tests/Quartermaster.Rules.Tests/TableLookupIndexTests.cs`

**Interfaces:**
- Public API unchanged: `TableLookup.Lookup`, `Locate`, `CellAt`, `ParseKey`, `Matches` keep their signatures and results.
- Internal: `private sealed class KeyIndex` (parsed `TableKey[]`, `Dictionary<string,int>` of text keys with `StringComparer.OrdinalIgnoreCase`, first index wins, `int[]` of non-text key indices, the list `Count` it was built from); `private static readonly ConditionalWeakTable<List<string>, KeyIndex>`.

- [ ] **Step 1: Write the failing tests** (`TableLookupIndexTests`, invented data):
  - `Indexed_lookup_matches_linear_scan_on_mixed_keys`: keys `{ "1-3", "Forest", "2", "4+", "forest", "<=0", "Hill", "5" }`; for each value in `{ 0, 1, 2, 3, 4, 5, 9, -1, 2.5 }` as numbers plus texts `"2"`, `"FOREST"`, `"  Hill "`, `"5"`, `"Swamp"`, `"4"` and `Value.Of(true)`, the row found by `Locate` (or `EvalError`) equals the reference `keys.FindIndex(k => TableLookup.Matches(TableLookup.ParseKey(k), v))` (-1 ↔ `EvalError` "No row for ...").
  - `Text_key_lookup_is_case_insensitive_and_first_wins`: `"Forest"` at row 1 and `"forest"` at row 4 → `"FOREST"` locates row 1.
  - `Edited_table_is_seen_at_once`: look up `"Hill"` in a table, then `TableEdit.SetRowKey(t, row, "Ridge")` → `"Ridge"` is found in the new table and `"Hill"` is not; the old `TableDef` still finds `"Hill"`.
  - `Two_dimensional_columns_use_the_index`: a TwoD table with text column keys `{ "Clear", "Rough" }` → `Lookup(t, row, Value.Of("rough"))` gives the Rough cell.
  - `Big_text_table_lookup_is_fast` (perf guard): a OneD table with 1000 rows keyed `Unit-0000` … `Unit-0999` (values = row number); one warm-up lookup, then 10,000 lookups of `"unit-0999"` with a `Stopwatch` → `Assert.True(elapsed < TimeSpan.FromMilliseconds(500), ...)` (the old code parses 1000 keys per call, about 10 s here) and each returns 999.
- [ ] **Step 2: Run to verify the perf guard fails**
  Run: `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~TableLookupIndexTests"`
  Expected: `Big_text_table_lookup_is_fast` FAILS on time; the others pass already (they pin behaviour).
- [ ] **Step 3: Implement** `FindIndex(List<string> keys, Value v)` via the index: get-or-build (rebuild when `Count` differs); text form of `v` is `v.Display()` for numbers, `Normalise(v.Text)` for text, none for booleans; `hit` = dictionary index or `int.MaxValue`; scan the non-text indices in order while `< hit`, returning the first that `Matches`; else `hit` (or -1). Add to `TableDef`'s summary: "RowKeys and ColumnKeys are replaced, never edited in place (TableLookup caches parsed keys per list)."
- [ ] **Step 4: Run the Rules tests**
  Run: `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~TableLookupIndexTests|FullyQualifiedName~TableTests|FullyQualifiedName~TableEdit"` then `dotnet test tests/Quartermaster.Rules.Tests -c Release`
  Expected: PASS.
- [ ] **Step 5: Commit**
  ```bash
  git add src/Quartermaster.Rules/Tables/TableLookup.cs src/Quartermaster.Rules/Model/TableDef.cs tests/Quartermaster.Rules.Tests/TableLookupIndexTests.cs
  git commit -m "perf(rules): table lookups parse keys once per table"
  ```

### Task 3: Validator knows table names in filter lookups

**Files:**
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (`UnknownPropertyRefs` and its three callers)
- Test: `tests/Quartermaster.Rules.Tests/ValidatorTests.cs`

**Interfaces:**
- Replace `UnknownPropertyRefs(string, RulesPack) : IEnumerable<string>` with `private static IEnumerable<string> FilterRefProblems(string text, RulesPack pack)` yielding message tails: `refers to unknown property "X"` or `refers to unknown table "X"`. Callers prefix as today (`Property "P": applies-to `, `{who}: {what} `, formula `applies-to `). Existing message texts for unknown properties stay byte-identical.

- [ ] **Step 1: Write the failing tests:**
  - `Validator_LookupTableInFilters_IsNotAProperty`: in `Clean()` (table `Rates`, property `Steps`), use `lookup(Rates, Steps) > 1` as a phase warning condition, an on-start filter, an on-move filter, an alert condition, a side unit filter, a formula applies-to and a property applies-to → `Assert.DoesNotContain(msgs, m => m.Contains("Rates"))`.
  - `Validator_LookupUnknownTableInFilter_Error`: warning condition `lookup(Nope, Steps) > 1` → contains `Phase "Alpha": warning condition refers to unknown table "Nope"`, and no `unknown property "Nope"`.
  - `Validator_LookupRowArgumentStillChecked`: `lookup(Rates, Ghost) > 1` as an alert condition → contains `Alert "Low": condition refers to unknown property "Ghost"` (use the file's alert helper `A(...)`).
- [ ] **Step 2: Run to verify they fail**
  Run: `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~ValidatorTests"`
  Expected: the first two FAIL ("unknown property \"Rates\"" / "\"Nope\"").
- [ ] **Step 3: Implement**: in the walk, for `Call { Function: "lookup" }` with `Args[0] is VarRef tv`: table missing (`pack.TableByName`) → yield the unknown-table problem; then walk `Args[1..]` only. Everything else unchanged.
- [ ] **Step 4: Run** the Step 2 command → PASS (all existing validator tests too).
- [ ] **Step 5: Commit**
  ```bash
  git add src/Quartermaster.Rules/Validation/RulesValidator.cs tests/Quartermaster.Rules.Tests/ValidatorTests.cs
  git commit -m "fix(rules): validator accepts table names in filter lookups"
  ```

### Task 4: Workbench choices may contain commas

**Files:**
- Modify: `src/Quartermaster.Render/FormulaEdit.cs` (`FormatDetails`, `ParseDetails`, `AutoBar`), `src/Quartermaster.Desktop/Workbench/FormulasTab.Grids.cs` and `src/Quartermaster.App/Workbench/FormulasTab.Grids.cs` (`DetailsHint` Input text only)
- Test: `tests/Quartermaster.Render.Tests/FormulaEditTests.cs`

**Interfaces:**
- Produces (used by task 7): `internal static IReadOnlyList<string> SplitOutsideQuotes(string text, char separator)` and `internal static string QuoteChoice(string choice)` in `FormulaEdit`; quoting rule = Design decision 6. `AutoBar` ignores `|` inside quotes. `ParseDetails` finds `..` only outside quotes.
- New error text: `A choice has an opening quote but no closing quote.`
- New Input hint (both grids): `A range such as 0..20, or choices separated by commas (put a choice that holds a comma in "double quotes"); add | auto: expression for the automatic value.`

- [ ] **Step 1: Write the failing tests:**
  - `Choices_with_commas_round_trip`: `Choices = { "North, coast", "Inland", "Say \"hi\", then go", "a|b", "1..2" }` → `FormatDetails` gives `"North, coast", Inland, "Say ""hi"", then go", "a|b", "1..2"`; `ParseDetails` of that text gives back the same five choices, `Min`/`Max` null.
  - `Choices_with_commas_and_auto_value`: same choices plus `AutoValue = "\"Inland\""` round-trip through Format/Parse.
  - `Old_details_text_parses_as_before`: `low, mid, high` → 3 choices; `0..20 | auto: 2` → Min 0, Max 20, AutoValue `2`; `say "hi", bye` → choices `say "hi"` and `bye`.
  - `Unclosed_quote_is_an_error`: `"North, coast` → binding unchanged, error `A choice has an opening quote but no closing quote.`
- [ ] **Step 2: Run to verify they fail**
  Run: `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~FormulaEditTests"`
  Expected: the round-trip and unclosed-quote tests FAIL.
- [ ] **Step 3: Implement** the quote-aware split (a token is quoted only when its trimmed text starts with `"`; `""` inside is one `"`; non-space text after the closing quote, before the next separator, is the error `Put the whole choice inside the quotes.`), `QuoteChoice`, and the hint text in both grids.
- [ ] **Step 4: Run** the Step 2 command, then `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~Workbench|FullyQualifiedName~Formulas"` → PASS (fix any test that asserted the old hint text by updating it to the new text).
- [ ] **Step 5: Commit**
  ```bash
  git add src/Quartermaster.Render/FormulaEdit.cs src/Quartermaster.Desktop/Workbench/FormulasTab.Grids.cs src/Quartermaster.App/Workbench/FormulasTab.Grids.cs tests/Quartermaster.Render.Tests/FormulaEditTests.cs
  git commit -m "fix(workbench): input choices may contain commas"
  ```

### Task 5: Property sheet rows the ledger keeps stay read-only, and say why

**Files:**
- Modify: `src/Quartermaster.Render/PieceMenuModel.cs` (new constant), `src/Quartermaster.Desktop/Shell/PropertySheetDialog.cs` (tooltip on a managed box and its note)
- Test: `tests/Quartermaster.Render.Tests/PieceMenuModelTests.cs`, `tests/Quartermaster.Desktop.Tests/ContextMenuTests.cs`

**Interfaces:**
- Produces: `public const string ManagedSheetTip = "The Ledger keeps this value. Change it in the Ledger (right-click the counter > Show in Ledger) or with a formula.";` on `PieceMenuModel`.

- [ ] **Step 1: Write the failing/pinning tests:**
  - `PieceMenuModelTests.Sheet_write_never_changes_ledger_values` (uses the file's `Open(rules: true)`; `PhaseFixture.RedOne` has a sheet row `Fuel` = 40 managed by the pack's fuel property): set the ledger to 25 through `LocalActions.EditLedger` (or `Game.Execute(new LedgerDelta(...))`), then read the sheet with `PieceActions.ReadSheet`, change only a non-managed row (or none), `WriteSheet` and send it through `PieceCommand` → the ledger value is still 25, no new ledger history entry, and `ManagedSheetValue(..., "Fuel")` is `"25"`. One undo removes only the sheet write.
  - `ContextMenuTests.Property_sheet_dialog_lists_rows_and_returns_values`: add `Assert.Equal(PieceMenuModel.ManagedSheetTip, ToolTip.GetTip(boxes[0]))` and `Assert.Null(ToolTip.GetTip(boxes[1]))`.
- [ ] **Step 2: Run to verify** — `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~PieceMenuModelTests"` (expected PASS: pins the rule) and `dotnet test tests/Quartermaster.Desktop.Tests -c Release --filter "FullyQualifiedName~Property_sheet_dialog"` (expected FAIL: no tooltip / missing constant).
- [ ] **Step 3: Implement**: the constant; in the Avalonia dialog set `ToolTip.SetTip(box, PieceMenuModel.ManagedSheetTip)` and the same on the note when `managed != null`. WinForms is left as is (it already shows the same read-only row and note).
- [ ] **Step 4: Run** both Step 2 commands → PASS.
- [ ] **Step 5: Commit**
  ```bash
  git add src/Quartermaster.Render/PieceMenuModel.cs src/Quartermaster.Desktop/Shell/PropertySheetDialog.cs tests/Quartermaster.Render.Tests/PieceMenuModelTests.cs tests/Quartermaster.Desktop.Tests/ContextMenuTests.cs
  git commit -m "feat(desktop): ledger-kept sheet rows say where to change them"
  ```

### Task 6: Validator: input-driven on-move formulas and auto-value variables

**Depends on:** task 3 (same validator file) and task 4 (same `FormulaEdit.cs`).

**Files:**
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (`CheckPhase` on-move branch, `CheckFormula`), `src/Quartermaster.Render/FormulaEdit.cs` (`UsedVariables`)
- Test: `tests/Quartermaster.Rules.Tests/ValidatorTests.cs`, `tests/Quartermaster.Render.Tests/FormulaEditTests.cs`

**Interfaces:**
- `FormulaEdit.UsedVariables(FormulaDef)` keeps its signature; it now also adds, for every binding already in the list, the variables of its `AutoValue` (when it parses), excluding the binding's own variable, iterating like the table row/column case.

- [ ] **Step 1: Write the failing tests** (`ValidatorTests`):
  - Change `Validator_OnMoveWithoutMovedBinding_Warning` to use a formula with no Input/Unit/Global/Holder/Moved binding: `Fx("D", new VariableBinding { Variable = "D", Kind = BindingKind.Roll, DiceCount = 1, DiceSides = 6 })` (same expected message).
  - `Validator_OnMoveWithInputOrPropertyBinding_NoMovedWarning`: on-move entry for `f1` of `Clean()` (it has an Input) → no warning containing `Moved binding`; same for `Fx("F", new VariableBinding { Variable = "F", Kind = BindingKind.Unit, Property = "p2" })`.
  - `Validator_VariableUsedOnlyInAutoValue_IsUsed`: `Fx("A", In("A") with { AutoValue = "B + 1" }, new VariableBinding { Variable = "B", Kind = BindingKind.Unit, Property = "p2" })` → `Assert.Empty(V(WithFormula(f)))`.
  - `Validator_AutoValueSelfReference_NotUse`: `Fx("1", In("A") with { AutoValue = "A" })` → still warns `variable "A" is bound but not used in the formula`.
  - `Validator_AutoValueUnboundName_Error`: `Fx("A", In("A") with { AutoValue = "Ghost" })` → error `Formula "Fuel Use": variable "Ghost" has no binding`.
  - `Validator_AutoValueUnknownTable_Error`: `AutoValue = "lookup(Nope, 1)"` → error containing `no table named "Nope"`.
  - (`FormulaEditTests`) `Auto_value_variables_are_used`: the first formula above → `FormulaEdit.Unused(f)` is empty and `RemoveUnused(f)` keeps `B`.
- [ ] **Step 2: Run to verify they fail**
  Run: `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~ValidatorTests"` and `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~FormulaEditTests"`
  Expected: the new tests FAIL.
- [ ] **Step 3: Implement**: on-move condition per Design decision 7. In `CheckFormula`, for a binding with a parsable `AutoValue`: add its `Parser.Variables` except `b.Variable` to `used` and run `CheckLookups` on it (the existing "has no binding" pass over `used` then covers unbound names). `UsedVariables` per Interfaces.
- [ ] **Step 4: Run** both Step 2 commands → PASS.
- [ ] **Step 5: Commit**
  ```bash
  git add src/Quartermaster.Rules/Validation/RulesValidator.cs src/Quartermaster.Render/FormulaEdit.cs tests/Quartermaster.Rules.Tests/ValidatorTests.cs tests/Quartermaster.Render.Tests/FormulaEditTests.cs
  git commit -m "fix(rules): auto values and typed on-move inputs count as used"
  ```

### Task 7: Inputs that always ask

**Depends on:** tasks 4 and 6 (`FormulaEdit.cs`), task 1 (Core project, avoid parallel builds).

**Files:**
- Modify: `src/Quartermaster.Rules/Model/FormulaDef.cs`, `src/Quartermaster.Core/FormulaRunner.cs` (`RunScope.Input`), `src/Quartermaster.Render/FormulaEdit.cs` (`FormatDetails`, `ParseDetails`, `IsConfigured`), `src/Quartermaster.Render/FormulaDialogModel.cs` (`Box`), both `Workbench/FormulasTab.Grids.cs` (`DetailsHint`)
- Test: `tests/Quartermaster.Core.Tests/FormulaRunnerTests.cs`, `tests/Quartermaster.Rules.Tests/RulesPackIoTests.cs`, `tests/Quartermaster.Render.Tests/FormulaEditTests.cs`, `tests/Quartermaster.Render.Tests/MissingInputsTests.cs`, `tests/Quartermaster.Render.Tests/FormulaDialogModelTests.cs`

**Interfaces:**
- Produces: on `VariableBinding`: `/// <summary>Input only: asked in every difficulty; the AutoValue is never used to fill it.</summary> [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AlwaysAsk { get; init; }`.
- Details text: parts joined by `" | "` in this order, empty ones left out: basic (range or choices), `always ask`, `auto: expr`. Examples: `low, mid | always ask | auto: "mid"`, `always ask`, `0..9 | always ask`. Parsing: strip the `auto:` part (`AutoBar`), split the rest on `|` outside quotes; a part equal to `always ask` (trimmed, any case) sets the flag; at most one other part is the basic part; anything else → error `Use a range like 0..20, or choices separated by commas; add | always ask and/or | auto: expression.`
- `public const string AlwaysAskedSuffix = " (always asked)";` on `FormulaDialogModel`; an AlwaysAsk Input's `BoxView.CaptionSuffix`.
- Input hint (both grids) gains: `Add | always ask to ask the player every time, even in Fully automatic.`

- [ ] **Step 1: Write the failing tests:**
  - `FormulaRunnerTests.AlwaysAsk_ignores_auto_value`: `AutoPack("\"Normal\"")` with the binding `AlwaysAsk = true`, `UseAutoValues = true` → `MissingReason == "Waiting for input"`; typed `fast` → `Fast`.
  - `RulesPackIoTests.AlwaysAsk_round_trips_and_default_is_not_written`: a pack with one AlwaysAsk binding saves/loads with `AlwaysAsk == true`; `JsonSerializer.Serialize(formula, RulesPackIo.JsonOptions)` of a formula whose bindings leave it false contains no `alwaysAsk` (so an unchanged pack keeps its hash), and a formula JSON without the member loads with `AlwaysAsk == false`.
  - `FormulaEditTests.Always_ask_details_round_trip`: the three examples above round-trip; `always ask` alone → `AlwaysAsk` true, no choices; `0..9 | sometimes` → the new error; `SetKind` to Roll clears `AlwaysAsk`; a choice equal to `always ask` is written quoted and comes back as a choice.
  - `MissingInputsTests.AlwaysAsk_input_is_queued_and_asked_in_fully_automatic`: like `Make()` but keep `Terrain`'s AutoValue and set `AlwaysAsk = true` → the Movement run is `NeedsInput`, `MissingInputs.For` lists `Terrain`, and `ApplyAll` with `Terrain` typed applies every unit.
  - `FormulaDialogModelTests.Always_ask_input_shows_suffix_and_calculate_leaves_it_empty`: an AlwaysAsk Input box has `CaptionSuffix == FormulaDialogModel.AlwaysAskedSuffix`; after `Calculate(state)` its text is still empty and the status says `Waiting for: ...`.
- [ ] **Step 2: Run to verify they fail**
  Run each project with its filter: `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~AlwaysAsk"`, `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~AlwaysAsk"`, `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~AlwaysAsk|FullyQualifiedName~Always_ask"`
  Expected: compile errors on `AlwaysAsk`, then FAIL.
- [ ] **Step 3: Implement**: the member; in `RunScope.Input` treat `b.AlwaysAsk` like "no auto value" (`if (!r.UseAutoValues || b.AlwaysAsk || string.IsNullOrWhiteSpace(b.AutoValue))`); Details format/parse; `IsConfigured` counts `AlwaysAsk`; the caption suffix (typed boxes only, not when overridden); the hint text. `FormulaEdit.SetKind` already builds a fresh binding, so it clears the flag.
- [ ] **Step 4: Run** the Step 2 commands, then `dotnet test tests/Quartermaster.Render.Tests -c Release --filter "FullyQualifiedName~Formula|FullyQualifiedName~Missing|FullyQualifiedName~Batch|FullyQualifiedName~PhaseController|FullyQualifiedName~Guest"` → PASS.
- [ ] **Step 5: Commit**
  ```bash
  git add src/Quartermaster.Rules/Model/FormulaDef.cs src/Quartermaster.Core/FormulaRunner.cs src/Quartermaster.Render/FormulaEdit.cs src/Quartermaster.Render/FormulaDialogModel.cs src/Quartermaster.Desktop/Workbench/FormulasTab.Grids.cs src/Quartermaster.App/Workbench/FormulasTab.Grids.cs tests/
  git commit -m "feat(rules): inputs that always ask, even in Fully automatic"
  ```

### Task 8: Docs and resume note

**Files:**
- Modify: `docs/wiki/Formula-Reference.md`, `docs/wiki/Making-a-Rules-Pack.md`, `docs/wiki/Window-Rules-Workbench.md`, `docs/wiki/Window-Formula-Dialog.md`, `docs/wiki/Window-Map.md`, `docs/superpowers/RESUME.md`

- [ ] **Step 1: Wiki edits** (plain language, invented examples only, the wiki's existing demo names):
  - Formula-Reference "In a filter": a property the unit has but that has no value yet reads as its default (scope must include units and the property's own applies-to must match); `has(x)` still means "has a value in the ledger"; in a player's own view another side's private values do not fall back. Update the error row "x has no value on this piece" (now: the property does not apply to this unit, or is another side's hidden value). Add the row `refers to unknown table "x"`. Note that a binding's automatic value counts as using the names in it.
  - Making-a-Rules-Pack (Input bindings): `| always ask`, quoting a choice that holds a comma (`"North, coast", Inland`).
  - Window-Rules-Workbench: Details column text for Input (choices, quotes, `| always ask`, `| auto:`); the on-move warning appears only when the formula has no Moved, Input or property binding.
  - Window-Formula-Dialog: "(always asked)" inputs; in Fully automatic they open the missing-input dialog every time.
  - Window-Map: the managed sheet row is read-only; change the value in the Ledger or by a formula (the tooltip says so).
- [ ] **Step 2: Run the docs tests**
  Run: `dotnet test tests/Quartermaster.Docs.Tests -c Release`
  Expected: PASS (wiki lint, formula reference checks).
- [ ] **Step 3: RESUME**: add a new CURRENT STATE entry at the top of "Where we stopped" (move the old one under "Previous CURRENT STATE"): branch `wishlist-quick-wins`, the seven features in one line each, the decisions (read-only sheet rows instead of re-seed; no `intable`; no Private default in player views), test counts from the final full runs, "not pushed; next: user review, then merge/PR".
- [ ] **Step 4: Full test runs**, one project at a time: Rules, Core, Render, Desktop, Docs (`dotnet test tests/<project> -c Release`) → all PASS; note the counts in RESUME.
- [ ] **Step 5: Commit**
  ```bash
  git add docs/wiki docs/superpowers/RESUME.md
  git commit -m "docs: filter defaults, always-ask inputs, quoted choices, validator fixes"
  ```
