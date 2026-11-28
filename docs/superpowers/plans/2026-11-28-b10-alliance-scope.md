# Phase Entries Reach Allies' Counters (B10, alliance scope) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a phase's side has allies, its per-unit start/end entries, its unit warnings and its OnMove **Acting side** rows cover the counters of every allied side (by B8 first-match ownership), and **Other side** means counters outside the alliance plus side-less ones. Packs without alliances behave exactly as before and pay nothing.

**Architecture:** Rules gains `SideFacts.Allies`. Core gains one scope predicate `Sequencer.Covers` that `UnitsFor` and `MovedUnitsFor` use (plus a `MovedUnitsFor` overload taking a shared side map). Render's `PhaseController.OnMoveCore` shares its existing lazy owner map. Desktop changes one tooltip; wiki texts follow.

**Tech Stack:** C# .NET 10, Avalonia (Desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-28-b10-alliance-scope-design.md`

## Before Task 1: baseline

The branch starts at master `75526ef` (B9 merged); no merge is needed. Run the Rules, Core and Render test projects once as a baseline and record the counts in the ledger (`.superpowers/sdd/2026-11-28-b10-alliance-scope/`).

## Global Constraints

Read `.superpowers/sdd/2026-11-28-b10-alliance-scope/global-constraints.md` (git-ignored; same folder as the ledger) before each task. It holds the invariants, the invented fixture with its expected unit lists, and **every exact tooltip and wiki string**; use them verbatim. In short:
- Public repo: invented data only (Northern Foot / Wagons / Balloons in `Northern Pact`, Southern Compact). No content from any published game.
- No-alliance packs: today's exact code path, same units and order, `Ownership.SideMapBuilds` stays 0. No pack, save, command or intent change; `CurrentFormat` 4.
- Additive API only; one scope function (`Sequencer.Covers`); ownership, stamps, seats, handover, `MayAct`, `phaseside()` unchanged.
- `-c Release`, one test project at a time, headless only, never stash/push, repo-local identity, commits end with `Co-Authored-By: Claude <Model> 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **No-alliance packs unchanged**: `Covers` returns the exact old compiled filter when `SideFacts.Allies` is null; `No_alliance_units_and_order_pinned` and `No_alliance_builds_no_side_map` (Task 1) pass; no file under `src/Quartermaster.Rules/Model` or `Io` changes (`git diff 75526ef -- src/Quartermaster.Rules/Model src/Quartermaster.Rules/Io` empty).
2. **Ownership semantics**: an allied counter owned first-match by an enemy (`NW 2 taken`) is not covered in an ally's phase but stays covered in its own side's phase (old reach kept); Acting and Other never overlap and together are Any.
3. **One scope function**: `grep -rn "Allies(\|AllianceId" src --include=*.cs` — `Allies` only in `SideFacts`, `Sequencer.Covers`, `PhaseController.OnMoveCore`; `AllianceId` only where B8 allowed it.
4. **No leak**: unit selection on a player view (`Decisions.Warnings` from the phase panel) uses only the view; a `south` viewer's view and log gain nothing (Task 1 Render hot-seat/online tests).
5. **Cost**: one side map per entry selection and one per move gesture with alliances; zero without.

---

### Task 1: Rules/Core/Render — the alliance scope — model: opus

**Files:**
- Modify: `src/Quartermaster.Rules/Validation/SideFacts.cs`: add
  ```csharp
  /// <summary>B10: the side and every other side with the same alliance (trimmed, case-insensitive), ordinal; null when the side is
  /// unknown, has no alliance, or no other side shares it.</summary>
  public static IReadOnlySet<string>? Allies(RulesPack? pack, string? sideId)
  ```
- Modify: `src/Quartermaster.Core/Sequencer.cs`: add
  ```csharp
  /// <summary>B10: the pieces a phase's per-unit entries cover (spec "The scope rule"). No known side: every piece. No allies
  /// (SideFacts.Allies null): the phase side's unit filter, exactly as before. Else: that filter, or a first-match owner
  /// (Ownership.PieceSides) among the allies; the side map is built at most once, lazily, unless <paramref name="owners"/> is given.</summary>
  public static Func<PieceInstance, bool> Covers(RulesPack pack, GameState s, PhaseDef phase, ModuleModel? model = null,
      Func<PieceInstance, string?>? owners = null)
  public static IReadOnlyList<PieceInstance> MovedUnitsFor(RulesPack pack, GameState s, PhaseDef phase, FormulaDef? formula, MoveEntry e,
      ModuleModel? model, Func<PieceInstance, string?>? owners)
  ```
  `UnitsFor` replaces `fs` by `Covers(pack, s, phase, model)` (no side: `Covers` returns `_ => true`, as `Compile(null)` did); the old
  6-parameter `MovedUnitsFor` forwards to the new overload with `owners: null`; Other = `!covers(p)`. Update both doc comments
  ("the phase side and its allies"; "Other: not covered").
- Modify: `src/Quartermaster.Render/PhaseController.cs` `OnMoveCore`: when `SideFacts.Allies(pack, phase.SideId)` is non-null, build
  `owner = Ownership.PieceSides(g.State, pack)` before the loop and pass it to the new `MovedUnitsFor` overload (the Other/Any grouping
  keeps using the same `owner`); without allies, unchanged code. `RunEntries` and `BadFilter`: no edit.
- Test: `tests/Quartermaster.Rules.Tests/SideFactsTests.cs` (append), `tests/Quartermaster.Core.Tests/AllianceScopeTests.cs`,
  `tests/Quartermaster.Render.Tests/AllianceScopeHotSeatTests.cs`, `tests/Quartermaster.Render.Tests/AllianceScopeOnlineTests.cs`.
- [ ] **Step 1: Failing tests** (fixture and expected lists in the constraints file):
  - Rules: `Allies_lists_alliance_members_case_insensitive` (`north-land` → {`north-land`, `north-supply`, `north-air`}); `Allies_null_without_alliance_unknown_side_or_lone_alliance`.
  - Core `AllianceScopeTests`:
    - `UnitsFor_covers_allied_counters_in_pack_piece_order` (Foot Supply: ids of `NF 1`, `NW 1`, `NB 1`, `NF 2 taken`, in that order);
    - `UnitsFor_ally_counter_owned_by_enemy_not_covered` (`NW 2 taken` absent in Foot Supply);
    - `UnitsFor_own_filter_reach_kept_when_enemy_owns_first` (Wagon Supply includes `NW 2 taken`);
    - `UnitsFor_formula_applies_to_and_entry_filter_still_apply` (entry filter `startsWith(name(), "NW")` in Foot Supply → `NW 1` only);
    - `UnitsFor_phase_without_side_every_piece` (unchanged);
    - `MovedUnitsFor_acting_includes_allies`; `MovedUnitsFor_other_is_outside_alliance_plus_sideless` (all seven moved, Foot Supply, Other → `SC 1`, `NW 2 taken`, `Loose crate`); `MovedUnitsFor_acting_and_other_partition_any`;
    - `MovedUnitsFor_shared_owner_map_builds_once` (reset `Ownership.SideMapBuilds`, build one `owners` map in the test (count 1), pass it to three calls Acting/Other/Any: count still 1; without `owners` each Acting/Other call adds at most 1);
    - `Warnings_reach_allied_units` (`Decisions.Warnings` with condition `startsWith(name(), "NB")` and message `{unit} aloft` in Foot Supply → one hit `NB 1 aloft`);
    - `No_alliance_units_and_order_pinned` (every `AllianceId` null: Foot Supply `NF 1`, `NF 2 taken`; Wagon Supply `NW 1`, `NW 2 taken`; Moved Other in Foot Supply = all but `NF 1`, `NF 2 taken`; the lists are hard-coded, not computed);
    - `No_alliance_builds_no_side_map` (`Ownership.SideMapBuilds == 0` after `UnitsFor`, both `MovedUnitsFor` modes and `Warnings`, also for a pack where only `north-air` has an alliance — a lone alliance);
    - `Perf_alliance_scope_1200Pieces` (1200 pieces spread over the four prefixes, Foot Supply: `UnitsFor` best of 5 `< 300` ms, as `AlertEngineTests.Perf_TenAlerts_1200Pieces_Under100ms`; logs both the alliance and no-alliance timings).
  - Render `AllianceScopeHotSeatTests` (hot-seat, Fully automatic, pattern of `AllianceFixture.cs` / `PhaseControllerHooksTests`): `Start_entry_for_each_unit_writes_allied_counters` (Foot Supply OnStart ForEach units: ledger of `NW 1` and `NB 1` written, `SC 1` and `NW 2 taken` untouched; one undo takes all back); `OnMove_acting_fires_for_ally_counter` (drag `NB 1` in Foot Supply); `OnMove_other_skips_ally_fires_for_enemy` (drag `NW 1`: no run; drag `SC 1`: run stamped `south`); `South_view_learns_nothing_new` (`PlayerView.For` for `south` after the start entry: no Private value or line of the `north-*` counters); `No_alliance_pack_same_log_as_before` (same scenario with alliances cleared: the log texts equal a hard-coded list).
  - Render `AllianceScopeOnlineTests` (host + guest, as the B8 online tests): `Guest_ally_move_fires_acting_entry_on_host` (guest holding `north-supply` drags `NW 1` in Foot Supply: the host runs the Acting row once); `Guest_south_snapshot_unchanged_by_allied_runs`.
- [ ] **Step 2: Run** `dotnet test tests/Quartermaster.Rules.Tests -c Release --filter "FullyQualifiedName~SideFacts"`, Core `--filter "FullyQualifiedName~AllianceScope"`, Render `--filter "FullyQualifiedName~AllianceScope"` → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** Step 2; Core `--filter "FullyQualifiedName~Sequencer|FullyQualifiedName~PhaseHooks|FullyQualifiedName~Decisions|FullyQualifiedName~Alert"`; Render `--filter "FullyQualifiedName~PhaseController|FullyQualifiedName~Alliance|FullyQualifiedName~PhasePipeline"`; then the full Rules.Tests, Core.Tests and Render.Tests, one at a time (flaky lists: re-run once, report both) → PASS. Build Desktop, App (`-c Release`). Run the Review Focus 1 and 3 greps and paste their output in the report.
- [ ] **Step 5: Commit** `feat: phase entries and OnMove rows reach allied sides' counters`

### Task 2: Desktop tooltip, wiki, RESUME, full runs — model: sonnet

**Files:**
- Modify: `src/Quartermaster.Desktop/Workbench/PhasesTab.cs`: `AllianceTip` = the constraints file's text; `tests/Quartermaster.Desktop.Tests/B8WorkbenchTests.cs`: its tooltip assertion to the new text.
- Modify: `docs/wiki/Making-a-Rules-Pack.md` (`### Alliances`: the new bullet; drop the On move clause from "What does not change"; `### Moves by the other side` gets one sentence `With alliances, **Other side** means counters outside the phase side's alliance; see [Alliances](#alliances).`), `docs/wiki/Window-Phases.md` (`### Moves by the other side` table rows; `### Phases of an ally` sentence), `docs/wiki/Window-Rules-Workbench.md` (`Alliance:` row). Every string verbatim from the constraints file; invented Riverland names only.
- Modify: `docs/superpowers/RESUME.md`: a new CURRENT STATE section for B10 (B9 moved under Previous): what changed, `Sequencer.Covers` as the one scope function, downstream note in neutral wording ("downstream pack": no pack bytes change; a pack with alliances now has its phase entries reach allied counters; a pack that wants the old reach filters the entry with `side() == "..."`).
- Test: Docs `WikiLintTests`; Desktop `B8WorkbenchTests`.
- [ ] **Step 1: Failing test**: update the `B8WorkbenchTests` tooltip assertion to the new text; run Desktop `--filter "FullyQualifiedName~B8Workbench"` → FAIL.
- [ ] **Step 2: Implement** the tooltip, the wiki and RESUME.
- [ ] **Step 3: Run** Step 1 → PASS; `dotnet test tests/Quartermaster.Docs.Tests -c Release --filter "FullyQualifiedName~WikiLint"` → PASS; `grep -n "does not fire for it" docs/wiki/*.md` → nothing.
- [ ] **Step 4: Full runs**, one at a time: Rules, Vassal, Core, Render, Desktop, Docs, Net → PASS (flaky: re-run once, report both). Build `src/Quartermaster.App -c Release`.
- [ ] **Step 5: Commit** `docs: B10 phase entries reach allies' counters`
