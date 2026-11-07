# Piece 6a — Players, Visibility and Hot-seat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Players holding sides, rules-pack visibility settings, a `PlayerView` filter that removes what a player may not see, and hot-seat play with a handover screen — the base the online (6b) and turn-file (6c) parts build on.

**Architecture:**
- **Rules** gains pack format 4 visibility fields and validator checks.
- **Core** gains the player list (`SetPlayers` command, save format 7 with `GameId`), piece/holder/map ownership, and the pure `PlayerView.For` filter with log redaction.
- **Render** gains a view state on `ModuleSession` (the filtered state the UI draws from) and a hot-seat controller deciding when a handover is due.
- **App** switches every display read to the view state, adds the players setup, handover screen, player switcher and the Workbench Visibility tab.

**Tech Stack:** .NET 10, C#, WinForms, xUnit (existing).

**Spec:** `docs/superpowers/specs/2026-11-07-piece6-multiplayer-design.md` (binding) — this plan covers §3, §4, §8 (rules format 4, save format 7) and §8a items 3 (hot-seat undo global), 4, 8, 10.

## Global Constraints

- **Content boundary (hard):** no real-game content. Tests use invented sides "Red"/"Blue", the existing `PhaseFixture` and synthetic modules.
- **Solo play unchanged:** a game with one player holding every side (the default, and every older save) behaves exactly as today — no handover, no filtering, all existing tests pass unchanged.
- **Never leak in the filter:** `PlayerView.For` output for a player must not contain any private value, private dice result, private log text or piece on a map owned by a side the player doesn't hold. Tested by serialising and searching.
- **Commands / determinism:** every game-state change goes through `GameSession.Execute`; reducer pure; replay JSON-identical; undo (piece 4) keeps working. The filter is derived, never stored.
- **Formats:** `RulesPackIo.CurrentFormat = 4` (1–3 load with everything public). `NativeSave.FormatVersion = 7` (adds players and `GameId`); formats 1–6 load as solo with a new `GameId`; format-5/6 saves keep full undo.
- **Exact copy:**
  - Handover: `Pass to {player} ({sides})` and button `Continue`. Sides joined with `, `.
  - Phase panel for a side not held: `{Side} is playing {Phase}`.
  - Redacted log lines: `{Side} ledger changed`, `{Side} rolled dice`.
  - Tooltip suffix on totals/alerts/outliner groups when anything was hidden: `visible units only`.
  - Setup page title `Players`; Settings → Game section `Players`; Workbench tab `Visibility`; status-bar switcher text `Player: {name}`.
- **Hot-seat is honour-based:** the filter applies to what the UI shows; the full state stays in memory (spec §4).
- **Layering:** logic in Rules/Core/Render with tests; App is wiring.
- **Look:** classic gray (Tahoma 8.25pt, system colours, DPI-aware).
- **Build:** `TreatWarningsAsErrors`; warning-free except the known pre-existing MSB3277 WebView2 warning; `dotnet test Quartermaster.slnx` green after every task.
- **Test isolation / smokes:** never touch the real `%LOCALAPPDATA%` (smokes use `QUARTERMASTER_LAYOUT_ROOT=<scratch>/qmroot`); screenshots only in scratch.
- **Commits:** end with a blank line then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; noreply identity; never stage `startapp.cmd`.

## Review Focus

1. **A piece moving onto an owned map / changing owner mid-game** (its side filter starts or stops matching after a ledger change): the filtered view must follow immediately, with no stale piece left visible. Task 3 test `PlayerView_PieceMovesOntoOwnedMap_Disappears`.
2. **Log entries that mention a hidden piece by name** in free text (move lines, formula working lines, warnings with `{unit}`): redaction must catch them, not just entries with a `PieceId`. Task 3 test `PlayerView_LogTextWithHiddenName_Redacted`.
3. **Undo/step back across a handover:** rewinding into the other side's phase must show the handover again for the right player and never flash the other player's private view. Task 5 test `Rewind_IntoOtherSidesPhase_RequiresHandover`.
4. **Old saves and packs** (format ≤ 6 saves, format ≤ 3 packs) load as solo with everything public and keep undo. Task 2 test `Format6Save_LoadsSolo_KeepsUndo_GetsGameId`.
5. **Player list edits that leave a side unheld or held twice:** validator refuses (every side held by exactly one player when the list has more than one player). Task 2 test `SetPlayers_Invalid_RejectedWithMessage`.

---

## File Structure

```
src/Quartermaster.Rules/Model/RulesPack.cs, PhaseDefs.cs, FormulaDef.cs – Visibility enum, PropertyDef.Visibility, HolderDef.SideId, MapOwner, RulesPack.MapOwners, FormulaDef.PrivateRolls
src/Quartermaster.Rules/Io/RulesPackIo.cs, Validation/*          – format 4, checks
src/Quartermaster.Core/Players.cs        – PlayerDef, Players helpers (validation, who holds a side) (new)
src/Quartermaster.Core/Ownership.cs      – side of a piece / holder / map (new)
src/Quartermaster.Core/PlayerView.cs     – the filter + log redaction (new)
src/Quartermaster.Core/Commands.cs, GameState.cs, NativeSave.cs – SetPlayers, Players, GameId, format 7
src/Quartermaster.Render/ModuleSession.cs – ViewSides, View (filtered state, cached)
src/Quartermaster.Render/HotSeat.cs      – handover decisions (new)
src/Quartermaster.App/*                  – display reads → View; handover screen; players UI; switcher
src/Quartermaster.App/Workbench/VisibilityTab.cs (new)
```

## Task dependencies

- Task 1 and Task 2 independent. Task 3 needs 1 and 2. Task 4 needs 3. Task 5 needs 4.
- Task 6 needs 4. Task 7 needs 5 and 6. Task 8 needs 1. Task 9 needs all.

---

### Task 1: Rules pack format 4 — visibility settings

**Files:** Modify `src/Quartermaster.Rules/Model/RulesPack.cs`, `FormulaDef.cs`, `Io/RulesPackIo.cs`, `Validation/RulesValidator.cs`. Test `tests/Quartermaster.Rules.Tests/RulesPackIoTests.cs`, `ValidatorTests.cs`.

**Interfaces — produces:**
- `public enum Visibility { Private, Public }`; `PropertyDef.Visibility = Private`.
- `HolderDef.SideId : string?` (null = public).
- `public sealed record MapOwner { string MapId; string SideId; }`; `RulesPack.MapOwners : List<MapOwner>`.
- `FormulaDef.PrivateRolls : bool = false`.
- `CurrentFormat = 4`. **Formats 1–3 load with every property `Public`** (old packs stay fully visible), no holder owners, no map owners, `PrivateRolls = false`. New properties created in format 4 default to `Private`.
- Validator: owner side ids that don't exist; map owner for an unknown map id (needs module → reuse how the validator gets the module, or skip map-id check when no module); duplicate map owners.

- [ ] **Step 1: Failing tests:** `Format4_RoundTrip_Visibility`, `Format3Pack_LoadsAllPublic`, `Format5Pack_Rejected`, `Validator_FlagsBadOwners`.
- [ ] **Step 2:** `dotnet test tests/Quartermaster.Rules.Tests` → FAIL. **Step 3:** implement. **Step 4:** `dotnet test Quartermaster.slnx` → PASS.
- [ ] **Step 5:** Commit `feat(rules): pack format 4 visibility settings`.

### Task 2: Players, GameId, save format 7

**Files:** Create `src/Quartermaster.Core/Players.cs`. Modify `Commands.cs`, `GameState.cs`, `GameLog.cs`, `NativeSave.cs`, `Render/ModuleSession.cs` (new games get a `GameId`), `App/CommandLog.cs` (Stamp). Test `tests/Quartermaster.Core.Tests/PlayersTests.cs`, `NativeSaveTests.cs`.

**Interfaces — produces:**
- `public sealed record PlayerDef(string Id, string Name, IReadOnlyList<string> Sides);`
- `GameState.Players : List<PlayerDef>` (empty = solo: one implicit player holding every side); `GameState.GameId : string` (GUID "N" format), set when a game is created (`ModuleSession.Replace` sets it on the genesis state before `Rebase`, so replay is unaffected).
- `public sealed record SetPlayers(IReadOnlyList<PlayerDef> Players, DateTimeOffset? At = null, string? Who = null) : GameCommand` (`$t` `"setPlayers"`, not folding). Log line `Players: {name} ({sides}); …` (kind System).
- `public static class Players`:
  - `IReadOnlyList<PlayerDef> Effective(GameState s, RulesPack? pack)` — the list, or the implicit solo player `("solo", "Player", all side ids)`.
  - `PlayerDef? Holding(GameState s, RulesPack? pack, string sideId)`.
  - `string? Validate(IReadOnlyList<PlayerDef> players, RulesPack pack)` — null when fine; with 2+ players every side must be held by exactly one player, names non-empty and unique, at most 6 players.
  - `bool IsSolo(GameState s)` = `Players.Count <= 1`.
- Save format 7; formats 1–6 load with `Players` empty and a new `GameId` assigned **without breaking the undo consistency check** (assign it to both genesis and snapshot after the check, like piece 5's format-5 path); format 5/6 keep `CanUndo`.

- [ ] **Step 1: Failing tests:** `SetPlayers_ReplaysAndUndoes`, `Effective_Solo_HoldsAllSides`, `SetPlayers_Invalid_RejectedWithMessage` (Review Focus 5: unheld side, double-held side, duplicate names, 7 players), `NewGame_HasGameId`, `Format6Save_LoadsSolo_KeepsUndo_GetsGameId` (Review Focus 4), `Format7_RoundTrip`.
- [ ] **Step 2:** run → FAIL. **Step 3:** implement (update tests asserting format 6). **Step 4:** full suite → PASS.
- [ ] **Step 5:** Commit `feat(core): players and game id; save format 7`.

### Task 3: Ownership and the PlayerView filter

**Files:** Create `src/Quartermaster.Core/Ownership.cs`, `PlayerView.cs`. Test `tests/Quartermaster.Core.Tests/PlayerViewTests.cs`.

**Interfaces:**
- Consumes: Task 1 fields, Task 2 `Players`.
- Produces:
  - `public static class Ownership { string? SideOfPiece(PieceInstance p, GameState s, RulesPack pack); string? SideOfHolder(string holderId, RulesPack pack); string? SideOfMap(string mapId, RulesPack pack); }` — piece: first side whose `UnitFilter` matches (compiled once per call batch; offer `Func<PieceInstance,string?> PieceSides(GameState, RulesPack)` for bulk use).
  - `public static class PlayerView { GameState For(GameState full, RulesPack? pack, ModuleModel model, IReadOnlySet<string> heldSides); }` — returns a deep-cloned, filtered state:
    - pieces (and their stacks) on maps owned by a side not held: removed;
    - pieces of a side not held with an active VASSAL `obs` (mask) trait: type/state replaced by the mask image state (builder: use the trait's masked appearance); active `hide`: removed;
    - ledger values (`Ledgers`) of keys belonging to a side not held, for properties with `Visibility.Private`: removed; same for `LedgerHistory` entries;
    - holder ledger rows of holders owned by a side not held: removed entirely;
    - `DiceHistory` / dice log entries from formulas with `PrivateRolls` run for a side not held: results removed, log text `{Side} rolled dice`;
    - `Log`: ledger lines for removed values → `{Side} ledger changed`; lines whose `PieceId` was removed → dropped; lines whose text names a removed piece (by its display name) → dropped (Review Focus 2); calc/working lines of formulas run on another side's units → `{Side} ledger changed`;
    - `Pending` runs whose units all belong to sides not held: removed; units of other sides inside mixed runs: removed;
    - `Ticked`, `Acknowledged`, `Position`, `Turn`, `Difficulty`, `Players`, `GameId`, `Notes`: unchanged.
  - With `heldSides` = every side, or no pack, `For` returns a clone equal (JSON) to the input.
- [ ] **Step 1: Failing tests:** `AllSidesHeld_ReturnsEqualClone`; `OwnedMap_PiecesRemovedForOtherSide`; `PrivateLedger_RemovedForOtherSide_PublicKept`; `PrivateHolder_Removed`; `PrivateRolls_ResultsRemoved_LogRedacted`; `Mask_ShowsMaskedState`, `Hide_Removed`; `PendingRuns_FilteredBySide`; `PlayerView_LogTextWithHiddenName_Redacted`; `PlayerView_PieceMovesOntoOwnedMap_Disappears` (Review Focus 1); **`Leak_SerialisedBlueView_ContainsNoRedPrivateData`** (seed distinctive values like 9137.5 and names like "RedSecretUnit"; serialise and assert none appear); `Perf_1200Pieces_Under50ms` (`[Trait("Category","Perf")]`, assert < 200 ms, print time).
- [ ] **Step 2:** run → FAIL. **Step 3:** implement. **Step 4:** full suite → PASS.
- [ ] **Step 5:** Commit `feat(core): ownership and player view filter`.

### Task 4: View state on the session

**Files:** Modify `src/Quartermaster.Render/ModuleSession.cs`. Test `tests/Quartermaster.Render.Tests/ViewStateTests.cs`.

**Interfaces — produces:**
- `ModuleSession.ViewSides : IReadOnlySet<string>?` — null = everything (solo, or no pack).
- `ModuleSession.View : GameState?` — `Game.State` when `ViewSides` is null, else `PlayerView.For(...)`, cached and invalidated on `Game.Changed`, `Game.Rewound`, `RulesChanged`, `GameReplaced` and `ViewSides` changes.
- `event Action? ViewChanged` — raised after the cache is invalidated (UI refresh hook; one event per change, debounce in App where needed).
- `void SetViewSides(IReadOnlySet<string>? sides)`.
- Helper `bool CanSee(string pieceId)`, `bool CanSeeMap(string mapId)`.
- [ ] **Step 1: Failing tests:** `Solo_ViewIsGameState`, `SetViewSides_FiltersAndRaisesViewChanged`, `GameChange_InvalidatesView`, `Rewind_InvalidatesView`, `CanSeeMap_RespectsOwners`.
- [ ] **Step 2–4:** RED → implement → full suite PASS. **Step 5:** Commit `feat(render): filtered view state on the session`.

### Task 5: Hot-seat controller

**Files:** Create `src/Quartermaster.Render/HotSeat.cs`. Test `tests/Quartermaster.Render.Tests/HotSeatTests.cs`.

**Interfaces:**
- Consumes: Task 2 `Players`, Task 4 `SetViewSides`, `PhaseController` (acting side via `Sequencer.ActingSide`).
- Produces `public sealed class HotSeat(ModuleSession s, PhaseController phases)`:
  - `PlayerDef? Current { get; }`; `bool Active` (= 2+ players).
  - `PlayerDef? HandoverDue()` — the player who must take over now: when phases exist, the player holding the acting side if different from `Current`; else null.
  - `void TakeOver(PlayerDef p)` — sets `Current`, calls `SetViewSides(p.Sides)` (or null when solo); raises `CurrentChanged`.
  - `event Action<PlayerDef>? HandoverRequired` — raised after `PhaseBoundary`, after rewinds (`HistoryController.Rewound`) and after game load when `HandoverDue()` is non-null.
  - On game load with 2+ players: `HandoverRequired` for the acting side's player (or the first player without phases).
- [ ] **Step 1: Failing tests:** `Solo_NeverHandsOver`; `NextPhase_ToOtherSide_RequiresHandover`; `SameSideNextPhase_NoHandover`; `TakeOver_SetsViewSides`; `Rewind_IntoOtherSidesPhase_RequiresHandover` (Review Focus 3); `Load_TwoPlayers_HandoverForActingSide`.
- [ ] **Step 2–4:** RED → implement → PASS. **Step 5:** Commit `feat(render): hot-seat controller`.

### Task 6: Display reads use the view state

**Files:** Modify App readers of `Game.State` used for **display**: `MapCanvas*` (drawing, hover popup, selection pruning), `LedgerWindow`, `InventoryWindow`, `OutlinerPanel`/`MainForm.Outliner`, `MainForm.Alerts` (alerts computed from `View`), `LogPanel`, `CounterDetailPopup`/`PieceDetails`, `ModuleTreePanel` (hide maps the player can't see), `PhasePanel` (`{Side} is playing {Phase}` when the acting side isn't held), `BatchGridWindow`/`FormulaDialog` (may only open for held sides' units; values of hidden units show as hidden). Commands are still issued against `Game.State`.
- Render-side totals that cover only visible units (inventory, outliner groups, alert tooltips) add `visible units only` to their tooltip when `ViewSides` is not null and anything was filtered.
- Subscribe to `ModuleSession.ViewChanged` where windows refresh on `Changed` today.
- Keep a Render-level test for any helper you add; otherwise verify with the Task 9 smoke.
- [ ] **Step 1:** Sweep (grep `Game.State`, `Game?.State`, `.State.` in App) and switch display paths; leave command paths.
- [ ] **Step 2:** `dotnet test Quartermaster.slnx` → PASS; build warning-free.
- [ ] **Step 3:** Commit `feat(app): windows draw the player's view`.

### Task 7: Players UI, handover screen, player switcher

**Files:** Modify `SetupChooserDialog.cs` (new `Players` page after difficulty: rows of name + side checkboxes; default one player holding all sides), `SettingsTabs.cs` (Game → `Players` editor, executes `SetPlayers` after `Players.Validate`), `MainForm*.cs`. Create `HandoverForm.cs`.

**Behaviour:**
- `HotSeat.HandoverRequired` → hide every Quartermaster window (MDI children, floating windows, docks, popups), close formula dialogs / batch grids / cheat sheets (reuse `CloseFormulaWindows`), show `HandoverForm` maximised over the main window: `Pass to {player} ({sides})` + `Continue`. Continue → `HotSeat.TakeOver(p)`, restore windows.
- Status bar: `Player: {name}` button; click → menu of players; choosing one shows the handover screen for them first.
- Hot-seat undo stays global (spec §8a.3).
- [ ] **Step 1:** Implement. **Step 2:** full suite PASS, build warning-free.
- [ ] **Step 3:** Commit `feat(app): players setup, handover screen and switcher`.

### Task 8: Workbench Visibility tab

**Files:** Create `src/Quartermaster.App/Workbench/VisibilityTab.cs`; modify `WorkbenchWindow.cs`. Optional UI-free helper with tests (`tests/Quartermaster.Render.Tests/VisibilityEditTests.cs`).

**Behaviour:** three grids — Properties (name, Private/Public), Holders (name, owner side or "(none)"), Maps & org displays (name from the module, owner side or "(none)"); plus a Formulas list with a "Private dice" checkbox. Commits through `UpdateRules` like the other tabs; validator issues shown below.
- [ ] **Step 1:** Helper tests RED → GREEN if a helper is added. **Step 2:** implement. **Step 3:** full suite PASS.
- [ ] **Step 4:** Commit `feat(app): workbench visibility tab`.

### Task 9: End-to-end, smokes, docs

**Files:** Create `tests/Quartermaster.Render.Tests/Piece6aEndToEndTests.cs`; modify `Program.cs`, smoke files, `README.md`, `docs/superpowers/RESUME.md`, `docs/superpowers/piece1-followups.md`.

- [ ] **Step 1: E2E:** invented fixture with sides Red/Blue, a private property, an owned org display and a private-dice formula; two players; play through a phase change → handover due → `TakeOver(Blue)` → `View` has none of Red's private data (leak search) → step back → handover due for Red → save/load keeps players and `GameId`.
- [ ] **Step 2: Smokes:** `--screenshot-handover <png>` (handover screen) and `--screenshot-playerview <png>` (Blue's map/ledger with Red hidden). Fixture via `SmokeFixtureWriter` + `QM_WRITE_SMOKE_FIXTURE` in scratch; run with `QUARTERMASTER_LAYOUT_ROOT`; inspect PNGs.
- [ ] **Step 3: Docs:** README gains a "Play with others" bullet (hot-seat now; online and turn files coming); RESUME piece 6a row and next steps (6b live online); followups: 6a rulings/minors. Keep the user's intro wording; no personal paths, emails or real-game content.
- [ ] **Step 4:** full suite PASS; smokes exit 0. **Step 5:** Commit `test: piece 6a end-to-end and smokes; docs`.
