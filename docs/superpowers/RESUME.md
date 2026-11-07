# Quartermaster — resume here

*Last updated 2026-11-07, after piece 5 was finished on its branch. A new session should read this file first, then the docs it points to.*

## What this is

Quartermaster is a Windows desktop engine (C# .NET 10, WinForms MDI, SkiaSharp, WebView2) that opens a VASSAL module (`.vmod`) and adds what VASSAL leaves to you:
- ledgers;
- lookup tables;
- formulas with live calculation;
- dice;
- a game log;
- a guided turn sequence.

The look is a dense, gray, classic Windows program in the style of *Rule the Waves 3*. The user is building it to play a large logistics-heavy wargame they own as a VASSAL module. They have never played that game and are learning it through this tool.

## Hard boundary — do not revisit

The engine ships **no content from any real game**: no rules text, tables, formulas, turn order, map, counters or chart data, in code, tests, docs or rules packs written by the assistant.
- The user enters real rules themselves in the Rules Workbench.
- The assistant may build **invented** test content (clearly labelled TEST) wired to the user's module structure, such as unit-name prefixes and marker keys.
- The user pushed back on this twice. The line was held. Don't reopen it.
- The repo is **public**, so this matters doubly.

## Repo and git

- Location: `%USERPROFILE%\Documents\Projects\CNAEngine`. Solution: `Quartermaster.slnx`.
- Projects:
  - `Quartermaster.Vassal`: `.vmod` parsing, partly ported from VASSAL under LGPL-2.1.
  - `Quartermaster.Rules`: rules packs and the expression language.
  - `Quartermaster.Core`: command-log game state and deterministic replay.
  - `Quartermaster.Render`: UI-free models.
  - `Quartermaster.App`: the WinForms app.
- Tests: xUnit under `tests/`. 898 tests are green after piece 5 (173 Rules, 116 Vassal, 207 Core, 402 Render).
- Remote `origin` is **public**: https://github.com/EmojiPlay88/Quartermaster. Everything through piece 4 is **pushed**. Piece 5 and a small undo fix are merged into local `master` but **not pushed**; push only when the user asks.
- Before any push: check commit emails are the noreply address, `startapp.cmd` is not staged, and docs contain no absolute personal paths (use `%USERPROFILE%`) and no real-game content.
- Commit identity is repo-local, with the noreply email `80044157+EmojiPlay88@users.noreply.github.com`. Never use the user's gmail.
- Commit messages end with a blank line and then `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Never commit `startapp.cmd`.** It is untracked, names a private file path, and the repo is public. `.claude/` is excluded locally via `.git/info/exclude`.

## Done so far (pieces 1-5 merged to master)

| Piece | What | Docs |
|---|---|---|
| 1 | Module reader, map, counters, drag, browser-style tabs | `specs/…engine-design.md`, `plans/…piece1…` |
| 2 | Rules packs, ledgers, formula runner, layout-C formula dialog, Ledger window, Rules Workbench, multi-monitor pop-outs | `plans/…piece2…` |
| 2b | Charts/Tables trees, dice tools, LOS, overview, notes, inventory, map export, Settings + autosave, game-master counter palette, log panel | `specs/…piece2b…`, `plans/…piece2b…` |
| 3 | Turn sequencer + difficulty modes (below) | `specs/2026-11-06-piece3-sequencer-difficulty-design.md`, `plans/…piece3…` |
| 4 | Undo / Redo and Step Back by truncate-and-replay of the command log (below) | `specs/2026-11-07-piece4-undo-design.md`, `plans/…piece4…` |
| 5 | Accessibility layer: breakdown tooltips, alert bar, what-now line, outliner, interface tour, notes (below) | `specs/2026-11-07-piece5-accessibility-design.md`, `plans/…piece5…` |

All rulings and deferred minors for every piece are in `docs/superpowers/piece1-followups.md`.

### Piece 5 in one paragraph
Numbers explain themselves, using only history (nothing previews a formula result, so Hardcore stays hard).
- **Breakdown tooltips** on ledger cells, the inventory, unit popups and batch grids: earlier-turns net, "Start of turn N", then each change with its source, rule reference and phase.
- **Alert bar** with built-in alerts (negative values, missing inputs, pending calculations, phase warnings) and user alerts defined in the Rules Workbench Alerts tab (a boolean expression per unit, a severity, an optional side). Chips click through to the pieces and can be hidden for the turn; the "what now" line sits beside the bar.
- **Outliner** dock: units grouped by map or organisation display, with Sum/Min/Average columns chosen per property in the Properties tab, and alert markers.
- **Interface tour** (Help, and once on first game open) and notes on properties, formulas, tables and alerts.
- **Formats:** rules pack format 3, save format 6 (format 5 saves keep full undo). Hidden alerts are stored in the save.
- **Smokes:** `--screenshot-breakdown`, `--screenshot-alerts`, `--screenshot-outliner`, `--screenshot-tour` (the tour smoke restores the user's first-run setting).

### Piece 4 in one paragraph
The command log is the history. Undo, Redo and Step Back rewind by cutting the log and replaying it from the start (with cached snapshots), so a rewound state is JSON-identical to the state recorded at that point. Commands are grouped into actions (one drag, one Next Phase with everything it triggered), and phase or turn boundaries are checkpoints.
- **Edit menu and toolbar:** Undo, Redo (Ctrl+Z, Ctrl+Y), Step Back to Start of Phase (Ctrl+Left), Step Back To... (a list of checkpoints with a plain-words preview of what would be undone).
- **Step back** logs a "Stepped back" line, undoes dice rolled after the checkpoint (rolling again gives new results), and autosaves first.
- **Saves:** format 5 stores the action marks and checkpoints. Formats 1-4 still load; undo then starts at the load point.
- **Autosave** keeps the last 20 per module plus the first of each turn.
- **Smokes:** `--smoke-undo` and `--screenshot-stepback <png>` (run on a fully automatic game).

### Piece 3 in one paragraph
Phases come from the rules pack and are authored in the Rules Workbench → Phases tab. A rules pack defines sides with a unit filter, phases, and repeating blocks.

The game window gets:
- a **tracker strip** showing the turn, the stage ("Axis stage 1 of 2"), the acting side and the phases;
- a **phase panel** with instructions, warnings with Acknowledge, and the decisions still open;
- a **Next Phase** button showing "N decisions left". It warns and lists the open decisions, and never blocks.

There are **three difficulties**, chosen at setup and switchable in Settings → Game:
- **Hardcore:** empty boxes; typed values are checked.
- **Semi-automatic:** hardcore plus a cheat-sheet window.
- **Fully automatic:** the engine runs formulas at phase start and on moves, and logs every calculation. It may ask, in a small prompt, only for values that have no default.

How the formula window behaves:
- **A wrong typed value can never reach the ledger.** Apply stays disabled while any box is wrong, and the ledger always receives the engine's own run (ruling P3-R5).
- Table keys are dropdowns.
- **Calculate** fills every box.
- A formula that runs once per unit, or a drag of several units, opens one batch grid instead of a window per unit.

## User's test material (outside the repo, never commit)

- Module: `%USERPROFILE%\Downloads\CNAv2.1.0.vmod`.
- Test rules pack: `%USERPROFILE%\Documents\Quartermaster\Rules\CNA-TEST-invented.qrules`, with a `.bak` of the pre-phase version beside it. All of it is **invented**.
  - Values tracked: Fuel, Ammo, Water, Stores, Readiness and Weather.
  - Holders: two test depots.
  - 4 tables and 7 TEST formulas.
  - Sides by unit-name prefix: Axis = IT/GE, Allied = BR/AL/IN/AU/SA/NZ.
  - 10 invented phases: Weather → Axis Supply → (Axis Movement/Combat ×2) → Allied Supply → (Allied Movement/Combat ×2) → Recovery.
- The units to target carry the marker `Type == "Pieces"` (about 1,170 in the default setup).

## Where we stopped / next steps

1. **The user hands-on tests piece 5** with the test pack: hover numbers in the Ledger, inventory and a unit popup; add a user alert in the Workbench Alerts tab and watch the bar and chips; set an outliner aggregate on a property and use the outliner dock; Help, Interface Tour; step back and check the bar and outliner follow. Fix whatever they find. Known open issue: the map hover popup can stay open if the pointer leaves it past the map edge onto a docked panel (fix: poll cursor against the popup bounds after the early return in MapCanvas).

   Still open from piece 4: hands-on test with the test pack: Ctrl+Z and Ctrl+Y after a drag and after Next Phase; Step Back to Start of Phase and Step Back To...; redo after a step back; dice after a step back; an old (format 1-4) save and the greyed-out Undo tooltip; difficulty switch then undo; Open Autosave of a turn-boundary snapshot. Fix whatever they find.

   Still open from piece 3, if not yet done: hands-on test of piece 3 with the test pack. Checklist:
   - Start game ▶.
   - Next Phase with open decisions.
   - Hardcore red box + Calculate.
   - Semi-auto cheat sheet.
   - Fully automatic logging.
   - Switch difficulty mid-game.
   - Batch grid dropdowns and Apply all.
   - Workbench phase editing with a broken block.
   - Delete the current phase mid-game.
   - Load a phase-boundary autosave.

   Fix whatever they find.
2. The README Status line and Roadmap are current as of piece 5. The intro paragraph was rewritten by the user's request (plain tone, with a jokey closing line about closing the spreadsheet); keep that wording.
3. **Next piece: multiplayer** (master spec section 11): hot-seat, live online with chat in the log, and play by turn files. After that, OCR for scanned rulebooks (section 8.3). Brainstorm, spec, plan, build, using the superpowers skills.
4. Residual piece-3 minors are listed at the end of `piece1-followups.md`.

## How the user likes to work

- They choose quickly ("ok yeah", "1"), like seeing mockups in the browser companion, and want plain-language summaries.
- **Build process they chose:** subagent-driven development with **parallel agents**:
  - Each task gets its own git worktree, with a task review after it.
  - Fix rounds go back to the same implementer.
  - Branches are merged into the feature branch as soon as they're approved.
  - Each task starts as soon as its own dependencies are merged.
  - The piece ends with an opus whole-branch review → one fix wave → a scoped re-review.
- Agent worktrees branch from `origin/master`, not the local branch. Have each agent `git merge` the feature branch plus any dependency branches first.
- Agents can't always write outside their worktree. Have them write reports inside their worktree, then copy the reports into `.superpowers/sdd/<plan>/`.
- Bash heredocs in this environment mangle backslashes. Write scripts with the Write tool.
