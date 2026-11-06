# Piece 2 — Ledgers, Tables, Formulas & Rules Workbench Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The player can write their own tables and formulas in a rules workbench beside the rulebook PDF. They can attach supply ledgers to counters and to holders such as depots or side-wide pools. They run any formula on a counter through the layout-C dialog, which fills the boxes it can, computes live, and writes the result to the ledger with full history.

**Architecture:**
- A new **`Quartermaster.Rules`** project holds the rules-pack data model, the safe expression language, tables and validation. It has no game state and no UI.
- **Core** gains ledger state and commands, plus a `FormulaRunner` that binds formula variables to game data.
- **Render**'s `ModuleSession` owns the loaded rules pack.
- **App** adds the formula dialog, the ledger window and the workbench.
- All ledger changes are commands in the existing command log.

**Tech Stack:** .NET 10, C#, WinForms, SkiaSharp (existing), WebView2 (existing), System.Text.Json, System.IO.Compression, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-06-quartermaster-engine-design.md`. This plan covers §7 and §8.1–8.2 (Tables / Formulas / Holders & Properties tabs only). Out of scope and deferred to piece 3: the sequence of play, unit filters for steps, alerts, roles, hidden-info settings, the coverage view, the number-tooltip breakdown on the map, and OCR (§8.3). Also read §2 (content boundary), §5 (command log), §14 and §14a.4 (stable IDs, saves keep working after pack edits). Piece-1 decisions are in `docs/superpowers/piece1-followups.md`.

## Global Constraints

- **Content boundary (spec §2):**
  - No CNA content, and nothing derived from it: no rules text, tables, formulas, sequence of play, map, counters or chart data.
  - This applies to code, tests, samples, docs and commit messages.
  - Test tables and formulas are invented (e.g. a made-up `Rates` table with numbers like 1, 2, 3).
  - Never fill a rules pack from the module's PDFs or chart images.
- **Rules packs are pure data. No executable code.** The expression language is interpreted, never compiled or `eval`ed.
- **Identity:**
  - Every pack entity (table, formula, property, holder, rule ref) has a stable `Id` string, a GUID in `"N"` format, created once.
  - Display `Name`s can be renamed freely.
  - Formulas refer to tables, properties and holders by **name** in their text, and the pack resolves names to ids. Commands and history store **ids**.
- **Pack identity:** pack SHA-256 = lowercase hex of the canonical `pack.json` + `tables/*.json` + `formulas/*.json` bytes, concatenated in ordinal path order. A save stores the hash of the pack it last used (spec §14a.4).
- **Formats:** `.qrules` = zip with `pack.json`, `tables/<id>.json`, `formulas/<id>.json`. A folder with the same layout also loads. JSON is camelCase.
- **Numbers:** `double`, invariant culture. Display format is `0.##`. `round` uses `MidpointRounding.AwayFromZero`.
- **Targets:** `Quartermaster.Rules` is `net10.0` with no project references. Core references Rules. The test project is `tests/Quartermaster.Rules.Tests`.
- **Behaviour carried over from piece 1:**
  - All state changes go through `GameSession.Execute`.
  - Unknown or malformed input never crashes: show an error or a diagnostic.
  - UI changes stay on the UI thread.
  - Classic gray WinForms look, Tahoma 8.25pt, system colours.
  - New windows open as MDI children with tabs (`MainForm.OpenChild`).
  - Smoke mode (`--smoke`, `--screenshot`) must keep exiting 0.
- **Dialog colours (spec §7.4):** auto-filled boxes `#FFFFE1` (the system Info colour), typed boxes `Window`. An overridden auto box switches to `Window` with an italic caption suffix `(override)`.

## Review Focus

1. **A formula refers to a table, property or holder that was renamed or deleted.** Validation must flag it by name, and running the formula shows a clear error in the dialog instead of crashing. Task 5 test `Validate_UnknownTableReference_Reported`; Task 7 test `Run_DeletedTable_ReturnsErrorTraceNotThrow`.
2. **Bad typed input in a white box** (`abc`, empty, out of the declared range). The dialog shows "Not a number" or "Must be 1–6", and Apply stays disabled. A negative ledger result is allowed but flagged. Task 7 tests `Run_TypedInputNotNumber_IsMissingWithReason` and `Run_InputOutOfRange_IsMissingWithReason`; Task 6 test `LedgerDelta_NegativeAfter_Allowed_FlaggedInHistory`.
3. **A rules pack for a different module, a corrupt zip or JSON, or a newer pack format.** The app warns about the hash mismatch and still loads. A corrupt pack gives an `InvalidDataException` message, and the app keeps running. Task 1 tests `Load_CorruptJson_ThrowsInvalidData` and `Load_FutureFormat_ThrowsInvalidData`; Task 8 test `LoadRules_ModuleHashMismatch_LoadsWithWarning`.
4. **Table keys pasted from PDFs:** en dash versus hyphen in ranges ("3–5" / "3-5"), stray spaces, overlapping or missing ranges, and a value matching no row. Parsing handles both dash types. Validation warns about overlaps. A failed lookup shows "No row for 7 in Rates". Task 4 tests `Key_EnDashRange`, `Lookup_NoMatch_ErrorMessage`; Task 5 test `Validate_OverlappingRanges_Warns`.
5. **A save made with an older version of the pack** (a formula or property deleted since). The save still loads, values are kept, and history shows `(deleted formula)`. Task 6 test `NativeSave_LedgerAndRulesHash_RoundTrip`; Task 10 test (Core-level) `LedgerHistory_DeletedFormula_DescribedAsDeleted`.

---

## File Structure

```
src/Quartermaster.Rules/                (new, net10.0, no references)
  Model/RulesPack.cs        – RulesPack, PropertyDef, HolderDef, RuleRef, PropertyScope
  Model/TableDef.cs         – TableDef, TableKind, TableKey, TableKeyKind
  Model/FormulaDef.cs       – FormulaDef, VariableBinding, BindingKind, FormulaOutput, OutputOp, OutputTarget
  Io/RulesPackIo.cs         – load (zip/folder), save (zip), hash
  Expr/Lexer.cs, Expr/Parser.cs, Expr/Ast.cs, Expr/ParseError.cs
  Expr/Value.cs             – Value (Number | Text | Bool)
  Expr/Evaluator.cs         – IEvalContext, Evaluator, EvalError
  Expr/WorkingLine.cs       – substituted rendering of an AST
  Tables/TableLookup.cs     – key parsing/matching, lookup
  Tables/TablePaste.cs      – TSV/CSV/space-column clipboard parser
  Layout/FormulaLayout.cs   – AST → rows of text/box items (layout C model)
  Validation/RulesValidator.cs – RulesIssue, Validate(pack)
src/Quartermaster.Core/
  Commands.cs               – + LedgerDelta, SeedLedger, RollDice
  GameState.cs              – + Ledgers, LedgerHistory, DiceHistory
  Ledger.cs                 – LedgerKey helpers, LedgerHistory queries, LedgerSeeder
  FormulaRunner.cs          – bind variables to game data, trace, commands
  PieceFilter.cs            – evaluate PropertyDef/FormulaDef AppliesTo on a piece
  NativeSave.cs             – format 2 (+ rulesSha256), still reads format 1
src/Quartermaster.Render/
  ModuleSession.cs          – + Rules, RulesPath, NewRules/OpenRules/SaveRules, last-pack memory
src/Quartermaster.App/
  FormulaDialog.cs          – layout-C dialog (also embeddable preview)
  LedgerWindow.cs           – holders × properties grid
  Workbench/WorkbenchWindow.cs, Workbench/PdfViewerControl.cs,
  Workbench/TablesTab.cs, Workbench/FormulasTab.cs, Workbench/PropertiesTab.cs
  MainForm.cs, MapWindow.cs, PdfWindow.cs, PropertySheetDialog.cs (modified)
tests/Quartermaster.Rules.Tests/        (new)
```

---

### Task 1: Rules project, pack model, load/save/hash

**Files:**
- Create: `src/Quartermaster.Rules/Quartermaster.Rules.csproj`, `Model/RulesPack.cs`, `Model/TableDef.cs`, `Model/FormulaDef.cs`, `Io/RulesPackIo.cs`, `tests/Quartermaster.Rules.Tests/*`
- Modify: `Quartermaster.slnx`, `src/Quartermaster.Core/Quartermaster.Core.csproj` (reference Rules)

**Interfaces:**
- Produces (all `sealed record`s with `init` properties, camelCase JSON):
  - `RulesPack`, with:
    - `int Format = 1`
    - `string Id`
    - `string Name`
    - `string ModuleSha256`
    - `List<PropertyDef> Properties`
    - `List<HolderDef> Holders`
    - `List<TableDef> Tables`
    - `List<FormulaDef> Formulas`
    - `List<RuleRef> RuleRefs`

    Plus lookups `TableByName(string)`, `PropertyByName(string)`, `HolderByName(string)` (case-insensitive), and `ById<T>(string id)`.
  - `PropertyDef(string Id, string Name, PropertyScope Scope, double Default, string? SheetField, string? AppliesTo, string? Notes)`, where `enum PropertyScope { Pieces, Holders, Both }`.
    - `SheetField` names a VASSAL property-sheet row to seed from (spec §7.1).
    - `AppliesTo` is an optional boolean expression (Task 3 language) that limits which pieces get the property.
  - `HolderDef(string Id, string Name, string? Notes)`. The built-in holder `"global"` always exists, is never stored in the pack, and has the constant `HolderDef.GlobalId = "global"`.
  - `RuleRef(string Id, string Label, string DocumentEntry, int Page)`
  - `TableDef(string Id, string Name, TableKind Kind, List<string> RowKeys, List<string> ColumnKeys, List<List<string>> Cells, string? RuleRefId, string? Notes)`, where:
    - `enum TableKind { OneD, TwoD, Dice }`;
    - `Cells[r][c]` holds raw text; OneD and Dice tables have a single column, whose header is `ColumnKeys[0] = "Value"`.
  - `FormulaDef(string Id, string Name, string Text, List<VariableBinding> Bindings, List<FormulaOutput> Outputs, string? AppliesTo, string? RuleRefId, string? Notes)`
  - `VariableBinding(string Variable, BindingKind Kind, string? Property, string? HolderId, string? TableId, string? RowExpr, string? ColExpr, double? Min, double? Max, List<string>? Choices, int? DiceCount, int? DiceSides, string? Caption)`, where `enum BindingKind { Unit, Global, Holder, Table, Location, Input, Roll }`.
    - `Location` resolves to the piece's grid or zone location name, as a string. This is the piece-2 form of spec §7.3 "terrain"; use it with `lookup()`.
  - `FormulaOutput(OutputTarget Target, string? HolderId, string Property, OutputOp Op)`, where `enum OutputTarget { Unit, Global, Holder }` and `enum OutputOp { Subtract, Add, Set }`.
  - `static class RulesPackIo`, with:
    - `RulesPack Load(string path)`, which accepts a `.qrules` zip or a folder
    - `void Save(RulesPack pack, string zipPath)`, which writes atomically through `.tmp` then a move
    - `string ComputeSha256(RulesPack pack)`
    - `RulesPack CreateEmpty(string name, string moduleSha256)`
    - `const int CurrentFormat = 1`

  Save writes one JSON file per table and formula, sorted by `Id`, indented, with `\n` line endings. That keeps the hash stable across machines.

- [ ] **Step 1: Write failing tests** in `tests/Quartermaster.Rules.Tests/RulesPackIoTests.cs`:
  - `SaveLoad_RoundTrip_PreservesEverything`: one of each entity, then save, load, and compare with deep JSON equality.
  - `Load_Folder_SameAsZip`
  - `Hash_StableAcrossSaveLoad_AndChangesWhenTableEdited`
  - `Load_CorruptJson_ThrowsInvalidData`: the message contains the entry name.
  - `Load_FutureFormat_ThrowsInvalidData`: `format: 99` gives a message containing "newer".
  - `Lookups_AreCaseInsensitive`
- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Rules.Tests`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement. Create the project with `dotnet new classlib -f net10.0` and the tests with `dotnet new xunit`, add both to `Quartermaster.slnx`, and add a Core → Rules reference.
- [ ] **Step 4:** Run the tests. Expected: PASS. `dotnet build Quartermaster.slnx` is clean.
- [ ] **Step 5: Commit** `feat: rules pack model and .qrules load/save`

---

### Task 2: Expression lexer, parser, AST

**Files:**
- Create: `src/Quartermaster.Rules/Expr/Lexer.cs`, `Parser.cs`, `Ast.cs`, `ParseError.cs`
- Test: `tests/Quartermaster.Rules.Tests/ParserTests.cs`

**Interfaces:**
- Produces:
  - `abstract record Expr(int Start, int Length)`, a source span for highlighting. Subtypes:
    - `NumberLit(double)`
    - `TextLit(string)`
    - `BoolLit(bool)`
    - `VarRef(string Name)`
    - `Unary(string Op, Expr Operand)`, where Op is `-` or `not`
    - `Binary(string Op, Expr Left, Expr Right)`
    - `Call(string Function, IReadOnlyList<Expr> Args)`
  - `if` parses as `Call("if", [c, a, b])`.
  - `sealed class ParseError : Exception`, with `int Position` and `string Detail`.
  - `static Expr Parser.Parse(string text)`. It throws `ParseError`. An empty or whitespace-only text throws `ParseError(0, "Formula is empty")`.
  - `static IReadOnlyList<string> Parser.Variables(Expr e)`: distinct `VarRef` names in first-occurrence order, excluding the first argument of `lookup` (a table name).

**Grammar**, lowest precedence first:

```
expr    := or
or      := and ( "or" and )*
and     := not ( "and" not )*
not     := "not" not | cmp
cmp     := add ( ("=="|"!="|"<"|"<="|">"|">=") add )?
add     := mul ( ("+"|"-") mul )*
mul     := unary ( ("*"|"/") unary )*
unary   := "-" unary | primary
primary := number | string | "true" | "false" | ident ( "(" args? ")" )? | "(" expr ")"
```

- Keywords and function names are case-insensitive. Identifiers are `[A-Za-z_][A-Za-z0-9_]*`.
- Strings are double-quoted, with `\"` and `\\` escapes.
- Numbers are invariant decimals such as `1.5` or `.5`.
- Known functions: `if min max round floor ceil abs clamp lookup dice has name marker`.
  - An unknown function name gives `ParseError` "Unknown function 'foo'".
  - A wrong argument count gives `ParseError`. The counts are: `if` 3, `min`/`max` 2 or more, `round` 1 or 2, `floor`/`ceil`/`abs` 1, `clamp` 3, `lookup` 2 or 3, `dice` 2, `has` 1, `name` 0, `marker` 1.

- [ ] **Step 1: Write failing tests:**
  - `Precedence_MulBeforeAdd`: `1+2*3` gives `Binary("+",1,Binary("*",2,3))`.
  - `UnaryMinus_BindsTighterThanMul`
  - `Comparison_And_Or_Not`
  - `If_ParsesAsCall`
  - `Lookup_FirstArgIsVarRef_NotListedInVariables`: `Variables("lookup(Rates, Kind) * MP")` gives `["Kind","MP"]`.
  - `Variables_DistinctInOrder`
  - `UnknownFunction_ParseErrorWithPosition`
  - `WrongArgCount_ParseError`
  - `UnterminatedString_ParseError`
  - `Empty_ParseError`
  - `Spans_CoverSourceText`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement a recursive-descent parser.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: formula expression parser`

---

### Task 3: Values, evaluator, working line

**Files:**
- Create: `src/Quartermaster.Rules/Expr/Value.cs`, `Evaluator.cs`, `WorkingLine.cs`
- Test: `tests/Quartermaster.Rules.Tests/EvaluatorTests.cs`

**Interfaces:**
- Consumes: the Task 2 AST.
- Produces:
  - `readonly record struct Value`, with:
    - `ValueKind Kind`, where `enum ValueKind { Number, Text, Bool }`
    - `double Number`
    - `string Text`
    - `bool Bool`
    - `static Value Of(double)`, `Of(string)`, `Of(bool)`
    - `string Display()` (numbers formatted `0.##`, invariant)
    - `bool IsTruthy` (true for a true Bool, or a non-zero Number)
  - `sealed class EvalError : Exception`, with `Expr? At`.
  - `interface IEvalContext`, with:
    - `Value GetVariable(string name)`, which throws `EvalError` when the value is missing
    - `Value Lookup(string tableName, Value row, Value? col)`
    - `IReadOnlyList<int> RollDice(int n, int sides)`
    - `bool Has(string property)`
    - `Value PieceName()`
    - `Value Marker(string key)`
  - `static Value Evaluator.Evaluate(Expr e, IEvalContext ctx)`. Rules:
    - Arithmetic needs Numbers; otherwise `EvalError` "'+' needs numbers, got text".
    - `/ 0` gives `EvalError` "Division by zero".
    - `==` and `!=` work on any kind. A string comparison is ordinal and case-insensitive. Comparing a number to a string is false or true respectively, not an error.
    - `<` and the other ordering operators need numbers.
    - `and`/`or` short-circuit, and `if` evaluates only its chosen branch.
    - `dice(n, sides)` returns the sum of `RollDice(n, sides)`. `n` must be 1–100 and `sides` 2–1000, else `EvalError`.
    - `round(x[, digits])` uses AwayFromZero.
    - `clamp(x, lo, hi)`.
  - `static string WorkingLine.Render(Expr e, Func<string, Value?> values)` renders the source with each variable replaced by its value, or by `?` when missing. Operators are shown as `×`, `÷` and `−` (U+2212), and parentheses are kept where the source had them. Function calls render as `name(args)`. A `lookup` renders as `TableName[row(, col)]`.

- [ ] **Step 1: Write failing tests**, using a `FakeContext` dictionary in the test project:
  - `Arithmetic_AndPrecedence`: `MP * Rate * Mod / Div` with `12,3,1.5,2` gives 27.
  - `Round_AwayFromZero`: `round(2.5)` = 3 and `round(-2.5)` = -3.
  - `DivisionByZero_EvalError`
  - `TextInArithmetic_EvalError`
  - `If_OnlyEvaluatesChosenBranch`: the other branch references a missing variable and no error results.
  - `AndOr_ShortCircuit`
  - `Compare_TextCaseInsensitive`
  - `Dice_SumsRolls`: the fake roller returns `[3,4]` for `dice(2,6)`, so the result is 7.
  - `Dice_OutOfRange_EvalError`
  - `Has_And_Marker_And_Name_DelegateToContext`
  - `WorkingLine_SubstitutesAndUsesMathSymbols`: `MP * Rate * Mod / Div` gives `"12 × 3 × 1.5 ÷ 2"`.
  - `WorkingLine_MissingShowsQuestionMark`
  - `WorkingLine_LookupRendersBracketed`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: formula evaluator and working line`

---

### Task 4: Tables: keys, lookup, clipboard paste

**Files:**
- Create: `src/Quartermaster.Rules/Tables/TableLookup.cs`, `TablePaste.cs`
- Test: `tests/Quartermaster.Rules.Tests/TableTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct TableKey(TableKeyKind Kind, double Lo, double Hi, string Text)`, where `enum TableKeyKind { Number, Range, AtLeast, AtMost, Text }`.
  - `static TableKey TableLookup.ParseKey(string raw)`:
    - The input is trimmed and NBSP-normalised.
    - `"5"` is Number.
    - `"3-5"`, `"3–5"` (en dash) and `"3 — 5"` are Range, inclusive.
    - `"7+"`, `"≥7"` and `">=7"` are AtLeast.
    - `"≤2"`, `"<=2"` and `"2-"` are AtMost.
    - `"-2"` is the Number -2.
    - Anything else is Text.
  - `static bool TableLookup.Matches(TableKey k, Value v)`
  - `static Value TableLookup.Lookup(TableDef t, Value row, Value? col)`. It takes the first matching row and column in order.
    - OneD and Dice tables ignore `col`.
    - A cell parses as Number when it can, otherwise as Text.
    - No match gives `EvalError` `"No row for {row} in {t.Name}"`, or `"No column for {col} in {t.Name}"`.
    - TwoD with `col == null` gives `EvalError` "Table X needs a column".
  - `sealed record PastedGrid(List<string> ColumnKeys, List<string> RowKeys, List<List<string>> Cells)`
  - `static PastedGrid TablePaste.Parse(string clipboard, bool firstRowIsHeader, bool firstColumnIsKey)`:
    - The splitter is tab when any line has a tab, else comma (RFC-4180 quotes), else runs of 2 or more spaces.
    - Blank lines are skipped and cells are trimmed.
    - Ragged rows are padded with `""`.

- [ ] **Step 1: Write failing tests:**
  - `Key_Number`, `Key_Range`, `Key_EnDashRange`, `Key_AtLeast_Variants`, `Key_AtMost_Variants`, `Key_NegativeNumber`, `Key_Text`
  - `Lookup_OneD_Range`: rows `["1-2","3-5","6+"]` with values `["1","2","3"]`, looked up with 4, give 2.
  - `Lookup_TwoD_TextColumn`: column match is case-insensitive.
  - `Lookup_NoMatch_ErrorMessage`: the message equals `"No row for 7 in Rates"`.
  - `Lookup_TwoD_MissingColumn_Error`
  - `Lookup_CellText_ReturnsText`
  - `Paste_Tsv_WithHeaderAndKeys`
  - `Paste_Csv_Quoted`
  - `Paste_SpaceColumns`: a two-space separation splits columns, and a single space does not.
  - `Paste_RaggedRowsPadded`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: table keys, lookup and clipboard paste`

---

### Task 5: Formula layout model and pack validation

**Files:**
- Create: `src/Quartermaster.Rules/Layout/FormulaLayout.cs`, `Validation/RulesValidator.cs`
- Test: `tests/Quartermaster.Rules.Tests/LayoutTests.cs`, `ValidatorTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces:
  - `abstract record LayoutItem`. Subtypes:
    - `TextItem(string Text)`
    - `BoxItem(string Variable, string Caption, bool Auto)`
    - `MirrorItem(string Variable)`, used for later uses of a variable that already has a box
    - `SubRow(string Label, IReadOnlyList<LayoutItem> Items)`
  - `sealed record FormulaLayoutModel(IReadOnlyList<LayoutItem> Main, IReadOnlyList<SubRow> SubRows)`
  - `static FormulaLayoutModel FormulaLayout.Build(FormulaDef f, Expr ast, RulesPack pack)`:
    - The main row follows the source with operators shown as `× ÷ −`.
    - Each variable's first occurrence becomes a `BoxItem`. `Auto` is false for `Input` and true for every other kind, `Roll` included. The dialog switches Roll boxes to typed when "physical dice" is on (Task 9).
    - Each **Table-bound variable** (binding kind Table, whose `RowExpr`/`ColExpr` mention other variables) also produces a `SubRow` labelled `"{Variable} = {Table.Name}[…]"`, containing boxes for those variables.
    - Inline `lookup(...)` and `if(...)` calls in the formula text become a `TextItem` of their working form on the main row, plus a `SubRow` holding their argument boxes.
    - Caption defaults are by binding kind:
      - Unit: `"{Property} (unit)"`
      - Global: `"{Property} (global)"`
      - Holder: `"{Property} ({Holder.Name})"`
      - Table: `"{Table.Name}"`
      - Location: `"location"`
      - Input: `"{Variable}"`, plus `" 1–6"` when it has a range
      - Roll: `"{n}d{sides}"`
    - An explicit `Caption` wins.
  - `sealed record RulesIssue(RulesIssueSeverity Severity, string EntityId, string EntityName, string Message)`, where `enum RulesIssueSeverity { Error, Warning }`.
  - `static IReadOnlyList<RulesIssue> RulesValidator.Validate(RulesPack pack)`. Errors:
    - a parse error, with its position in the message;
    - a variable with no binding;
    - a binding for a variable the text doesn't use (Warning);
    - an unknown table, property or holder, whether by id in a binding or by name in a `lookup`;
    - a Table binding without `RowExpr`, or a TwoD table without `ColExpr`;
    - an Input with Min > Max;
    - a Roll with a bad count or sides;
    - an output to a property whose scope doesn't allow the target;
    - duplicate names (case-insensitive) within a kind;
    - `AppliesTo` failing to parse;
    - a table whose key ranges overlap (Warning) or whose key is empty (Error);
    - a ruleRef pointing at a missing RuleRef id.

- [ ] **Step 1: Write failing tests:**
  - `Layout_BoxesInSourceOrder_WithOperatorsAsText`: `MP * Rate` gives `[Box MP, Text " × ", Box Rate]`.
  - `Layout_RepeatedVariable_MirrorAfterFirst`
  - `Layout_InputIsTyped_OthersAuto`
  - `Layout_TableBindingWithVariableRow_HasSubRow`
  - `Layout_InlineLookup_SubRow`
  - `Layout_CaptionDefaults_And_Override`
  - `Validate_CleanPack_NoIssues`
  - `Validate_UnboundVariable_Error`
  - `Validate_UnknownTableReference_Reported`: both the binding form and the inline `lookup(Name, …)` form.
  - `Validate_ParseError_HasPosition`
  - `Validate_OverlappingRanges_Warns`
  - `Validate_DuplicateNames_Error`
  - `Validate_OutputScopeMismatch_Error`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: layout-C model and rules validation`

---

### Task 6: Ledger state, commands, history, save format 2

**Files:**
- Create: `src/Quartermaster.Core/Ledger.cs`
- Modify: `src/Quartermaster.Core/Commands.cs`, `GameState.cs`, `NativeSave.cs`, `GameSession.cs`
- Test: `tests/Quartermaster.Core.Tests/LedgerTests.cs`, `NativeSaveTests.cs`

**Interfaces:**
- Produces:
  - `static class LedgerKey`, with:
    - `string Piece(string pieceId)`, which gives `"p:" + id`
    - `string Holder(string holderId)`, which gives `"h:" + id`
    - `bool TryParse(string key, out bool isPiece, out string id)`
  - New fields in `GameState`, all serialized:
    - `OrderedDictionary<string, OrderedDictionary<string, double>> Ledgers`, keyed by ledger key, then **property id**
    - `List<LedgerEntry> LedgerHistory`
    - `List<DiceEntry> DiceHistory`
  - `sealed record LedgerEntry(long Seq, string Key, string PropertyId, double Before, double After, string Source, string? FormulaId, string? RuleRefId, string? Note, string? Working)`. `Source` is `"formula"`, `"manual"` or `"seed"`.
  - `sealed record DiceEntry(long Seq, string Purpose, int Count, int Sides, IReadOnlyList<int> Results)`
  - New commands, registered with `[JsonDerivedType]` and these discriminators:
    - `LedgerDelta(string Key, string PropertyId, double Before, double After, string Source, string? FormulaId, string? RuleRefId, string? Note, string? Working)`, discriminator `ledgerDelta`
    - `SeedLedger(IReadOnlyList<LedgerSeed> Seeds)`, discriminator `seedLedger`, where `sealed record LedgerSeed(string Key, string PropertyId, double Value)`
    - `RollDice(string Purpose, int Count, int Sides, IReadOnlyList<int> Results)`, discriminator `rollDice`
  - Reducer rules:
    - `LedgerDelta` sets the value to `After` (not Before + delta), so replay is deterministic, and appends a `LedgerEntry` with `Seq` = history count + 1.
    - `SeedLedger` sets only values that are missing and writes `Source = "seed"` entries.
    - `RollDice` appends a `DiceEntry`.
    - A ledger key for a piece id that no longer exists is applied anyway; it's harmless.
    - `RemovePiece` also removes the piece's ledger. History is kept.
  - `static class Ledger`, with:
    - `double? Get(GameState s, string key, string propertyId)`
    - `double GetOrDefault(GameState s, string key, PropertyDef p)`
    - `IReadOnlyList<LedgerEntry> History(GameState s, string key, string propertyId)`
    - `string Describe(LedgerEntry e, RulesPack? pack)`, which gives, for example, `"−27 Fuel Use (formula)"`, or `"(deleted formula)"` when `FormulaId` doesn't resolve
  - `static class LedgerSeeder`, with `SeedLedger? BuildSeeds(GameState s, RulesPack pack, Func<PieceInstance, bool> appliesTo, string? sheetTraitLookup = null)`:
    - For each piece where `appliesTo` is true, and each property with Scope Pieces/Both and a non-null `SheetField`, it reads the piece's property-sheet value (via `PieceActions.ReadSheet` on the first `propertysheet` trait that has that row name).
    - When that value parses as a number and the ledger has no value yet, it adds a seed.
    - It returns null when there's nothing to seed.
  - `GameSession` gains `string? RulesSha256 { get; set; }`.
  - `NativeSave` writes `format: 2` with `"rulesSha256"`, and reads formats 1 and 2. Format 1 has no ledgers, so it gets empty collections.

- [ ] **Step 1: Write failing tests:**
  - `LedgerDelta_SetsAfter_AndAppendsHistory`
  - `LedgerDelta_NegativeAfter_Allowed_FlaggedInHistory`: the entry is kept, and `Ledger.History(...)[0].After < 0`. The "flag" is computed by the UI from `After < 0`, so assert the value only.
  - `SeedLedger_OnlyFillsMissing`
  - `RollDice_AppendsDiceHistory`
  - `RemovePiece_RemovesLedgerKeepsHistory`
  - `Seeder_ReadsBoundSheetField_IgnoresNonNumeric`: use the fixture piece type with a `propertysheet` trait, built with SequenceEncoder as in piece-1 tests.
  - `NativeSave_LedgerAndRulesHash_RoundTrip`
  - `NativeSave_ReadsFormat1_EmptyLedgers`
  - `Describe_DeletedFormula`: the pack lacks the id, and the result contains "(deleted formula)".
  - `Commands_JsonDiscriminators`
- [ ] **Step 2:** Run `dotnet test tests/Quartermaster.Core.Tests`. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run all tests. Expected: PASS, with no piece-1 regressions.
- [ ] **Step 5: Commit** `feat: ledger state, commands and history`

---

### Task 7: FormulaRunner and piece filters

**Files:**
- Create: `src/Quartermaster.Core/FormulaRunner.cs`, `PieceFilter.cs`
- Test: `tests/Quartermaster.Core.Tests/FormulaRunnerTests.cs`, `PieceFilterTests.cs`

**Interfaces:**
- Consumes: Rules (all), `Ledger`, `GameState`, `ModuleModel` (for the location name via the board grid: `IMapGrid.LocationName` of the piece's position minus the board offset; the board is the one containing `Pos`), and the `PieceTypeParser`/`MarkerDef` state for `marker()`. Marker values are read the same way as the piece-1 fix in `MapInteraction.PieceDetails`.
- Produces:
  - `sealed record VariableState(string Variable, Value? Value, bool Auto, bool Overridden, string? MissingReason)`
  - `sealed record FormulaTrace(FormulaDef Formula, IReadOnlyList<VariableState> Variables, Value? Result, string Working, IReadOnlyList<OutputPreview> Outputs, string? Error, IReadOnlyList<DiceEntry> Rolls)`
  - `sealed record OutputPreview(string Key, string PropertyId, string PropertyName, double Before, double After)`
  - `sealed record RunRequest(string FormulaId, string? PieceId, IReadOnlyDictionary<string, string> Typed, IReadOnlyDictionary<string, string> Overrides, IReadOnlyDictionary<string, IReadOnlyList<int>> FixedRolls)`
  - `sealed class FormulaRunner(RulesPack pack, ModuleModel model, Func<int, int, IReadOnlyList<int>> roller)`, with:
    - **`FormulaTrace Run(GameState s, RunRequest r)`.** It never throws. Parse and eval errors go to `Error`, and missing inputs go to `MissingReason`. Binding resolution:
      - Unit: the piece's ledger value, or the property `Default` when the piece has no value.
      - Global: the ledger under `LedgerKey.Holder("global")`.
      - Holder: the given holder's ledger.
      - Table: `RowExpr`/`ColExpr` are evaluated in the same context, then `TableLookup.Lookup` runs.
      - Location: a Text value.
      - Input: parsed from `Typed`. A missing value gives "Waiting for input"; a non-number "Not a number"; a value out of range `"Must be {Min}–{Max}"`; a value not among `Choices` "Choose one of …".
      - Roll: `FixedRolls[var]` if present, else `roller(n, sides)`. The rolls are recorded in `Rolls`, so the dialog shows the same dice the Apply will log; the caller keeps them in `FixedRolls` between re-runs.
      - `Overrides[var]` replaces any auto value and sets `Overridden = true`. A non-numeric override for a number-typed use gets the MissingReason "Not a number".
    - Result: computed only when nothing is missing. Then `Working = WorkingLine.Render(...) + " = " + result.Display()`, plus `" → {Prop} {before} − {x} = {after}"` per output, joined with `"; "`. The operator glyph follows the op: `−`, `+`, or `=` for Set.
    - Outputs: Subtract gives before − result, Add gives before + result, Set gives result. Unit targets need a PieceId, else Error "This formula needs a unit".
    - **`IReadOnlyList<GameCommand> Commands(FormulaTrace t, string? note)`.** It returns `RollDice` commands for every roll, then one `LedgerDelta` per output, with `Source = "formula"`, `FormulaId`, `RuleRefId`, `Working` and `note` filled in. It returns an empty list if `Result` is null or `Error` is set.
  - `static class PieceFilter`, with:
    - `bool Matches(string? appliesTo, PieceInstance p, GameState s, ModuleModel model, RulesPack pack)`. Null or empty matches every piece. The expression is evaluated with `name()`, `marker(k)` and `has(prop)`; Unit variables resolve by property name to ledger values. Any parse or eval error gives false.
    - `IReadOnlyList<FormulaDef> FormulasFor(PieceInstance p, …)`, the formulas whose `AppliesTo` matches and that need a unit (any Unit binding or Unit output), ordered by Name.

- [ ] **Step 1: Write failing tests**, with a fixture pack holding table `Rates` (OneD, keys `1-2,3-5,6+`, values `1,2,3`), properties `Fuel` (Pieces, Default 0) and `Supply` (Holders), holder `Depot A`, and formula `Fuel Use` with text `MP * lookup(Rates, Kind) / Div`. Its bindings: `MP` Input 0–20, `Kind` Input, `Div` Global property `Divisor`. Its output: Unit Fuel Subtract.
  - `Run_AllFilled_ComputesResultWorkingAndOutputs`: Fuel 140, MP 12, Kind 4, Divisor 2 give result 12. The working line is `"12 × Rates[4] ÷ 2 = 12 → Fuel 140 − 12 = 128"`. The exact string is asserted.
  - `Run_MissingInput_NoResult_ListsWaiting`
  - `Run_TypedInputNotNumber_IsMissingWithReason`
  - `Run_InputOutOfRange_IsMissingWithReason`
  - `Run_Override_UsedAndFlagged`
  - `Run_Roll_UsesFixedRollsWhenGiven`
  - `Run_DeletedTable_ReturnsErrorTraceNotThrow`
  - `Run_UnitFormulaWithoutPiece_Error`
  - `Commands_RollsThenDeltas_WithProvenance`
  - `Commands_EmptyWhenIncomplete`
  - `Location_BindsGridLocationName`, using a fixture square grid from piece-1 tests
  - `PieceFilter_MarkerAndName`
  - `PieceFilter_BadExpression_False`
  - `FormulasFor_OrderedAndFiltered`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement. `IEvalContext` is implemented privately inside FormulaRunner.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: formula runner with bindings, overrides and provenance`

---

### Task 8: ModuleSession rules integration

**Files:**
- Modify: `src/Quartermaster.Render/ModuleSession.cs`
- Create: `src/Quartermaster.Render/RulesMemory.cs`
- Test: `tests/Quartermaster.Render.Tests/ModuleSessionRulesTests.cs`

**Interfaces:**
- Produces, on `ModuleSession`:
  - `RulesPack? Rules`
  - `string? RulesPath`
  - `bool RulesDirty`
  - `event Action? RulesChanged`
  - `void NewRules(string name)`, which creates an empty pack bound to `Archive.Sha256`
  - `void OpenRules(string path)`. On a hash mismatch it logs a `Warning`/`rules` diagnostic `"Rules pack was made for a different module"` and still loads. It then runs `LedgerSeeder` and executes the seed command, if any.
  - `void SaveRules(string? path = null)`, which recomputes the hash and sets `Game.RulesSha256`
  - `void UpdateRules(Func<RulesPack, RulesPack> edit)`, which marks the pack dirty and raises `RulesChanged`
  - `FormulaRunner? Runner`, rebuilt on change, with the roller `Random.Shared`-based

  `LoadNative` warns when the save's `rulesSha256` differs from the loaded pack's hash: diagnostic `"Save was made with a different version of the rules pack"`.
- `static class RulesMemory` remembers the last pack path per module hash in `%LOCALAPPDATA%\Quartermaster\rules-index.json`. It has `string? LastFor(string moduleSha)` and `void Remember(string moduleSha, string path)`. The file is never trusted blindly: a missing file means no entry. `ModuleSession.Open` auto-opens the remembered pack if the file exists, and on failure logs and continues.

- [ ] **Step 1: Write failing tests:**
  - `NewRules_BoundToModuleHash`
  - `SaveOpen_RoundTrip_SetsGameRulesSha`
  - `LoadRules_ModuleHashMismatch_LoadsWithWarning`
  - `OpenRules_SeedsLedgerFromSheetFields`: use a fixture module with a piece that has a `propertysheet` field `Fuel=40`, and a pack property with `SheetField "Fuel"`.
  - `RulesMemory_RemembersAndAutoOpens`: the tests use a temp `LOCALAPPDATA`-equivalent root passed in, through a constructor overload.
  - `OpenRules_Corrupt_ThrowsInvalidData_SessionUnchanged`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run. Expected: PASS.
- [ ] **Step 5: Commit** `feat: rules pack in module session`

---

### Task 9: Layout-C formula dialog and the Calculate menu

**Files:**
- Create: `src/Quartermaster.App/FormulaDialog.cs`
- Modify: `src/Quartermaster.App/MapWindow.cs` (right-click menu), `MainForm.cs` (File menu: New/Open/Save/Save As Rules Pack; title shows the pack name)
- Test: logic is covered by Task 7. This task adds smoke-mode coverage.

**Interfaces:**
- Consumes: `ModuleSession.Runner`/`Rules`, `FormulaLayout.Build`, `FormulaRunner.Run/Commands`, `MapWindow.Run`.
- Produces: `sealed class FormulaDialog : Form`, with `FormulaDialog(ModuleSession s, string formulaId, string? pieceId, bool previewOnly = false)`.
  - **Layout**, as in spec §7.4 and the user's chosen mockup C:
    - Title `"{Formula.Name}: {piece name}"`.
    - A "Formula" GroupBox with a FlowLayoutPanel of TextItem labels and BoxItem TextBoxes, each box with a caption Label beneath (a small Tahoma 7pt).
    - SubRows go on their own indented lines inside the group.
    - The result box is right-aligned and bold.
    - A "Working" GroupBox holds a Consolas label showing `Trace.Working`. A negative `After` is shown in red with a "(negative)" suffix.
    - Buttons: Apply to ledger, Cancel, Rule ref…
    - A StatusStrip shows "All inputs filled", `"Waiting for: MP, Kind"`, or the error text.
  - **Behaviour:**
    - Auto boxes start filled with a `#FFFFE1` background.
    - Typing in an auto box adds an override, switches it to the Window background and appends "(override)" to its caption.
    - Clearing an overridden box restores the auto value.
    - Every TextChanged re-runs `Runner.Run`, debounced 150 ms, with the current `Typed`/`Overrides` and the retained `FixedRolls`.
    - Roll boxes show the dice, e.g. "4 (1+3)". A small "↻" button re-rolls and clears that variable's `FixedRolls`. When "physical dice" is on (a checkbox in the dialog, default off), Roll boxes become typed boxes instead.
    - Apply is enabled only when `Result != null && Error == null`. Apply executes `Commands(trace, note: null)` through `MapWindow.Run`-style guarding, in order, then closes.
    - Rule ref… opens the PDF via `MainForm.OpenDocumentAtPage(entry, page)` (add it; it reuses `OpenChild` + `PdfWindow.GoToPage`). It's disabled when there's no `RuleRefId`.
    - `previewOnly` disables Apply, and the dialog can be hosted as a child control. Expose `Control CreatePreviewPanel()`, or make the dialog `TopLevel = false` friendly, for the Task 12 embed.
  - **Calculate menu:** when the piece's formulas list (`PieceFilter.FormulasFor`) is non-empty, the right-click menu gets a "Calculate ▸" submenu, one entry per formula, which opens a FormulaDialog for that piece. The submenu is hidden when no rules pack is loaded.
  - **Smoke:** when a `--rules <path>` CLI argument is given, it's loaded. With `--smoke`, it opens the first formula's dialog for the first piece it applies to. In preview mode, `--screenshot-formula <png>` saves `DrawToBitmap` of the dialog, which then closes.

- [ ] **Step 1:** Implement the dialog and wiring. Keep each file under about 400 lines; put the trace-to-controls binding in a helper if needed.
- [ ] **Step 2:** Build. `dotnet test Quartermaster.slnx` is green.
- [ ] **Step 3:** Smoke with a synthetic fixture module plus a synthetic pack: `--new --rules <pack> --smoke 3 --screenshot-formula <scratch png>`. Expected: exit 0, and the PNG shows the boxes, captions and working line.
- [ ] **Step 4: Commit** `feat: layout-C formula dialog and Calculate menu`

---

### Task 10: Ledger window

**Files:**
- Create: `src/Quartermaster.App/LedgerWindow.cs`, `src/Quartermaster.Core/LedgerTable.cs`
- Modify: `MainForm.cs` (View → Ledger), `PropertySheetDialog.cs`
- Test: `tests/Quartermaster.Core.Tests/LedgerTableTests.cs`

**Interfaces:**
- Produces:
  - `sealed record LedgerRow(string Key, string Name, string Kind, IReadOnlyList<double?> Values)`
  - `static class LedgerTable`, with:
    - `IReadOnlyList<PropertyDef> Columns(RulesPack p)`, ordered by Name
    - `IReadOnlyList<LedgerRow> Rows(GameState s, RulesPack p, ModuleModel m, string filter)`
      - Rows are the holders (including `global`), then the pieces that have at least one ledger value.
      - The piece name comes from BasicPiece. `Kind` is "Holder" or "Unit", followed by the map name.
      - The filter is a case-insensitive substring match on Name or Kind.
    - `string ToCsv(IReadOnlyList<PropertyDef> cols, IReadOnlyList<LedgerRow> rows)`, RFC-4180, invariant culture
    - `LedgerHistory_*` helpers are reused from `Ledger.History`/`Describe`.
  - `LedgerWindow`, an MDI child titled "Ledger":
    - The ToolStrip has a filter TextBox, Export CSV…, Show units without values (toggle), and Refresh.
    - A `DataGridView` in VirtualMode with sortable columns (sorted in memory).
    - Cell tooltip: the last 10 history entries via `Ledger.Describe`, newest first.
    - Editing a cell prompts for a required note (the InputDialog from piece 1). Then it executes `LedgerDelta(Source "manual", Note)`. Non-numeric input is rejected with a message.
    - Refreshes on `GameSession.Changed` and `RulesChanged`, marshalled to the UI thread and debounced.
  - `PropertySheetDialog`: rows whose name equals some `PropertyDef.SheetField` show read-only, with the hint "(managed by ledger: {value})".

- [ ] **Step 1: Write failing tests:**
  - `Rows_HoldersFirst_ThenUnitsWithValues`
  - `Rows_FilterMatchesNameOrKind`
  - `Csv_QuotesAndInvariantNumbers`
  - `LedgerHistory_DeletedFormula_DescribedAsDeleted`
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement the logic, then the window.
- [ ] **Step 4:** Run the tests. Expected: PASS. Smoke with `--rules … --smoke 3` and View→Ledger opened during smoke (extend `FinishSmoke` to open it when rules are loaded). Expected: exit 0.
- [ ] **Step 5: Commit** `feat: ledger window`

---

### Task 11: Workbench shell, PDF pane, Tables tab

**Files:**
- Create: `src/Quartermaster.App/Workbench/WorkbenchWindow.cs`, `PdfViewerControl.cs`, `TablesTab.cs`
- Modify: `PdfWindow.cs` (host `PdfViewerControl`, same behaviour), `MainForm.cs` (Tools → Rules Workbench, Ctrl+Shift+W)

**Interfaces:**
- Produces:
  - `PdfViewerControl : UserControl`, extracted from PdfWindow. It has:
    - `void Open(DocumentDef d, int page = 1)`
    - `int Page { get; }`
    - `event Action<int>? PageChanged`

    The ToolStrip has a document combo (module PDFs), a page `NumericUpDown`, and Prev/Next. Changing the page re-navigates `#page=N`. The page field is the source of truth for "current page"; WebView2 doesn't expose a reliable current page. All piece-1 WebView2 error handling moves with it.
  - `WorkbenchWindow`, an MDI child titled "Rules Workbench — {pack name}":
    - A SplitContainer: `PdfViewerControl` on the left, a TabControl on the right with Tables / Formulas / Properties & Holders.
    - If no pack is loaded, it shows a "New rules pack…" / "Open…" prompt panel.
    - Helper `RuleRef LinkCurrentPage(string label)`. It creates or reuses a `RuleRef` for (document, page) and returns it.
  - `TablesTab : UserControl`:
    - A left ListBox of tables, with New, Delete (confirm) and Rename.
    - On the right: a Kind combo, a Notes textbox, a "Rule ref" label with a "Link to current page" button, and an editable DataGridView.
      - The first column holds row keys. The header row holds column keys, editable through a "Columns…" dialog or the first grid row; pick one and document it.
      - TwoD tables can add and remove rows and columns. OneD and Dice tables have a single value column.
    - **"Paste from clipboard"** runs `TablePaste.Parse` with checkboxes for header and key column, shows a preview grid, and replaces the table on OK.
    - Invalid keys (empty) are highlighted in pink. Key cells show their parsed meaning in a tooltip, e.g. "3 to 5" or "at least 7".
    - Every edit goes through `ModuleSession.UpdateRules`.
  - An issues list at the bottom of the window is a ListView of `RulesValidator.Validate` results. It is re-validated (debounced 300 ms) on `RulesChanged`. Double-clicking an issue selects the entity's tab and item.

- [ ] **Step 1:** Implement. If `PdfWindow` behaviour changes, keep the piece-1 tests and smoke green.
- [ ] **Step 2:** Build, run `dotnet test`, and smoke: with `--rules`, open the Workbench during smoke (`--screenshot-workbench <png>`). Expected: exit 0, with the screenshot showing the PDF pane and the Tables tab.
- [ ] **Step 3: Commit** `feat: rules workbench with PDF pane and tables editor`

---

### Task 12: Workbench Formulas and Properties & Holders tabs

**Files:**
- Create: `src/Quartermaster.App/Workbench/FormulasTab.cs`, `PropertiesTab.cs`

**Interfaces:**
- Consumes: Tasks 5, 7, 9 and 11.
- Produces:
  - `FormulasTab : UserControl`:
    - A list of formulas (New, Delete, Rename).
    - A formula text box (Consolas). Parse errors appear as a red message plus the caret moved to `ParseError.Position` on demand.
    - A **Bindings grid**, auto-populated from `Parser.Variables`. New variables are added as Input; removed ones are dropped, after confirming if they were configured. Columns: Variable, Kind (combo), Target, Details (range/choices/dice/row & column expressions), Caption.
      - Target is a property, holder or table, chosen from a combo of names.
    - An **Outputs grid** with columns Target (Unit/Global/Holder), Holder, Property and Op.
    - **AppliesTo**, with a syntax check.
    - A Rule ref link button.
    - The **live preview** is an embedded FormulaDialog in `previewOnly` mode.
    - The **test panel** has a piece picker (a combo of placed pieces with type-to-filter, plus "(no unit)"), and the preview runs against it. Typed and override values in the preview act as sample values.
  - `PropertiesTab : UserControl`:
    - The **Properties grid** has columns Name, Scope, Default, SheetField and AppliesTo. AppliesTo has a syntax check, and SheetField has a dropdown of row names found in the module's propertysheet traits.
    - The **Holders grid** has columns Name and Notes. `global` is shown read-only.
    - Deleting a property that's used by formulas asks for confirmation and lists the formulas; validation will then flag them.
  - Every edit goes through `UpdateRules`, so the validation list and the preview refresh.

- [ ] **Step 1:** Implement.
- [ ] **Step 2:** Build, run `dotnet test`, and smoke with `--screenshot-workbench` after selecting the Formulas tab (add `--workbench-tab formulas`). Expected: exit 0, with the preview visible in the screenshot.
- [ ] **Step 3: Commit** `feat: workbench formula and property editors with live preview`

---

### Task 13: End-to-end check and user checklist

**Files:**
- Create: `tests/Quartermaster.Render.Tests/RulesEndToEndTests.cs`
- Modify: `docs/superpowers/piece1-followups.md` → append a "Piece 2" section of deferred items found during this piece.

- [ ] **Step 1: Write the end-to-end test** `EndToEnd_PackFormulaLedgerSaveLoad`:
  1. Fixture module plus `ModuleSession.Open`.
  2. `NewRules`.
  3. `UpdateRules`: add property `Fuel` (with `SheetField "Fuel"`), table `Rates`, and formula `Fuel Use`, all invented.
  4. Save the pack, reopen the session, open the pack. The seeded Fuel is 40.
  5. `Runner.Run` with typed inputs, then `Commands` executed.
  6. Assert Fuel is 28 and the history has 2 entries (seed and formula).
  7. `SaveNative`, then `LoadNative`. The ledger and history survive, and `RulesSha256` matches.
  8. Edit the pack (delete the formula), then `LoadNative` again. It loads with the rules-version warning, and `Describe` shows "(deleted formula)".
- [ ] **Step 2:** Run. Expected: PASS. If it fails, fix the owning task's code with a regression test.
- [ ] **Step 3:** Run the full `dotnet test Quartermaster.slnx`. Expected: PASS. Then smoke on the user's real module with an **empty** new pack created during smoke (`--new-rules SmokePack`): open the Workbench and the Ledger, with exit 0 and the screenshot kept in the scratchpad only. **No rules are written from module content.**
- [ ] **Step 4:** Record in the report the checklist for the user's hands-on test:
  - create a table by pasting from a PDF;
  - write a formula with a lookup and an input;
  - Link to current page;
  - run Calculate on a counter;
  - check Apply updates the Ledger window with the history tooltip;
  - check a manual edit requires a note;
  - save and reload the game and the pack.
- [ ] **Step 5: Commit** `test: piece 2 end-to-end; docs: follow-ups`
