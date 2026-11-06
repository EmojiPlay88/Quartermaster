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
- Ruling P3-R4: the Phases-tab grids validate per keystroke and "Move" no longer refuses when an invalid block exists, so the user can reorder to repair it.
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
- Phases tab: grids commit and validate per keystroke without debouncing; `FormulaIdByName` takes the first match on duplicate names; `GoToIssue` is a no-op for side and block issues; `RemoveSide` skips Flush.
- Smoke: `AppLog` writes to the real `%LOCALAPPDATA%\Quartermaster\app.log` even when `QUARTERMASTER_LAYOUT_ROOT` is set (logging only; layout, autosave and settings do honour the root). The smoke's Weather-style once-per-turn dialog may be partly visible behind the screenshot.
- Deferred by the spec: stepping back a phase.
