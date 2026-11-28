# Phase entries reach allies' counters (batch B10, alliance scope) — design (short)

Date: 2026-11-28. Branch `b10-alliance-scope` (from master `75526ef`, B9 merged). Requirement approved by the user on 2026-11-28. Items
the requirement left open are marked "decided".

## Intent

B8 lets allies see and act for each other, but the phase automation still reaches only the one side a phase names. A pack that splits a
team into several allied sides (invented: the `Northern Pact` of `north-land` Northern Foot, `north-supply` Northern Wagons and `north-air`
Northern Balloons, against `south` Southern Compact) gets per-unit start/end entries and phase warnings that skip the wagons in the foot's
phase, OnMove **Acting side** rows that ignore a balloon dragged in the foot's phase, and **Other side** rows that treat the wagons as the
enemy. B10 makes a phase's per-unit scope the acting side's alliance.

## Principles (decided)

1. **No-alliance packs are byte-identical in behaviour.** When the phase's side has no ally, every function takes today's exact path: the
   same compiled filter, the same units in the same order, no side map built (`Ownership.SideMapBuilds` unchanged). No pack field, no
   format change (`CurrentFormat` 4, pack bytes and SHA-256 unchanged), no save, command or intent member.
2. **Ownership still decides who a counter belongs to** (B8 principle 3 is kept): B10 widens what a phase's entries *cover*, never who
   owns a counter. `side()`, `phaseside()`, unit filters, stamps, seats, the hot-seat handover and `MayAct` are unchanged.
3. **One scope function.** Every "units of the phase" question goes through one new predicate (`Sequencer.Covers`); no call site reads
   `AllianceId` or forms its own ally set.

## The scope rule (decided)

`SideFacts.Allies(pack, sideId)` (new, Rules): the side and every other side whose alliance equals it (trimmed, case-insensitive, as
`SideFacts.Held` compares), as an ordinal set; **null** when the side is unknown, has no alliance, or no other side shares it (a lone
alliance, which the validator already warns about, therefore costs nothing and changes nothing).

A piece is **covered** by phase P (acting side A) when:
- A is null (phase without a known side): every piece (as today);
- `Allies(pack, A)` is null: A's unit filter matches it (as today, the exact compiled filter, with the model);
- else: A's unit filter matches it, **or** its owner `Ownership.PieceSides(state, pack)(p)` (first matching side filter) is in
  `Allies(pack, A)`.

**Why this and not the other two readings (decided).**
- *Union of the allied sides' filters* would let the foot's phase write a counter that the wagons' filter matches but that a side earlier
  in pack order (an enemy) owns. B8 decides acting rights by first-match ownership (`PieceAccess.CanAct` over `Ownership.PieceSides`): a
  player of the alliance could not touch that counter by hand, so the automation must not either.
- *Pure first-match ownership* would drop a counter that A's own filter matches but an earlier side owns: a pack with alliances and
  overlapping filters would lose units its own side's entries reach today.
- The rule above keeps A's own reach exactly as today (a superset, never fewer units) and adds allies' counters by the same ownership
  B8 uses for acting rights. A counter owned by A is always matched by A's filter, so the ally term adds only counters owned by another
  member of the alliance.

**OnMove** (`MoveEntry.FiresFor`): **Acting side** = the moved pieces covered; **Other side** = the moved pieces not covered (pieces owned
by sides outside the alliance, plus side-less pieces, which stay "other" as today); **Any side** = every moved piece (unchanged). Acting
and Other still never overlap and together are Any. Runs for Other/Any stay grouped by owner and stamped for the owner (`AsOwner`,
unchanged); Acting runs keep the phase's stamp (decided: allies share what the stamp hides, B8, so stamping by owner gains nothing).
This supersedes B8's non-goal "alliance-aware OnMove (`FiresFor`)" and its wiki sentence "an On move row set to **Acting side** does
not fire for it".

## API (additive only)

- Rules `SideFacts.Allies(RulesPack? pack, string? sideId) : IReadOnlySet<string>?`.
- Core `Sequencer.Covers(RulesPack pack, GameState s, PhaseDef phase, ModuleModel? model = null, Func<PieceInstance, string?>? owners = null)
  : Func<PieceInstance, bool>` — the scope rule; builds A's filter once and, only when `Allies` is non-null and `owners` is null, one side
  map (`Ownership.PieceSides`) per call, lazily on the first piece A's filter does not match.
- `Sequencer.UnitsFor` and `Sequencer.MovedUnitsFor` (signatures unchanged) use `Covers`; new overload
  `MovedUnitsFor(RulesPack, GameState, PhaseDef, FormulaDef?, MoveEntry, ModuleModel?, Func<PieceInstance, string?>? owners)` so one
  move gesture shares one side map across its OnMove entries.

## Performance (decided)

No alliance: zero side maps, the old code path. With alliances: one side map per entry selection (`UnitsFor`, i.e. one per start/end entry
and one per `Decisions.Warnings`), built only when needed, and one per move gesture (`PhaseController.OnMoveCore` builds its existing
lazy `owner` before the loop when `Allies` is non-null and passes it). The per-unit runs keep using the B2 #31 formula pass unchanged; the
selection happens before the pass opens, as today, so the pass's side map is not shared with it (sharing would need the pass to open
earlier — not worth it at one map per entry).

## Inventory: every place scoped by "the phase's side" (decided)

| Place | B10 |
|---|---|
| `PhaseController.RunEntries` (OnStart/OnEnd ForEach units, all difficulties: Fully automatic applies, Semi-automatic/Hardcore queue the unit list) via `UnitsFor` | **Changed** (through `UnitsFor`) |
| `PhaseController.OnMoveCore` via `MovedUnitsFor` | **Changed**; shares one side map |
| `Decisions.Warnings` (per-unit phase warnings `{unit}`) via `UnitsFor`, also from `Alerts`, `BatchModel` and the phase panel (`PhaseController.View`, on the viewer's `seen` state) | **Changed** (same "units of the phase"). On a player view the scope is judged on that view, as A's filter is today, so a viewer learns nothing its view lacks |
| `PhaseController.BadFilter` (parses the phase side's filter) | Left: an ally's bad filter owns nothing, as `Ownership` treats it everywhere; the validator reports bad side filters |
| Start-entry cursor (`StartCursor`, B2 #32) | Left: indexes entries, not units |
| Ask runs (`AskSideId`), sealed runs (B7) | Left: a phase without a side; fixed side / every side |
| Schedule rows (`ScheduleDue`) | Left: row piece filter plus `PieceAccess.CanAct` on the held set (B8 already includes allies) |
| B9 holder pickers / draws (`HolderChoices.For`, Draw pool) | Left: judged on `RunRequest.PickSides` (held set) and the pool filter, not the phase side |
| Alerts by phase (`Alerts` legacy solo overload: a side alert only in its own side's phase) | Left: an alert's side says who sees it, not phase automation; the viewer overload already shows side alerts in every phase |
| `phaseside()`, stamps (`ActorSideFor`, `_triggerSide`), handover (`HotSeat`), `MayAct`/reactors, checklists shown | Left: B8 principle 3 / naming and acting rights, not per-unit scope |
| Same-hex roll groups (B4) | Left: groups by owning side; an ally's counters in the hex are their own group, as an enemy's are today |
| `PlayerView` pending-run units | Left: filtered by the viewer's held set (allies already included) |
| Validator | No text becomes wrong: B8's `reacting side "{s}" is an ally of the phase's side, so it already acts in this phase` stays. No new message (decided) |
| Workbench | Desktop `PhasesTab.AllianceTip` gains the phase-entry reach (text in the constraints file). No preview counts units; WinForms App builds only |
| Wiki | Making-a-Rules-Pack `### Alliances` (and `### Moves by the other side`), Window-Phases `### Moves by the other side` and `### Phases of an ally`, Window-Rules-Workbench `Alliance:` row |

## No opt-in flag (decided)

An alliance already is the opt-in: only a pack that lists one changes, and B8 (alliances) was merged one day before B10. A flag would be
a new pack field and validator/Workbench/wiki surface for a choice almost no author wants (an ally's counters skipped by the team's own
phase automation). Rejected. A pack that wants the old reach for one entry filters it: `side() == "Northern Foot"` in the entry's
**Filter (units)**.

## Tests (Core unless named)

Invented 4-side fixture: pack order `north-land` Northern Foot (`startsWith(name(), "NF")`), `south` Southern Compact
(`startsWith(name(), "SC") or contains(name(), "taken")`), `north-supply` Northern Wagons (`startsWith(name(), "NW")`), `north-air`
Northern Balloons (`startsWith(name(), "NB")`); the three `north-*` in alliance `Northern Pact`. Pieces `NF 1`, `NF 2`, `NW 1`,
`NW 2 taken` (owned by `south`: first match), `NB 1`, `SC 1`, `Loose crate` (no side). A no-alliance regression pins the old units and order
on the same pieces with `AllianceId` cleared, and `Ownership.SideMapBuilds == 0`; an OnMove Other test; a 1200-piece perf test
(best of 5, as `AlertEngineTests.Perf_*`). Render: Fully automatic start entry, OnMove hot-seat and online (host) runs.

## Non-goals

Changing ownership, stamps, seats or handover; alliance-aware `phaseside()`; an opt-in flag; sharing the B2 pass side map with unit
selection; the WinForms App's tooltip.

## Addendum (Task 3), decided 2026-11-28 by the controller

- A met victory condition whose side has allies records the whole alliance as winners (condition side first, then allies in pack order); a blank message defaults to `{alliance} wins`. Without alliances: unchanged (one winner, `{side name} wins`).
- `Players.MaxPlayers` 6 → 12, so a pack with many allied sides can seat one player per side. Protocol unchanged.
