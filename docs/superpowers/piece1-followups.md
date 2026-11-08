# Piece 1 — deferred follow-ups

Minor findings deferred by task reviews (none block use). Collected from the piece-1 execution ledger on 2026-11-06.

- Task 1: minor (deferred): Decode_RoundTrips vacuous early-return for 'ends\' row — make encode-only test
- Task 1: minor (deferred): NextStringArray test doesn't assert next token readable after array
- Task 1: minor (deferred): NU1701 NoWarn in App (SkiaSharp.Views.WindowsForms) — confirm intentional
- Task 1: minor (deferred): template Form1.* boilerplate remains (Task 11 replaces)
- Task 2: minor (deferred): FixtureModule.Build detects MinimalBuildFile by string compare (brittle) — explicit flag better
- Task 2: minor (deferred): SaveFileReader keeps leading UTF-8 BOM if present
- Task 3: minor (deferred): DiagnosticLog.Added subscriber exception propagates to logging caller — wrap invoke
- Task 3: minor (deferred): OpenEntry decompresses under single lock (tiling contention)
- Task 3: minor (deferred): coverage — Dispose releases handle, concurrent OpenEntry, non-zip → InvalidDataException, malformed XML → XmlException
- Task 3: minor (deferred): ModuleNode public ctor takes parent without registering child
- Task 4: minor (deferred): keystroke/NamedKeyStroke fields all skipped (not kept raw) — needed only if hotkey dispatch added
- Task 4: minor (deferred): PlaceMarker offsets & LayerDef.ResetLevel parsed as int though Java FormattedString ($prop$ → 0)
- Task 4: minor (deferred): Immobilized neverMove edge divergence; old `nonRect` id unsupported; RgbColor drops alpha
- Task 4: minor (deferred): thin coverage — Labeler field-order assert, 1-angle rotator, hide/footprint/markmoved/clone/delete/replace values; int-parse idiom repeated 5x
- Task 5: minor (deferred): SquareGrid snapscale not ported (Hex is)
- Task 5: minor (deferred): hex numbering tests round-trip only (no known-value/descending/stagger=false); edge/corner tests assert NotEqual only
- Task 5: minor (deferred): OverlayLines unbounded loop for tiny dx/dy; ZonedGrid unused container param; HexGrid default-dx edge
- Task 6: minor (deferred): image header read copies whole entry (bounded prefix better for huge PNGs); SVG huge width unclamped; ImageSize garbage JPEG/GIF/SVG tests missing; weak count assert; root .txt + HelpFile duplicate doc; legacy `edge` attr order
- Task 7: minor (deferred): NativeSave null snapshot/log → NRE not InvalidDataException; determinism test lacks StackOnto; OrderedDictionaryConverter dup keys overwrite silently
- Task 7: minor (deferred): literal tab chars in test sources (use '\t'); DecodeState fallback list shorter than trait count
- Task 8: minor (deferred): SetupStack with absent owning board placed at map edge (VASSAL skips it); placeNonStackingSeparately not ported; untested branches (dup ids, piece in two stacks, unresolvable grid loc, unknown owning board, missing stack member, board-picker differs); ExpandTypeAndState logs no-BasicPiece warning per piece
- Task 9: minor (deferred, FLAG FOR FINAL: violates never-crash constraint): ShapeParser legacy int.Parse OverflowException uncaught → HitTester crash on huge ints
- Task 9: minor (deferred): BoardTiler Resize/ExtractSubset/IO failures propagate (no placeholder/diag); Sanitize collisions "A.B"/"A_B"; marker doesn't verify tiles exist
- Task 9: minor (deferred): ImageSource/MapRenderer/PieceRenderer not IDisposable; PieceRenderer._faces plain Dictionary; shared SKPath across threads; Reload missing diag; multi-layer order/DrawUnderneath untested; LRU oversize entry undocumented
- Task 10: minor (deferred): SendTo not disabled when already at destination; PieceFactory states[^1] unguarded + Build closures outside try/catch; Replace puts replacement on top not source index; tests not RED-first; Hideable/Obscurable player "local"
- Task 11: minor (deferred): Repopulate duplicate rows race; ExitCode=1 sticky on recoverable ThreadException; Guard catches generic InvalidOperationException; ConfirmDiscard before Open; DPI/font set twice; thin session tests (Dispose handle, missing setup entry); chooser double-click on empty space
- Task 12: minor (deferred): ghost lingers on early-return drop; Execute in OnDragDrop/OnMouseDoubleClick unguarded (use Run()); Ctrl+click deselect keeps _downPiece; SKTypeface not disposed per repaint; _cts disposed before continuation race; Map-menu Show Grid not synced with MainForm; ForMap()[0] fragile; grid clip untested; MapCanvas.cs 471 lines
- Task 12: minor (deferred): ghost target stale if state changes mid-drag; DrawGhost index clamp if Offsets empty
- Task 13: minor (deferred): doc cache name collisions (a/b.pdf vs a_b.pdf); text ReadAllText outside try; ChartWindow image lock per paint; acceptance test vacuous on empty module; only first map tiled
- Task 13: minor (deferred): 32-bit doc hash; concurrent extraction shares .tmp path
- Change 1: minor (deferred): sprite up to 2048px each (memory with big selections), label clip; Preparing msg hidden during drag; MouseUp outside without move; tests: cross-map formation, PlacedExcept, HiddenPieces asserts bytes differ
- Change 1: minor (deferred): drop during ClearIncoming window silently no-ops; App-side guards untested
- Change 2: minor (deferred): tab strip fixed px sizes not DPI-scaled (high-DPI); Ctrl+W swallowed with no active child; WebView2 focus bypasses shortcuts; TabsCloseRequested unguarded; _menuTab index; hover state after close; redundant lines

## Rulings made during execution

- Ruling R1: ModuleModelBuilder reads board image dimensions itself from the archive (PNG IHDR / JPEG SOF / GIF header; SVG width/height or viewBox) so BoardDef is complete and offsets are computed at build time — why: records are immutable and layout needs widths — cost if wrong: small, header parsers are self-contained.
- Ruling R2: BoardTiler exposes `internal int DecodeCount` (InternalsVisibleTo Quartermaster.Render.Tests); cache-hit test asserts it stays 0 on second run — why: plan's hook named wrong class — cost if wrong: trivial.
- Ruling R3: Task 12 adds command-line args `Quartermaster.App.exe [<vmod>] [--setup <index>|--new] [--smoke <seconds>]`; `--smoke` opens module+setup+main map, waits N s, exits 0 (non-zero on unhandled exception). Implementer verifies via smoke run on a fixture module; GUI behaviours re-checked by user at the end — why: subagents can't drive GUI — cost if wrong: GUI bugs surface later in user check.
- Ruling R4: Task 13 adds an env-gated test (`QM_REAL_VMOD` path; skipped when unset) in Render.Tests asserting structural counts from acceptance items 1,2,6(page count >0),7,8 programmatically; never commits module data; GUI items (3,4,5) listed for the user's manual check — why: same as R3 — cost if wrong: manual items unverified until user check.
- Ruling R5: Ported VASSAL code keeps an LGPL-2.1 attribution header ("Ported from VASSAL <file>, LGPL-2.1") and repo gets THIRD-PARTY-NOTICES.md (added in Task 1) — why: license compliance for private derivative — cost if wrong: none.
- Ruling R6: Game pieces carry prototype-EXPANDED type strings (VASSAL PieceCloner expands prototypes when a piece is created from a slot; module saves contain no `prototype` traits — verified in inventory). Creating a piece from a slot (NewGameBuilder, PlaceMarker, Replace, Clone-of-slot) must expand prototypes into the type string and give expanded traits default state; imported saves are already expanded. Task 6/8/10 dispatches carry this; Task 4's ExpandedPiece stays for slot-level use — why: otherwise prototype-trait state (layers, labels) is lost — cost if wrong: re-work in importer/actions.
- Ruling R7: Task 6 must give grids the real board size (from R1 image dims) so descending numbering works — via whatever hook Task 5 exposes (or add a minimal one in Task 6) — cost if wrong: wrong location names on descending-numbered boards.
- Ruling R8: NavPath fallback name → entryName → text → short class (implementer's extra `text` step) accepted — PieceWindow labels live in `text` — cost if wrong: cosmetic folder names.
- Ruling R9: GameState.Stacks (and Pieces) use System.Collections.Generic.OrderedDictionary (not Dictionary) so insertion order = draw order deterministically after removals; a stack created by a move goes to the end (drawn on top, like VASSAL) — cost if wrong: draw-order glitches only.
- Ruling R10: Render/actions read game pieces via PieceTypeParser.Parse(piece.TypeString) (cached per type string) with TraitStates[i] aligned 1:1 (types are already expanded per R6); ModuleModel.Expander only for slots. Trait state "" = trait default. Trait state parsing (layer level/active, rotation angle, hidden/obscured flags, label text, dynamic property value, property-sheet values) is ported from each Java mySetState into Vassal/Traits as static helpers so Render (T9) and Actions (T10) share them — cost if wrong: duplicated state parsing.
- Ruling R11: Task 12 extends smoke: opens main map window (OpenMainMap), waits for tiling, renders; adds `--screenshot <png path>` which, in smoke mode just before exit, saves a capture of the main form (DrawToBitmap or Skia surface snapshot) so the controller can visually verify UI headlessly — cost if wrong: small extra code.
- Ruling R12: piece-1 multi-piece drag drops all dragged pieces into ONE stack at the resolved point (MovePieces has one Pos); drop point = cursor + primary grab offset (piece centre under cursor-relative grab), then DropResolver; ghost drawn at the resolved (snapped) position as a stack — cost if wrong: VASSAL keeps relative offsets for multi-drag; revisit later.
- Ruling R13: Task 13 real-module acceptance test is GENERIC (works for any module; asserts invariants: model builds, every setup imports with CommandsSkipped==0, no Error diagnostics, every map/chart/doc resolvable, native save round-trip) and prints counts to test output — no module-specific names/strings/counts hardcoded in committed code; commit messages and reports summarise diagnostics by category+count only (no module file names or text). Smoke screenshot of the real module saved to scratchpad only (never committed) — cost if wrong: weaker assertions on CNA specifics.
- Ruling R14 (amends R6): prototype-derived traits take their initial state from the PrototypeDefinition's own state (split per trait, BasicPiece excluded, nested prototypes recursed), as VASSAL PrototypeDefinition.getPiece + PieceCloner do; "" only when the definition has none — cost if wrong: small, matches VASSAL.
- Ruling R15: tile cache folder keyed by image (sanitized image name + short hash of image name), lookups by image — pyramid depends only on image.
- Ruling R16 (supersedes R12, user-directed): multi-piece drag keeps relative offsets (VASSAL PieceMover): primary grabbed piece's drop point = cursor + grab offset → snapped via DropResolver; delta = snapped − primary original pos; every other dragged piece/stack moves by the same delta (no individual snap), stacking only for the primary; one MovePieces per source stack group. Ghost follows cursor unsnapped at mouse rate + faint snap/landing outline; in-window drag uses mouse capture (not OLE DoDragDrop) with cached map snapshot + cached ghost image; OLE drag only when leaving the window.

## Piece 2

Deferred minors and rulings from the piece-2 execution ledger (generic engine notes), collected 2026-11-06.

### Deferred minors

- Change 3: minor (deferred): HashFile sync on UI thread at open; titles captured once at Register (resolved by P2-R11)
- Change 3: minor (deferred): legacy layout Dpi==0 falls back to host DPI (should be no-scale); TitleBelowBottom test weak/dead clamp
- Task 1: minor (deferred): SafeName weak (use whitelist); BuildFiles before .tmp create; no size cap on entries; dup zip entry names; enum ints allowed; test gaps (non-zip, unsafe id, dup tables/formulas)
- Task 1: minor (deferred): Load doesn't apply SafeName whitelist / case-insensitive dupes (load-ok, save-fails asymmetry); format "1"/1.0 message cosmetic
- Task 2: minor (deferred): trailing backslash message; ASCII-only identifiers; recursive record ToString/equality
- Task 2: minor (deferred): height bound only for Parser.Parse output (rewritten trees unbounded)
- Task 3: minor (deferred): WorkingLine hard-cast (VarRef) lookup arg; ctx.Lookup result bypasses finite guard; TextArg null Text; TextLit quote escaping; paren/neg-zero tests missing
- Task 4: minor (deferred): CSV all-comma rows kept; U+205F/U+3000 not normalised; test gaps (inclusive bounds, NBSP keys, multi-line CSV, Bool value)
- Task 5: minor (deferred): parse error → spurious "unused binding" warnings; Variables recomputed in lambda; single top-level try/catch aborts all validation on one bad entity (per-entity catch better); Build catch-all hides bugs; nested sub-rows inner-first order; test gaps
- Task 6: RESOLVED by P2-R6 (was FLAG FOR FINAL): seeder ReadSheet(...).First(...) throws on short/stale sheet state; PieceTypeParser.Parse unguarded in seeder → OpenRules could throw on a malformed piece
- Task 6: minor (deferred): Describe "manual: " trailing space for null note, "−0" for tiny deltas; LedgerKey.TryParse("p:") empty id
- Task 7: minor (deferred): Fail drops Variables on error trace; Location test single-board only; test gaps (Set/Add working, Holder/Table-with-vars, has())
- Task 8: minor (deferred): Replace stamps dirty pack hash; AutoOpenRemembered extra Remember write
- Task 9: minor (deferred): partial Apply on mid-exception; re-roll button focus loss; CreatePreviewPanel orphan Form disposal (RESOLVED in Task 12); ResultBox font
- Task 9: minor (deferred, FLAG FOR FINAL): OnUi queued lambdas guard (RESOLVED by P2-R11); rebuilt view after RulesChanged DPI-scaled (RESOLVED); dedupe ignores requesting window; _formulaGroup unused
- Task 10: minor (deferred): CommitManual no scope check; Reload RowCount reset drops current cell; PropertySheetDialog 3rd ColumnStyle; ASCII vs U+2212 minus inconsistency
- Task 10: minor (deferred): ParseNumber de-DE "1.234" parses as 1.234 (consistent with display); MapWindow inline format could use LedgerTable.FormatNumber
- Task 11: minor (deferred): reused RuleRef label shown for new table; malformed OneD multi-column shows several "Value" columns
- Task 12: minor (deferred): AppliesTo trim mismatch re-commit; picker shows deleted piece; Input min>max not flagged in grid; Dispose doesn't flush pending text
- Task 12: minor (deferred): _declined never cleared; _dirty off-thread branch effectively dead (RulesChanged is UI-thread); no prompt on window close

### Rulings

- Ruling P2-R1: implement as pop-out (float) documents: MDI child ↔ top-level Form (not owned by MainForm, ShowInTaskbar true), via tab drag-out, tab context menu, Window menu; dock back via window system menu / Window menu / dropping on tab strip; per-module remembered docked/floating state + bounds + maximized, clamped to Screen.AllScreens; Skia surfaces re-render on DpiChanged — cost if wrong: UI rework only.
- Ruling P2-R2: promote review minors #4 (_lastDpi reset on handle recreate skips zoom rescale) and #5 (pop-out on any release outside strip, no min distance) to Important — user's explicit multi-monitor/mixed-DPI use case; #6 (restore ShowIcon/KeyPreview/MinimizeBox on dock) and #7 (strip BOMs) folded in as cheap — cost if wrong: small extra work.
- Ruling P2-R3: in Matches, numeric key kinds coerce a Text value via Normalise+TryNum; Number keys also match Text equal to key text (OrdinalIgnoreCase) — Task 7 needs no own coercion. Folded minors: Ordinal StartsWith.
- Ruling P2-R4: VariableBinding.Property and FormulaOutput.Property hold PropertyDef ids; validator/layout resolve by id; renames safe — spec §14a.4.
- Ruling P2-R5: roll keys = variable name for Roll bindings, "dice#{i}" (0-based call order in evaluation) for inline dice(); Roll consults FixedRolls[key] first; FormulaTrace gains IReadOnlyDictionary<string, IReadOnlyList<int>> RollsByKey (feed back into RunRequest.FixedRolls by Task 9); DiceEntry.Purpose = "{formula}: {key}".
- Ruling P2-R6: Task 8 (OpenRules runs the seeder on the real module) also hardens LedgerSeeder per Task 6 FLAG: per-piece try/catch around Parse/ReadSheet, FirstOrDefault instead of First; failures logged once (Warning/rules) and skipped — cost if wrong: none.
- Ruling P2-R7: SaveNative stamps Game.RulesSha256 = current pack hash (when a pack is loaded) before writing; UpdateRules re-runs (idempotent) seeding so newly added SheetField properties seed immediately. Folded: mismatch warning via AddOnce after Commit; explicit null-model guard in BuildSeeds.
- Ruling P2-R8: FormulaDialog modeless (Show(owner)), dedupe per (formula, piece), re-run on Game.Changed, rebuild/close on RulesChanged (close if formula deleted), close if piece removed. Folded minors: clear Roll overrides on PhysicalDice toggle; DPI scaling (AutoScaleMode.Dpi or DeviceDpi-scaled sizes) — user runs multi-monitor.
- Ruling P2-R9: all UI number display invariant 0.##; Describe fixed at source (single parens). Folded: unsubscribe in OnFormClosed/Dispose; ManagedSheetValue honours AppliesTo; skip manual delta when before==after.
- Ruling P2-R10: (I1) timer flush commits text but keeps stale bindings with a visible "unused" marker; add a "Remove unused bindings" action; final flush (deferred via BeginInvoke, not inside Leave) asks once. (I2/I3) when !InvokeRequired act directly regardless of handle; tabs reload on VisibleChanged when marked dirty; SetFormula rebuilds directly. (I5) Reload flushes pending text first. (I6) reject empty/duplicate (case-insensitive) names on rename/edit for properties, holders, tables, formulas with a message.
- Ruling P2-R11 (final-review fix wave): (C1) FormulaDialog Apply executes then Close(); Cancel.Click closes (no-op embedded); smoke asserts Apply closes and records exactly the previewed entries; OnUi guards IsDisposed/_closing. (I1) FormulaDialogModel merges dice pins (only Reroll and the PhysicalDice toggle remove them); FormulaRunner.Fail carries RollsByKey already produced. (I2) workbench New/Open prompt buttons go through Guard. (I3) RulesValidator checks property names referenced by AppliesTo (variables and has()) — unknown is an Error. (M1) LedgerSeeder exits early when no Pieces/Both property has a SheetField and uses PieceInfo.TypeOf cache. (M12) WindowManager follows TextChanged of registered forms.

## Piece 2b (UI tools) — deferred minors and rulings

Generic engine notes only; all items were reviewed and deferred by the controller. Items marked "fixed in Task 11" were closed by the final task.

### Rulings

- Ruling P2b-R1: CommandRegistry (ids, defaults, overrides, conflicts, reserved) lives in Render with string shortcuts; `ShortcutParser.TryParse` lives in App.
- Ruling P2b-R2: "last moved" reads GameSession.Log (the command list), via `MapViewTools.LastMovedIds`.
- Ruling P2b-R3: WindowManager must not record the floating " — Quartermaster" suffix as the base title.
- Ruling P2b-R4/R5: building a game from a setup or import must not produce per-piece log entries; the log is cleared and one system entry is added (`Setup "{name}" loaded: N pieces` / `New game: N pieces`). LoadNative leaves the saved log untouched.
- Ruling P2b-R6: SnapOnDrop and ConfirmDelete are wired; settings Commit merges onto the store's current values; Autosaver.Reset on manual save.
- Ruling P2b-R7: floating forms route shortcuts through MainForm.TryDispatchShortcut; editing and navigation keys never dispatch while a text control has focus.
- Ruling P2b-R8: an open NotesWindow is flushed into the current game before any replacement or close decision.
- Ruling P2b-R9: smoke SaveScreenshot composites a map canvas only for the screenshot target or the active MDI child; map export has a re-entrancy guard, a unique temp name (`path.<guid>.tmp`) and cancels when the owner window closes (fixed in Task 11).

### Deferred minors

- Game log / commands: RemovePiece logs before detach; no old-command-JSON back-compat test; DeepClone cost grows with the log; `Num()` NaN text; multi-piece move "from" and StackOnto "to" approximations; CentreOnPiece is silent for deleted pieces; `OnGameReplaced` Changed lambda is never removed (pre-existing).
- Log panel: virtual ListView reselect may raise SelectedIndexChanged; DockLog relies on WindowManager closed-form handling.
- Dice / turn: multi-roll physical cancel keeps earlier rolls; StepTurn clears the phase (documented choice); unguarded DropDownOpening/Refresh; Next-turn caption refreshes only on dropdown open; filter wiring untested (smoke calls dispatch directly); Action/Func<bool> overload fragility.
- Trees: splitter fraction drift from SizeChanged; TryPending leaves a stale pending id after 5 passes. The Formulas branch is now exercised by the smoke (an invented table and formula are added to the empty pack made with --new-rules) (fixed in Task 11).
- Settings: dialog is fixed size (DPI); PieceRenderer.LabelScale is shared; toolbar ids equal captions; linked-folder skip is not logged.
- Palette: thumbnail cache clears at 2000 (LRU would be better); per-thumbnail BeginInvoke flood; search text overrides the folder selection; palette pop-out not restored on reopen; seed is not undone with a placement; a stale worker may steal one pending item; first prioritisation is wrong before bounds are realised; no BeginUpdate in Apply; the scroll timer ticks while hidden.
- LOS / overview: LOS drag is stuck if the window pops out mid-drag; OverviewWindow OnTargetClosed BeginInvoke has no handle guard; overview tile image cache is never invalidated; GridFor resolution is duplicated; TryCell expects snapped input (undocumented).
- Map export: whole-map export ignores hide counters / last moved; cancel is ignored during PNG encode; RefreshLastMoved does work when the option is off; incoming drag preview while hidden. (Concurrent export collision, ".tmp" clobbering and no cancel on owner close were fixed in Task 11.)
- Inventory / notes: marker keys are recomputed each refresh; group label casing is order-dependent; no notes size cap; the Leave/timer flush can run between GameReplaced and its deferred handler.

## Piece 3 — deferred minors and rulings

Engine notes only. No game content: phase names, order, formulas and warnings are the user's to author in the Rules Workbench.

### Rulings made during execution

- Ruling P3-R1 / R2: tasks ran as parallel worktrees, each started as soon as its own dependencies were done; dependent worktrees merged their dependency branches first, and the whole branch was re-tested after each merge.
- Ruling P3-R3: repeat semantics. Moving within a block keeps the repeat count and increments it on wrap-back to the block start. Entering or leaving a block resets it to 1. (Resetting on every phase change would loop forever.)
- Ruling P3-R4 (implemented in the final review fixes): Phases-tab combo and checkbox cells commit at once; text cells commit when the edit ends or about 400 ms after the last keystroke, and validation runs at that commit. "Move" refuses only a move that would invert a block that was valid before it; blocks that are already inverted never prevent a reorder, so the user can repair them.
- Ruling P3-R5: in checked modes the typed values only gate Apply. When every checked box matches, the commands applied come from the expected (engine) run, so the ledger never sees typed text. The expected run uses the same roll overrides and pins as the actual run. Numbers compare by exact engine value (Calculate fills full-precision invariant text, not the rounded display), text compares through `TableLookup.Normalise` on both sides, and a Location compares as text.
- Ruling P3-R6: an Input without Choices accepts text when its binding has no Min or Max. With Min or Max set it stays numeric-only.
- Ruling P3-R7: "1 decision left" is singular and "N decisions left" plural; grammar wins over the spec's illustrative text.
- Ruling: Calculate-then-Apply logs as the `calc` kind (the brief's `formula` was superseded). Typed answers in the missing-input dialog count as `calculated = false`.
- Ruling: fixture module zips are built with a fixed entry timestamp, so two builds of the same content hash identically (this fixed a flaky `RulesMemory_RemembersAndAutoOpens`, which compared two builds with different zip stamps).

### Resolved spec ambiguities

- "Calculations" log kind: a new kind `calc` for engine-computed lines (fully automatic, Calculate, batch summaries, skipped runs, trigger problems). A manual Apply stays `formula`.
- Key dropdowns: offered only when the Input is the whole row or column expression or a bare `lookup` argument, and never for an axis with range keys. A computed expression has no single valid key set.
- Checking Inputs: Inputs, including those with an AutoValue, are never checked. An AutoValue is a default, not a correct answer.
- Unit-menu formulas in fully automatic show the piece-2 prefilled dialog; Hardcore and Semi-automatic open it checked. "Not opened" in the spec refers to sequencer triggers.
- Stage label is `"{Label or Stage} 2 of 3"` everywhere (the spec uses both "Stage 2 of 3" and "Stage 2/3").
- Old saves (formats 1-3) load as Semi-automatic and before turn 1, so nothing applies itself unexpectedly.
- The phase-boundary autosave honours `AutosaveMinutes = 0` as off.
- Moved without a measurable distance (across maps or boards, or no grid) counts as 0 with a log line.
- Missing inputs in fully automatic: the small dialog asks once per run and applies the values to every unit; "Per unit..." opens the batch grid.
- Pack storage: sides, phases and blocks are stored in `pack.json` in authored order, not sorted. Pack format is 2; format 1 loads as "no phases"; format above 2 is rejected with a message.
- Relocation: a mid-game relocation (the current phase was deleted) is a logged `AdvancePhase` with a `Reason`, so it replays.

### Deferred minors

- Rules model: normalising maps null checklist items to "" instead of dropping them; a negative stored Repeat is not clamped on the increment path; `UnitsFor` returns dictionary order; `Current` is null on a stale id (callers must Resolve first); an overridden Moved binding with no Source is unpinned.
- Commands / replay: the replay test lacks Tag/Subject through GameSession; `NativeSave.Validate` keeps null Units entries; `AdvancePhase` with a null To is a silent no-op.
- Tests: the block-error test uses Contains rather than an exact count; the `UnitsFor` test uses `name() ==` not `startsWith`; no test for a numeric AutoValue against Min/Max; the judgement-call cases of the message formatter are untested.
- Messages: removing `{unit}` from a warning leaves a double space mid-message.
- Phases tab: `FormulaIdByName` takes the first match on duplicate names; `GoToIssue` is a no-op for side and block issues; `RemoveSide` skips Flush.
- Smoke: `AppLog` writes to the real `%LOCALAPPDATA%\Quartermaster\app.log` even when `QUARTERMASTER_LAYOUT_ROOT` is set (logging only; layout, autosave and settings do honour the root). The smoke's Weather-style once-per-turn dialog may be partly visible behind the screenshot.
- Deferred by the spec: stepping back a phase.

### Final review fixes (piece 3)

- Difficulty switch: a change of difficulty (Settings, Game tab) closes every open formula dialog, batch grid and cheat sheet and shows "Difficulty changed — reopen from the phase panel". Pending runs stay pending and reopen in the new mode. As defence in depth the dialog and batch models re-read the current difficulty on every evaluation, so an override typed while fully automatic can never reach the ledger after a switch to a checked mode.
- Opening a pending run always uses the current difficulty: fully automatic finishes it from the auto values and asks only for missing inputs; hardcore and semi-automatic open the checked dialog or batch grid (cheat sheet in semi-automatic).
- Batch grid: Input columns that index a table with plain keys are editable dropdowns; axes with range keys stay free text. Values still go through the checked comparison.
- Starting a setup by double-clicking it in the window tree shows the same difficulty chooser as File > Load Setup (default = last used).

### Piece 3 — residual minors after the final re-review
- Silent finish of a pending run in fully automatic, and answers from the missing-values prompt, log as an untagged `formula` entry instead of `calc` "(auto)"; units that fail there show only in the status line, not the log.
- The Settings → Game hint doesn't mention that switching difficulty closes open formula windows and batch grids.
- `MainForm.OnDifficultyChanged` has no `InvokeRequired` guard (its only caller is on the UI thread today).
- Not covered by any smoke (check by hand): batch-grid key dropdowns, debounced Phases-tab commits, setup chooser on tree double-click, windows closing on a difficulty switch.

## Piece 4 (undo and step back) — rulings and deferred minors

### Rulings made during execution
- Rewinding to any log index is allowed. If the cut falls in the middle of an action, the cut-off tail becomes its own redo entry with the same label, ahead of the later actions. Folding commands (ledger seeds, "Stepped back" lines) can join an action after its checkpoint, so mid-action checkpoint indices do exist.
- Rewinding while a gesture's action scope is open throws; the UI cannot reach that state.
- Tasks that needed `GameSession` helpers (`DropTail`, `ClearRedo`) were allowed to edit it.
- The log always keeps one "Stepped back" line that covers everything on the redo stack. A second step back sums the totals; Undo after a step back re-logs one merged line; each Redo replaces it with one covering only what is still undone, and the last Redo leaves no line. A plain Undo with no step-back tail adds no line.
- The relocation that follows a rewind (when the current phase was deleted) is part of that rewind. One Undo goes back through the relocation and the action before it, and the relocation does not clear that Undo's redo. Only the relocation made by the latest rewind belongs to its tail.
- Saving the rules pack no longer clears redo; only reseeds and reconciles that change the game do.
- The step-back line's totals count everything on the redo stack, so it may also mention plainly undone actions.
- Three small UI issues from the Task 7 review were fixed in Task 8: the "Undone"/"Redone"/"Stepped back" status and the closing of formula windows now happen only when a rewind really ran; "&" in an action label is escaped in menu captions; toolbar tooltips refresh after a shortcut change.
- The MSB3277 WebView2/WindowsBase warning in the App build is a pre-existing package reference conflict, left as is.

- Final review fixes: a rules change right after a step back keeps its "Stepped back" line as permanent history (it is no longer re-counted by the next Undo or Step Back); an automatic phase relocation is labelled "phase relocation", not "Next Phase"; after loading an old save, the first checkpoint and its step-back line say "load point" instead of "game start"; a snapshot is cached every 500 commands, so a game without phases or turn changes does not replay from the start on every undo.

### Open spec gaps for the user
- A plain Ctrl+Z of an action that rolled dice, followed by a reroll, leaves no trace in the log (spec 6.4 says a plain Undo adds no line). Not changed.
- Fixed 2026-11-07: undoing an action that carries a "Stepped back" line now re-logs a line, and every new line also counts the actions and dice recorded by earlier lines inside the redo stack, so a step back followed by a rules change and an Undo or another step back keeps its dice in the log.
- Autosave keeps "the first of each turn" per module, not per game, so two games of the same module share that retention. Not changed.

### Deferred minors
- Autosaver: an empty catch around the turn provider is not reported through `onError`; the turn key uses an odd `TrimStart('0')`; no test for the pruning of a `-t{n}-2` collision twin; Open Autosave may list the twin before the original.
- Core history: Redo pops before it replays, so a partial failure would lose the entry (the reducer is pure, so the risk is low); orphan commands before the first action are swept into the first action's redo; `RewindTo` throws on a bad index, so callers keep it behind a guard; a checkpoint's time and label can come from different commands when one action holds both an `AdvancePhase` and a `SetTurn`; a mid-action cut after `AdvancePhase` loses that checkpoint on redo of the tail (only arbitrary-index callers); `ActionLabels.For` has an unused count parameter; the singular "1 dice roll" label is untested.
- Native save: the `Need` helper is duplicated; a `.tmp` file is left if the write fails.
- History controller: the preview puts display names in `PropertyId`; dropping the rewind tail shares a try block with the rewind, so an exception path can lose the line; the rewind tail lives in memory only, so after a reload a trailing "Stepped back" line counts as plain history; view-only folding commands after a rewind are dropped with the tail; the preview of a row does not subtract the tail's dice; the Step Back confirmation preview can be smaller than the logged line; later `SetStackExpanded` commands can be dropped with a step-back tail; an early return inside the Next Phase scope can leave a relocation-only "Next Phase" action; the `RulesChanged` subscription is never removed.
- App: the `Changed`/`Rewound` lambdas in `MainForm` are duplicated, and the notes window's `OnRewound` duplicates `OnGameChanged`; the shortcut-gate tests pass trivially; no automated menu-state tests (checked by the smoke screenshots and by hand).
- Smoke: `--smoke-undo` runs on a fully automatic game only; the Edit-menu and confirm-dialog pictures are written next to the `--screenshot-stepback` file with `-menu` and `-confirm` suffixes.

## Piece 5 (accessibility) — rulings and deferred minors

### Rulings made during execution
- Breakdown line order is: earlier-turns line, "Start of turn N", an "N more changes" line when history is long, then entries oldest first. This is accepted over the order in the spec example. The earlier-turns line reads "Earlier turns: net +N" without a "seeded" note, so it carries slightly less information.
- The "more changes" line shows the net amount of the hidden entries. A manual change with no note has no trailing space. The ledger and the breakdown share one source label.
- An alert limited to one side is hidden unless that side is acting, whenever phases exist (strict reading of the spec). Side alerts are therefore invisible in neutral phases such as a weather phase.
- A tooltip's first line uses the alert name when the message contains the `{unit}` token.
- The outliner does not evaluate alerts: it refreshes from the main window's `AlertsUpdated` hub (one debounced evaluation per change, shared with the alert bar). Its grid is virtual and its columns are rebuilt only when the set of outliner properties changes, so user column widths survive refreshes.
- The alert bar shows whenever a game is open (Settings permitting), also without a rules pack; its banner then reads "No phases defined — free play."
- The unit popup is never placed under the cursor (it flips left and/or up near a screen edge) and is click-through except over its ledger lines.
- `HideAlert` carries an optional `Name` (the alert's user-facing name) for its log line; older commands without it log the id.
- The tour smoke reads the saved first-run setting before opening the tour and restores it in a `finally` block, so a smoke run never changes it. The tour also closes when the session closes or the game is replaced.
- The command-line doc comment now lists the new smoke flags.

### Deferred minors
- Rules pack: a duplicate alert id also produces duplicate-name validator noise; no test of the lowercase outliner enum spelling in the file.
- Breakdown: the Where column is ragged; a deleted formula is labelled "deleted formula"; an unused model parameter; the format-5 test does not check undo, re-save or the load notice; entries from future turns count as earlier.
- Alert engine: an unused model parameter; a warning with no pieces yields no hit; swallowed errors are silent; warnings with no current phase are untested.
- Tooltips: the tip can overlap a popup's lines and can land under the cursor near a screen edge; the tip text is slightly brown on yellow; a batch grid tip covers only the first output; the " · " separator is not a shared constant; a test contains a literal tab character.
- Outliner: BySide grouping uses rest.Except(mine) per side (O(sides × pieces)); a HashSet of assigned pieces would be cheaper; the collector re-walks the pieces; arrays are shared between nodes; an empty side filter swallows the rest; the Decisions text can read "Weather roll — Weather roll" when a unit label equals the formula name (fixture data); "1 decisions left" keeps the piece 3 wording; Flush does nothing while the panel is hidden.
- Alert bar: a stale hover reference; severity is shown by colour and name only; chip gap and padding are not DPI-scaled; the overflow menu is untested.
- Notes and headers: header and strip tooltips go stale after an automatic pop-out; alert match status is recomputed on every keystroke without debounce; the Formulas tab commits twice per flush; test breadth is thin.
- Tour: cancelling the setup chooser means no automatic tour; the arrow clamp is repeated; the handle check is strict.

## Piece 6a (players, visibility, hot-seat) — rulings and deferred minors

### Rulings made during execution
- Private-roll and private-ledger attribution is explicit: every formula run stamps the acting side and unit on its ledger lines and dice rolls, and the log lines keep that stamp. Guessing from neighbouring lines may only hide more, never show more. A phase's side is used as a fallback only when the phase name maps to a single side. Cost: in edge cases a log line is hidden that could have been shown.
- Log lines of deleted pieces are hidden from everyone (fail closed), with a diagnostic. The validator warns when a side has an empty unit filter and there are two or more sides.
- Trail points on a map the viewer cannot see are not filtered, because that needs a reducer change that would alter how existing saves replay. Cost: a visible piece's trail can show earlier coordinates on a hidden map.
- Every path that applies a formula (Next Phase, automatic runs, the formula dialog, the batch grid, missing inputs) stamps the current player's side. Automatic phase-start runs stamp the phase's acting side, not the outgoing player's. When both a stamp and a unit side exist, the viewer must hold both to see the line.
- Undo labels are plain (no names or numbers) when the action touched hidden pieces. Cost: less descriptive labels in hot-seat.
- Until Continue is pressed after a handover, or after loading a multi-player game, the game shows public information only (a briefly empty view behind the handover screen). Only taking over sets a player's sides. A repeated handover for the same player is raised once.
- Rewinding back into the current player's phase cancels the pending handover and restores their view. Changing the player list from solo to several players mid-game raises a handover for the acting player.
- The players on the setup page are part of the game's start (genesis), not an undoable command.
- Next Phase is disabled, with a tooltip, while the acting side is not held by the current player. Phase instructions are hidden then too. The Formulas, Alerts and Properties tabs refresh when the view changes.
- The player-view speed test allows 500 ms (it was flaky under a parallel test run). Cost: a weaker guard on speed.
- **Acceptance ruling:** the formula dialog and the batch grid evaluate against the full game state for the player's own units, so a hidden input a formula reads is visible there. Hot-seat is honour-based in 6a. Masking inputs belongs to piece 6b.

### Rulings of the final review
- A move off a map that has an owner no longer names the square or hex it left: the log line reads "{unit} moved → {to}". The log line only records the map it went to, so the source would otherwise reach players who cannot see that map. Done when the line is written; the reducer and replay are unchanged.
- A holder or map whose owner names a side that no longer exists counts as public at runtime (spec 8a.10), in the player view, in "can see map" and in "can edit ledger". The validator still reports the dangling side.
- Loading or starting a game decides public-only or solo view at once, before seeding the starting values can raise a view change. So a multi-player game is never shown in full for a moment.
- "Can this player act on / see this unit" is answered from the full state through ownership, map owners and the hide/mask state, with the side filters compiled once per game state. It no longer builds the whole player view for each unit. Same answers; a speed test covers applying 20 units in hot-seat on a 1,200-piece game.
- Hot-seat re-checks the handover after a rules change and after a phase relocation outside Next Phase or a rewind. If the current phase's side was edited, the handover is raised or cancelled as needed.
- Tests added: automatic OnMove runs are stamped with the phase's side, or with the player's side when the phase has none; the view is fresh inside a rules-applied handler.

### Deferred minors
- Rules pack loading: formats below 4 do not clear owner fields that were edited by hand; an empty holder side and a map owner can disagree; only the map loop is guarded; the app does not pass the known map ids to the loader.
- Visibility tab: no reload after a failed commit; an owner is matched by name with IndexOf; the issue filter matches message text; duplicate rows for items not in the module; some test gaps.
- Player view: building the view costs about 80 ms on a large game (it deep-copies the state); pairing of ledger lines to holders has an edge case; a deleted piece's lines are over-hidden for its own side.
- View cache: a duplicate view-changed event per rules update or command (the UI coalesces them); a stale-publish race across threads; the reset of view sides on a rules change has no test.
- Rewind and stamps: the undo label stays plain for a player's own removed pieces; a queued-refresh flag has a small race.
- Phase strip: hovering a chip shows the notes of another side's phase (rules content, not game state).
- Small counts: a move of several pieces says "{n} pieces moved" and the step-back preview counts actions and dice for everyone, so another side can learn how many pieces moved or how many actions a step back undoes.
- Alerts: how alerts should behave during another side's phase is not decided yet.
- Smokes: `--screenshot-playerview` makes one property private in memory when the pack has none (older packs load everything Public); it never saves the pack.

## Launcher — rulings and deferred minors

### Rulings made during execution
- The settings store has one `Update(Func)` with a single lock and unique temp file names, because the background module check and the game window both write settings. Changed events may fire on the calling thread.
- App settings writes use `SettingsStore.Update` instead of saving a copy of the current settings.
- The Back to launcher flag is set when the window has really closed (after the unsaved-changes prompt), not when the menu item is clicked. The menu item is hidden when there is no launcher.
- The launcher reads a fresh settings store each time it opens, so it shows what the game window just wrote (last played, recent saves).
- The "Reopen last module" setting now means "Select the last module in the launcher". It never skips the launcher.
- The Continue line drops the turn or phase part when the autosave has none (turn 0, empty phase).
- A cancelled module check never writes settings: cancellation is checked again right before the final write.
- Play offline: while a module loads or the setup dialog is open, the window's buttons are disabled, so a second click cannot start another run.
- The "file was not found" hint on the disabled Play button is shown through a host panel, like the Online lobby hint, since a disabled button gets no mouse events.
- Smokes: `--smoke-launcher <module> --smoke N` starts a blank new game from the launcher, goes back to the launcher and checks that the module shows a last-played time. The two screenshot flags take a module path and set up a scratch library (one module listed, one missing; for the offline window also an autosave and recent saves). Always run with `QUARTERMASTER_LAYOUT_ROOT` pointing at a scratch folder.

### Rulings of the final review
- Launcher screenshot runs (`--screenshot-launcher`, `--screenshot-offline`) check `QUARTERMASTER_LAYOUT_ROOT` before reading or writing anything, with or without a module path; without it they log the reason and exit 1. `--smoke-launcher` already did.
- An unreadable `settings.json` (bad JSON, not an object, or a read error such as a lock) is never replaced by defaults: the store copies it once to `settings.json.bad-<yyyyMMdd-HHmmss>`, logs one warning, and is marked degraded. While degraded, changes stay in memory for the session but are not written (`LastSaveError` says why) until a later load succeeds. A missing file is not degraded.
- A module added while the module check runs is hashed by one more check that starts when the running one ends. It only re-runs when something was added meanwhile, so an unhashable file cannot loop.
- Starting a game from the launcher loads the remembered rules pack only when it differs from the one already loaded and the file or folder still exists (same guard as Play offline's New game). A moved pack is skipped without a dialog.
- After Back to launcher, the launcher selects the module just played whatever "Select the last module in the launcher" says.
- `ModuleLibrary.MarkPlayed` writes nothing when no entry matches or the time is unchanged.
- The module check catches every error per entry (except out of memory), so one bad path (invalid characters, unreadable archive) cannot stop the others.
- Rules pack "(none)": a `use (none)` link next to `change…` (shown when a pack is remembered) forgets it via `RulesMemory.Forget`; `ModuleLibrary.SetRulesPack(entry, null)` does the same.

### Deferred minors
- A module on a slow or offline network path can stall the module check (existence checks and hashing are not time-limited).
- The Play offline window's close veto (while busy) does not look at `CloseReason`, so it also refuses a Windows shutdown or task-manager close.
- Two app instances share `settings.json`: the last one to write wins (no merge across processes).
- Play offline's New game opens the module once to read its setups and the game window opens it again.
- Setup dialog: players are not validated when the module has no rules pack (a test documents this).
- The Online lobby panel height in the setup dialog is not tuned yet (6b).
- A module that fails to open from the launcher leaves an empty game window.
- A whitespace-only module path is not handled specially.
- Opening a module by command-line path does not record it as last played in the launcher list.

## Piece 6b (live online play): parked items

- A side filter that depends on a piece's position could, in an edge case, let a move change which side a piece belongs to. The host would not notice until the next view is built. Not seen in practice.
- A mask (hidden input) on a piece that has no side is not enforced. This was already so before 6b.
- If the host undoes a change of players made in the session, the saved player list and the online seats can disagree. Seats are what the host checks, so play is not affected.
- Guests apply "show trail" and "expand stack" on their own copy only, because the host refuses them. They are not shared between players.
- Task 4 screenshot smokes crashed in the graphics library while the display was locked; rerun them on an unlocked desktop.
- All guests of a game share one game key. Guest-to-guest privacy relies on the relay routing messages honestly, so run your own relay. Any room creator gets the larger host message budget on the relay.
- The host holds a side too (a game needs every side held). A host who only moderates must hold an extra side with no pieces.
- The end-to-end test (`Piece6bEndToEndTests`) covers two guests over a real in-process relay; the Docker image is checked by hand (see the task 9 report), not by tests.

## Piece 6c (turn files and mailbox): parked items

- A trait type with more than 256 traits and empty initial states ignores a high trait-state change when a turn is replayed. Very unlikely.
- One test builds a very large turn file to check the size limit, so it is heavy to run.
- The relay's mailbox does its disk work while holding its lock, so a slow disk slows other mailbox requests.
- If the relay fails to save a box's index right after a stray file was deleted, some files can go uncounted against the mailbox total.
- The relay setup guide and the code now say MiB for sizes; older notes may still say MB.
- When fetching a waiting turn keeps failing, the same status message is repeated on each poll.
- A single poll does not cap how many items it handles, and does not check item ids first.
- A turn from another game in the same box shows "This turn belongs to another game." instead of being quietly skipped.
- The mailbox poll's cancellation source is not disposed; the tests do not cover stopping mid-poll, a wrong game or trust prompts.
- Undoing a pass does not clear the redo list.
- Sending a turn catches write errors broadly except out of memory; a narrower list may be better.
- The list of already-played turns is kept per game identity and can go stale while the game is open.
- The replay bar height is not scaled for high-DPI screens.

## Piece 7 (rulebook search and OCR): parked items

- Dice-like text: the dice pattern should require a digit before the d; thin-space and similar space characters (U+2007, U+2008, U+00A0) are not all handled; forced column counts below N lack docs and a test; digit matching mixes `[0-9]` and `\d`.
- A corrupt PDF that ends early is treated as a temporary failure, so it is parsed again on every index build. A non-seekable file over 512 MB is reported as missing. The temporary-failure path during parsing has no test. A hostile PDF that hangs the parser cannot be cancelled.
- The image guard turns any argument error into "too large". A failed PNG save leaks a raw error. A corrupt image body gives a raw error that the app has to catch. The PDF render error wording is a catch-all.
- Search panel: the index cancellation source is not disposed; a failure is logged even after cancel; the Search button could enable earlier; result links should be tab stops; Find in rulebook can use a stale selection; Ctrl+Shift+F with no pack open does nothing visible.
- OCR grid: the "empty cell" message also shows for cells that are meant to be blank. A merged anchor-plus-digit cell stays empty with a message.
- Scan cache pruning can race with a file check. The test `RelayClientTests.MemberId_Exposed_AndUpdatedOnRejoin` is flaky under heavy parallel load.
- Small text is read natively first and again at 2x only when words look small or nothing was found, so it can cost two OCR passes. PdfPig is also referenced from the OCR tests (a deviation from the spec).

## Piece 8 (ledger images and tabs, updater, release script): parked items

- Ledger tab store: adding a tab uses add-then-RemoveAt; a tab with only unknown column ids falls back to the Units columns; Save overwrites an unparseable root settings file and loses every module's tabs (keep a `.bak`); a null column-widths entry skips the whole write silently.
- Counter images: six small items (see the task 3 review), including a dead generation counter in `CounterImages`; null renders are not negative-cached (re-queued on every paint); the pending set is not pruned; Reset runs on every refresh; SetVisible runs per cell paint; no unit test for `CounterCellImages`.
- Ledger tabs window: seven small items (see the task 4 review): renaming the current tab jumps back to Units; widths are stored by position; holder rows leave an empty image gap; the breakdown tip may fall back to the full state (pre-existing, judge the hidden-information impact). Not tried by hand: real hover and double-click, and the OK path of Edit tabs.
- Updater, download side: IO failures in extract and launch-helper escape as `IOException` instead of `UpdateRefused`; `UpdateFeeds.Default` accepts a redirecting client (callers must use `GitHubFeed.CreateClient()`); the env-var test is not in a serial test collection; the tolerant-load test does not assert warnings.
- Updater helper: a "rolledback" journal with the same version makes a retry report failure without trying; a journal without a step is treated as an install; a stale-rollback failure is retried twice.
- Update dialog (task 8): see the task 8 review minors: the finishing step can race the launcher closing, a failed check does not stamp `LastUpdateCheckUtc`, and the App has no test project for the dialog.
- `VersionNotice` sits between the `NotAPlayerText` doc comment and its constant in `TurnReplayModel.cs` (about line 296).
- Release script: Linux and macOS archives are built on Windows, so the executable bit is not stored; the docs say `chmod +x`. The archive check looks for the literal `C:\Users\<user name>` in binaries only, not for other absolute paths. The script was only run under Windows PowerShell 5.1 here.

## Found while writing the user wiki: parked items

- The module rules/text window shows text with LF-only line endings all on one line (the demo module was changed to CRLF to avoid it); real modules will hit it. The window should normalise line endings.
- The toolbar "Next turn" button and Tools > Turn > Next turn only step the turn counter, but the shortcut shown next to it, Ctrl+Right, does Next Phase when the pack has phases.
