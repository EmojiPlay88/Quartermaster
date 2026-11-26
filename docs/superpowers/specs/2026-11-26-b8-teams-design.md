# Teams: former side ids and alliances (batch B8) — design (short)

Date: 2026-11-26. Branch `b8-teams` (planned from `0f33dfe`, B7 after its Task 2). B7 (visibility, reveals, sealed answers) merges to
master **before** B8 is built; the implementation starts by bringing `b8-teams` up to that master head (see the plan). Engine wishlist #8
(`SideDef.FormerIds`) then #2 (`SideDef.AllianceId`). The user accepted every default of the batch; the choices below are those defaults,
made concrete. Details the defaults left open are marked "decided".

## Intent

- A pack that is rebuilt, or whose two sides are merged into one, gives sides new ids; every save made with the older pack then names
  sides that no longer exist: its players hold nothing, its stamped log lines and ask runs belong to no one, its winners are unknown (#8).
- Games with more than two sides have teams: the Hill Clans fight beside the Northern League. Today two allied sides hide everything from
  each other and cannot touch each other's counters; the players work around it by holding both sides in one seat (#2).

## Principles (decided)

1. **One place forms a held set.** Every rule that asks "does this viewer hold side X" (`HiddenValues`, `PieceAccess.NotHeld`/`CanAct`,
   `Sequencer.MayAct`, `IntentExecutor.ActingSideHeld`, the alert sides, the private-dice and stamped-line rules of `PlayerView`, map
   owners, checklists, ask runs) already reads one set of side ids. B8 widens that set once, where a player's sides become a held set,
   with one new function `SideFacts.Held(pack, sides)`; no rule gets its own ally test.
2. **Former ids are a load-time translation.** They are applied to the loaded game (state and its undo history) and to the pack's own
   references, never at every read. A save is rewritten only when the user saves it.
3. **Ownership does not change.** A counter, holder or map still belongs to exactly one side; `side()` still names that side; a seat
   still holds exactly the sides it chose. Only what a player may see and act on widens.
4. Packs without the features pay nothing: `SideFacts.Held` returns its input set unchanged when no side has an alliance or a former id;
   the former-id translation is skipped when no side has former ids.

Every new pack field is optional and omitted from JSON when empty or default; blank text becomes null on load (`RulesPackIo.Normalise`).
Old packs keep their bytes and SHA-256, `CurrentFormat` stays 4. Old saves keep their bytes: nothing is written on load, and a game
whose save names no former id is not touched in memory either. The validator checks every new field. Every new field gets a Workbench
editor and a wiki section with invented Riverland examples (no content from any published game). No new expression function (decided).
Public API changes are additive.

## Design (decided)

### Pack model (Rules)
- `SideDef.FormerIds : List<string>?` (`WhenWritingNull`): ids this side had in older versions of the pack. `Normalise` trims every entry,
  drops blank ones and duplicates (ordinal), and turns an empty list into null.
- `SideDef.AllianceId : string?` (`WhenWritingNull`): sides with the same alliance (compared trimmed, case-insensitively) are allies.
  `Normalise` trims it; blank → null. It is a name, not an expression (any text).

JSON (invented):
```json
{ "id": "north", "name": "Northern League", "unitFilter": "startswith(name(), \"NL\")", "formerIds": ["league"], "allianceId": "Upriver Pact" }
{ "id": "hills", "name": "Hill Clans", "unitFilter": "startswith(name(), \"HC\")", "allianceId": "Upriver Pact" }
```

### `SideFacts` (new Rules static, `src/Quartermaster.Rules/Validation/SideFacts.cs`, namespace `Quartermaster.Rules`)
- `FormerMap(RulesPack? pack) : IReadOnlyDictionary<string, string>?` — former id → listing side id (ordinal); null when no side has a
  former id. A former id that is also a current side id is ignored (the validator reports it); a former id listed by two sides maps to the
  first in pack order (the validator reports it).
- `Canonical(RulesPack? pack, string? sideId) : string?` — the listing side for a former id, else the id unchanged.
- `Alliance(SideDef? side) : string?` — the trimmed alliance or null.
- `HasAlliances(RulesPack? pack) : bool` — some side has an alliance.
- `Held(RulesPack? pack, IEnumerable<string>? sides) : IReadOnlySet<string>?` — **the held set of a player**: null stays null (solo); each
  id is first translated by `Canonical`; then every side whose alliance equals (case-insensitively) the alliance of a side in the set is
  added. When the pack has no alliance and no former id and `sides` is an `IReadOnlySet<string>`, the same instance is returned (no
  copy); otherwise a new ordinal `HashSet<string>`. The empty set stays empty (a hot-seat handover screen shows nothing).
- `Groups(RulesPack pack) : int` — the number of alliance groups (a side without an alliance is a group of its own).
- `MapReferences(RulesPack pack) : RulesPack` — the pack with every side reference that names a former id rewritten to the listing side:
  `PhaseDef.SideId`, `PhaseDef.Reactors` (translated, deduplicated, the phase's own side dropped, empty → null), `StartEntry.AskSideId` in
  `OnStart` and `OnEnd`, `HolderDef.SideId`, `MapOwner.SideId`, `AlertDef.SideId`, `VictoryCondition.SideId`. Returns the same instance
  when `FormerMap` is null or no reference changes. Called last in `RulesPackIo.Normalise` (#8 "holder/map owners, alerts → listing side").

### #8 Former ids applied to a loaded game (Core `FormerSides`, Render `ModuleSession`)
New `src/Quartermaster.Core/FormerSides.cs`, `public static class FormerSides`:
- `Uses(GameState s, IReadOnlyDictionary<string, string> map) : bool`, `Uses(GameCommand c, …) : bool`.
- `Apply(GameState s, IReadOnlyDictionary<string, string> map) : bool` (in place; true when something changed) over every side id a state
  holds: `Players[].Sides` (translated, deduplicated in order), `Pending[].AskSideId`, `Log[].SideId`, `GameOver.Winners` (translated,
  deduplicated), `ViewSides` (a viewer copy's; translated, deduplicated, sorted ordinal as `PlayerView.For` writes it).
- `Apply(GameCommand c, IReadOnlyDictionary<string, string> map) : GameCommand` — the same instance when nothing changes, else a `with`
  copy, for every command member holding a side id: `RemovePiece.SideId`, `LedgerDelta.SideId`, `RollDice.SideId`, `AddLogLine.SideId`,
  `SetPlayers.Players[].Sides`, `QueueRuns.Runs[].AskSideId`, `EndGame.Winners`, and B7's `TickChecklist.SideId` and
  `AcknowledgeWarning.SideId` (re-check after B7 merges: any other side-id member B7 added is mapped too).
- A guard test enumerates by reflection every `string`/`string?` member whose name ends in `SideId`, and every `IReadOnlyList<string>`/
  `List<string>` member named `Sides`, `Winners` or `ViewSides`, on `GameState`, `PlayerDef`, `PendingRun`, `LogEntry`, `GameOverState` and
  every `GameCommand` type registered with `[JsonDerivedType]`, and compares the list with the members `FormerSides` maps. A later batch that
  adds a side-id member fails it until the member is mapped. (`RollDice.Sides` is an `int`, not a side.)

`GameSession.MapSides(IReadOnlyDictionary<string, string> map) : bool` (new public method): when neither `Genesis`, `State`, the command
log nor the redo stack uses a former id, returns false and changes nothing. Else it maps `Genesis` in place, replaces each logged and
each undone command by its mapped copy, and sets `State` to the replay of the mapped genesis through the mapped log (so a later save
replays to its snapshot exactly; the `Players:` line a SetPlayers writes then names the listing ids). No event is raised; action marks,
checkpoints and the turn base are unchanged (the command count is). One undo after it still takes back one action.

`ModuleSession.MapFormerSides()` (private) runs `SideFacts.FormerMap(Rules)` and `Game.MapSides(map)`:
- in `LoadNative` (after `Game = loaded`, before `SeedLedgers`), also for a viewer copy;
- in `Commit` and `UpdateRules` when a game is open (before `SeedLedgers`), so a Workbench merge reaches the open game at once.
- Never in guest mode (the host's snapshots are already translated), never for a turn being replayed (decided: a received turn stays
  exactly the sender's; both players open their saves with the same pack before they exchange turns, which the wiki says).
- It remembers the last map it applied (by content) and skips the scan when the map is unchanged, so Workbench edits that do not touch
  former ids cost nothing.
- When `MapSides` changed the game: one Info diagnostic (`sides`) `Former side ids in the game now read as the rules pack's sides:
  {list}` (`"{old}" → {Name}` per translated id, joined by `, `, the side's name or its id when the name is blank); and, for each side the
  translated player list gives to more than one player, a Warning `{Side} is now held by more than one player; change it in Game →
  Players…` (DECISION "several → seats get all": every player who held one of the side's former ids now holds the side; the Players dialog
  asks for a fix when next opened, as `Players.Validate` already says `{Side} is held by more than one player.`).
- Seats: online seats follow the game's player list at hosting start (`HostPlayer`), and a guest seat's sides go through `SideFacts.Held`
  (which translates former ids) wherever they are used; `HostSession.OnRulesApplied` also translates every seat's `Sides` with
  `SideFacts.Canonical` (deduplicated) before it schedules snapshots, so `SyncPlayers` never writes a former id back.
  `OnlineLobbyModel.SuggestedHostSides` translates the save's first player's sides with `SideFacts.Canonical` before it keeps the pack's ids.

### #2 Alliances: the held set (Render; one function in Rules)
A player's held set is `SideFacts.Held(pack, sides of the player)` everywhere a player's sides become a held set:
1. `ModuleSession.SetViewSides(sides)` keeps the given sides as new `ModuleSession.PlayerSides : IReadOnlySet<string>?` (the seat's or
   hot-seat player's own sides) and sets `ViewSides = SideFacts.Held(Rules, PlayerSides)`; `Commit`, `UpdateRules` and `LoadGuestRules`
   recompute `ViewSides` from `PlayerSides` (a new alliance applies at once). This covers hot-seat (`HotSeat`), the host's own view
   (`HostSession.SyncPlayers`), turn files (`TurnUiModel.ViewSides`, `EndTurnReplay`) and every caller of `ViewSides`, `CanAct`,
   `CanSee`, `CanSeeMap`, `View`, `ActorSide` and `CanEditLedger`.
2. `ModuleSession.ApplySnapshot(filtered, sides)` sets `GuestSides = SideFacts.Held(Rules, sides)` (kept raw in a private field and
   recomputed by `LoadGuestRules`). The snapshot's own `ViewSides` already is the host's held set.
3. `IntentExecutor.Execute(playerId, sides, intent, name)`: `var held = SideFacts.Held(s.Rules, sides ?? empty)` before anything else, so
   every host check of a guest intent (`PieceAccess`, `ActingSideHeld`, `GuestRule`, `CanEditLedgerFor`, `PhaseGate`, ask runs, sealed
   runs, schedule rows) uses the alliance.
4. `HostSession.SendSnapshot`: `PlayerView.For(full, pack, model, SideFacts.Held(pack, seat.Sides))`. `HostSession.SyncPlayers` compares
   `_s.PlayerSides` (not `ViewSides`) with the host seat's sides before calling `SetViewSides`.
5. `PackHarness.ViewFor(sideIds)`: `PlayerView.For(..., SideFacts.Held(pack, sideIds))` (a pack test sees what a seated player sees).

No other code forms a held set from a player's sides (grep check in the plan). Every rule downstream reads the held set it is given and
never calls `SideFacts.Held` itself.

**What allies get (DECISION default: allies share private information, act on each other's units and in an ally's phase):**
- **See**: every value, line, roll, ask run, checklist, map and alert an ally sees (they hold each other's sides for every information
  rule: `HiddenValues`, `PlayerView`, `FormulaMasking`, alerts by viewer sides, private dice, stamped tick and ask lines, map owners).
- **Act**: move and run formulas on an ally's counters (`PieceAccess.CanAct`), change an ally's holder values by hand
  (`CanEditLedgerFor`), act, tick, acknowledge and press Next Phase in an ally's phase (`Sequencer.MayAct`, `PhaseGate`, the guest
  `mayPress` test), answer an ally's ask run.
- **Not changed** (decided): ownership (`Ownership`, `side()`, `phaseside()`, unit filters), seats and `Players.Validate` (each side is
  held by exactly one player), `MoveEntry.FiresFor` (an ally's move is a move of another side), victory winners, the hot-seat handover
  (it still goes to the player holding the phase's side; an ally at the screen may act meanwhile), `ModuleSession.ActorSideFor` (in an
  ally's phase a run is stamped with the ally's acting side; both allies see it, nobody else does).
- **Sealed answers (B7) — decided: allies answer separately.** A sealed entry still queues one run per side; each run is answered once,
  by any player whose held set has that side (so a player may answer for an ally, as an ally may act for it), and `askside()` is that
  run's side. Allies see each other's answers before release (they share private information); other sides never do.
- **Reveal (B7)** and **game over** are unchanged.

### Validator (side messages prefixed `Side "{name}": `, phase messages `Phase "{name}": `)
- error `Side "{name}": former id "{f}" is the id of side "{other}"` (also when it is the side's own id)
- error `Side "{name}": former id "{f}" is also a former id of side "{other}"` (reported on the later side in pack order)
- warning `Side "{name}": alliance "{a}" has no other side`
- warning `Every side is in alliance "{a}", so nothing is hidden from any player` (pack subject; two or more sides, all with one alliance)
- warning `Phase "{name}": reacting side "{s}" is an ally of the phase's side, so it already acts in this phase` (`{s}` the side's name)
- B7's two filter warnings (`its choices filter reads values another side may not see…`, `its piece filter reads values another side may not
  see…`) fire only when `SideFacts.Groups(pack) >= 2` (was: two or more sides).
- The existing side-reference checks are unchanged; after `Normalise` a loaded pack never references a former id.

### Workbench (Avalonia Desktop; the WinForms App builds only)
Phases tab, Sides box, under `Units of this side:`:
- `Alliance:` text box; tooltip `Sides with the same alliance see what each other sees and may act on each other's units and in each
  other's phases. Blank = no alliance.` Commits on flush through `PhaseEdit.SetSideAlliance(pack, id, text)` (blank → null, trimmed).
- `Former ids:` text box (comma-separated); tooltip `Ids this side had in older versions of the pack, separated by commas. Saved games that
  use them read as this side.` Commits through `PhaseEdit.SetSideFormerIds(pack, id, text)` (split on `,`, trimmed, blanks and duplicates
  dropped, none → null).
- Button `Merge into…` next to `Remove`: asks `Name of the side to merge "{from}" into:` (`AskTextAsync`, title `Merge side`), then
  confirms `Merge "{from}" into "{into}"? Its phases, holders, maps and alerts move to "{into}", and saved games that used it read as
  "{into}".` (caption `Merge side`), then `PhaseEdit.MergeSide(pack, fromId, intoId)`: the target's `FormerIds` gains the merged side's id
  and its former ids; the target's unit filter becomes `({into filter}) or ({from filter})` when both are non-blank, else blank (a blank
  filter already matched every unit); the merged side is removed; `SideFacts.MapReferences` rewrites every reference. Errors (shown with
  the existing warning box): `No side is named "{n}"`, `A side cannot be merged into itself`.
- The Problems list shows the new validator texts (through `WorkbenchRules.Issues`).

### Wiki (invented Riverland examples only)
- Making-a-Rules-Pack: `## Teams: renamed sides and alliances` with `### Former side ids` (side `Northern League`, id `north`, Former ids
  `league`: an old save whose players held `league` opens with the player holding Northern League; `Merge into…` for folding the side
  `East Shore` into `Southern Compact`), `### Alliances` (`Northern League` and `Hill Clans` in alliance `Upriver Pact`: League players see
  the Clans' Private `Supply`, move Clan barges, act in the Clans' phase; the Southern Compact sees neither), `### What the Problems list
  says for teams` (every new validator text).
- Window-Rules-Workbench: the Sides box rows (`Alliance:`, `Former ids:`, `Merge into…`).
- Playing-With-Others: `### Allies` (hot-seat, online and turn files; seats unchanged; sealed answers per side; a received turn is not
  translated, so both players open their saves with the new pack first).
- Window-Phases: one paragraph: an ally may act and press Next Phase in an ally's phase.

## Public API changes
All additive: `SideDef.FormerIds`, `SideDef.AllianceId` (init properties); `SideFacts`; `FormerSides`; `GameSession.MapSides`;
`ModuleSession.PlayerSides`; `PhaseEdit.SetSideAlliance`, `SetSideFormerIds`, `MergeSide`. Behavioural: `ModuleSession.ViewSides`,
`GuestSides`, a guest's snapshot `ViewSides` and the sets the host checks a guest's intent with now include allies;
`PackHarness.ViewFor` includes allies; a loaded game (and an open game when the pack changes) has former ids translated.

## Non-goals
An expression function for alliances (`allied()`), shared victory, alliances that change during a game, alliance-aware OnMove
(`FiresFor`), seats shared by allies, translating received turn files, re-rendering old log text that names a former id, the WinForms
App's new editors.

## Anti-goals
A player of a side outside the alliance seeing or acting on anything more than before; an ally rule anywhere but `SideFacts.Held`;
a former id surviving in a loaded game's state, history or seats; a save changing on disk without the user saving; old packs or saves
changing bytes; a pack without alliances or former ids paying a copy, a scan or a replay.

## Approaches
Picked: widen the held set once where a player's sides enter the rules (five call sites, one function), so every information and acting
rule follows without change; translate former ids once at load, state and history together (history by mapped replay, so a later save
replays to its snapshot).
Rejected: an ally test inside each rule (`HiddenValues`, `PieceAccess`, `MayAct`, alerts, dice — the scattering B7 removed); resolving
former ids at every read (every comparison of a side id would need the pack); translating only the snapshot (undo and the next load's
replay check would bring the old ids back or rebase the save); seats holding allies' sides (it would change `Players.Validate`, free
seats and the handover).

## Left to the builder
Test layout; the shared invented fixture (three sides `north` Northern League, `south` Southern Compact, `hills` Hill Clans; alliance
`Upriver Pact` for `north` and `hills`; depots, a hex board); exact private helper names; wiki prose around the exact strings.
