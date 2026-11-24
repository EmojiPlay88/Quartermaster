# Other-Piece Binding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Binding kind `Piece`: a formula reads a property of a second piece the player picks from a list of visible candidates.

**Architecture:** Rules gains `BindingKind.Piece`, `VariableBinding.PieceFilter`, validator checks and the hidden-input rule. Core gains a shared candidate helper `PieceChoices` and the runner case (label → piece among candidates visible to `RunRequest.PickSides`). Render/Desktop show the binding as a choice box, pass the viewer's sides, and the online host passes the guest's sides. Masking reuses the existing hidden-input path.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-24-other-piece-design.md`

## Global Constraints

- Public repo: invented test data only (fixtures, Riverland demo names). No CNA text.
- Old packs keep bytes and SHA-256: `PieceFilter` omitted when null/empty; `CurrentFormat` stays 4.
- No new NuGet packages. Never open visible windows. One test project at a time, `-c Release`, `--filter` while iterating.
- Never kill processes you did not start. Never `git stash`. Never push.
- Commits: repo-local identity; message ends with a blank line then a `Co-Authored-By:` trailer naming the model that wrote it. Never commit `startapp.cmd`, `.superpowers/`, untracked notes in `docs/`.
- Branch `other-piece` (from master `a063e19`).
- Exact texts:
  - candidate label `{name} ({location})`; off-grid `{name}`; duplicates get ` #2`, ` #3` … in state order (first keeps the plain label); list sorted by label (ordinal-ignore-case)
  - unknown pick: `Choose one of the listed pieces`
  - no candidates: `No piece matches "{filter}"` (empty filter: `No piece to choose`)
  - working description: `{property} of {piece name}`
  - validator: `Formula "{f}": variable "{v}" reads unknown property "{p}"`; `Formula "{f}": variable "{v}" reads holder property "{p}"`; `Formula "{f}": variable "{v}" "which pieces" filter does not parse: {msg}`

## Review Focus

1. **Guest picks a label that, on the host's full state, also matches a piece hidden from that guest** → resolves only among the guest's visible candidates. Test in Task 2 (`Pick_resolves_only_visible_to_pick_sides`).
2. **Masked (Obscurable) enemy piece in the list** → shows as its placeholder name, its Private value never readable by the guest. Test in Task 2 (`Masked_piece_listed_by_placeholder_name`).
3. **Picked piece deleted/moved between list and run** → label no longer resolves → `Choose one of the listed pieces`, nothing written. Test in Task 2 (`Stale_pick_is_error`).
4. **Fully automatic run with a Piece binding** → stops and asks (never auto-picks). Test in Task 2 (`Auto_run_waits_for_piece_pick`).
5. **Guest dialog for a Piece binding on a Private property** → host calculates, working redacted for the guest; Public property shown. Test in Task 3 (`Guest_piece_binding_private_masked`, `Guest_piece_binding_public_shown`).

---

### Task 1: Rules — kind, field, validator, hidden-input rule

**Files:**
- Modify: `src/Quartermaster.Rules/Model/FormulaDef.cs` (`BindingKind.Piece` appended last; `VariableBinding.PieceFilter : string?`), `src/Quartermaster.Rules/Io/RulesPackIo.cs` (omit when null; blank → null on load, as `Expr`)
- Modify: `src/Quartermaster.Rules/Validation/RulesValidator.cs` (the three texts), `src/Quartermaster.Rules/Validation/FormulaVariables.cs` (a Piece binding whose property is not `PropertyFacts.IsPublic` counts as a hidden read in the shared helper PlayerView and FormulaMasking use — add `PieceBindingsHidden(RulesPack, FormulaDef) : IEnumerable<string>` variables), `src/Quartermaster.Rules/Layout/*` and `Render/FormulaEdit.cs` sites that switch on `BindingKind` (grep `BindingKind.Moved` — every exhaustive switch gets `Piece`)
- Test: `tests/Quartermaster.Rules.Tests/PieceBindingTests.cs`
- [ ] **Step 1: Failing tests**: `PieceFilter_round_trips_and_absent_keeps_bytes`; `Validator_unknown_property`; `Validator_holder_property`; `Validator_filter_does_not_parse`; `Hidden_when_property_not_public` (Private stored, Public computed-from-private → hidden; Public → not).
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~PieceBinding"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Rules.Tests; build Core, Render, Desktop, App → PASS / 0 errors.
- [ ] **Step 5: Commit** `feat(rules): piece binding kind`

### Task 2: Core — candidates and runner

**Files:**
- Create: `src/Quartermaster.Core/PieceChoices.cs`: `public sealed record PieceChoice(string Label, string PieceId);` `public static IReadOnlyList<PieceChoice> For(GameState s, RulesPack pack, ModuleModel model, VariableBinding b, IReadOnlySet<string>? sides)` — pieces matching `PieceFilter` (`PieceFilter.Compile` with model), visible per `PieceAccess(s, pack, model, sides).CanSee` when `sides != null`, label via `PieceInfo.Name` (placeholder name for a masked piece, as `PlayerView` names it) and `PieceInfo.Location`, duplicate suffixes, sorted
- Modify: `src/Quartermaster.Core/FormulaRunner.cs`: `RunRequest` gains trailing `IReadOnlySet<string>? PickSides = null`; `BindingKind.Piece` case: typed label → `PieceChoices.For(...)` match (ordinal-ignore-case, trimmed) → `PropertyValues.GetOrDefault` of that piece; no typed value → `Waiting for input` (also under `UseAutoValues`); unknown label → `Choose one of the listed pieces`; empty candidates → the no-candidates text; description `{property} of {name}`
- Test: `tests/Quartermaster.Core.Tests/PieceBindingRunTests.cs`
- [ ] **Step 1: Failing tests**: `Reads_picked_piece_property`; `Default_when_unset`; `Labels_with_location_duplicates_sorted`; `Filter_limits_candidates`; `No_candidates_error`; `Pick_resolves_only_visible_to_pick_sides`; `Masked_piece_listed_by_placeholder_name`; `Stale_pick_is_error`; `Auto_run_waits_for_piece_pick`; `Working_names_piece`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Core.Tests -c Release --filter "FullyQualifiedName~PieceBinding"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2, full Core.Tests → PASS.
- [ ] **Step 5: Commit** `feat(core): pick another piece for a formula binding`

### Task 3: Dialogs, host, Workbench, masking, docs, full runs

**Files:**
- Modify: `src/Quartermaster.Render/FormulaDialogModel.cs` (Piece binding box = choice box with `PieceChoices.For(...).Select(c => c.Label)` as `Choices`, computed on the dialog's state with the viewer's sides; pass `PickSides` in every `RunRequest` it builds), `src/Quartermaster.Render/Online/IntentExecutor.cs` (host run for a guest: `PickSides` = that guest's sides), `src/Quartermaster.Render/FormulaMasking.cs` + `src/Quartermaster.Core/PlayerView.cs` (use `FormulaVariables.PieceBindingsHidden`), `src/Quartermaster.Desktop/Workbench/FormulasTab.Grids.cs` (kind `Piece`; details Property + "Which pieces" filter), `src/Quartermaster.App` (build only; add `Piece` wherever an exhaustive switch needs it)
- Modify: `docs/wiki/Formula-Reference.md` (Piece binding: picking, labels, errors, hidden rule; invented Riverland example: compare a raider's Speed with the picked target's Speed), `docs/wiki/Window-Rules-Workbench.md` (kind list), `docs/superpowers/RESUME.md` (new CURRENT STATE, old under Previous)
- Test: Render `PieceBinding_dialog_lists_visible_choices`, `Guest_piece_binding_private_masked`, `Guest_piece_binding_public_shown`, `Host_resolves_guest_pick_with_guest_sides`; Desktop `Workbench_piece_binding_details`
- [ ] **Step 1: Failing tests.**
- [ ] **Step 2: Run** Render `--filter "FullyQualifiedName~PieceBinding|FullyQualifiedName~Mask"`, Desktop `--filter "FullyQualifiedName~Workbench"` → FAIL.
- [ ] **Step 3: Implement**; wiki; RESUME.
- [ ] **Step 4: Full runs**, one at a time: Rules, Core, Render, Desktop, Docs → PASS (known flaky: Core `PlayerViewTests.Perf_1200Pieces`, Render `Piece4EndToEndTests.Perf_SyntheticGame_UndoAndStepBack`, Render `LauncherTests.Refresh_InvalidPath`, Desktop `DocumentViewerToolbarTests.Add_tool_item_appends`, Desktop `MainWindowOnlineTests.Host_players_dialog_and_guest_tooltip`, Desktop `TurnsEndToEndTests.Round_trip_through_mailbox` — re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `feat: piece binding in dialogs, host and Workbench; docs`
