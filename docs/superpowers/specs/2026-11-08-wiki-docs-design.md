# Quartermaster user wiki — design

Date: 2026-11-08. Status: approved in chat, awaiting written-design review.

## Intent

Quartermaster is public on GitHub (EmojiPlay88/Quartermaster). Strangers who find it
need to learn what it is, install it, play a game, host online games, and build a
rules pack for **their own** VASSAL module. Today the only user docs are `README.md`
(install/update, feature overview) and `docs/relay-setup.md`. Nothing teaches the
menus, the Rules Workbench or the formula language.

Success: a stranger with a VASSAL module and no contact with us can go from download
to a working rules pack (with supply/ledger values computed by formulas) using only
the wiki.

## Audience

Strangers (user's decision). Assume they know what VASSAL and a hex wargame are; do
not assume they know Quartermaster, programming, or networking.

## Goals

- Cover install/update, first game, multiplayer, relay hosting, every window/menu,
  the Workbench, and the formula language.
- Formula reference is complete and correct against the code.
- Pages are kept in the main repo so they are reviewed with code changes.

## Non-goals

- Translations (English only).
- Developer/contributor docs (architecture, building from source beyond one short
  section). The specs in `docs/superpowers/` stay the developer record.
- Video tutorials.

## Anti-goals (failure even if it "works")

- Any real-game content: no CNA names, counters, art, scans, rules text, or module
  files. Examples use an **invented demo module** (hard project rule).
- A formula reference that lists functions/operators that do not exist, or misses
  ones that do.
- Personal paths, emails, or the user's machine details in text or screenshots.
- Docs that claim Mac/Linux work before those builds ship.

## Pages (user agreed the list)

1. **Home** — what Quartermaster is, relation to VASSAL (opens .vmod, adds rules/
   ledger/supply on top), feature tour with screenshots, links to every page.
2. **Install & update** — Windows now; Mac (M1) and Linux sections added when those
   builds ship (see the cross-platform design). First-start warnings (SmartScreen;
   later Gatekeeper) and how to get past them. How the updater works, portable vs
   installed copies.
3. **Your first game** — open a module, board/map basics, moving pieces, saving,
   undo, hot-seat.
4. **Playing with others** — online lobbies (joining), turn files (play by email/
   file), seats and hidden information.
5. **Hosting a relay** — own page (user asked): pick the download per OS, run on a
   home PC vs a rented VPS, port forwarding, firewall, Docker, systemd autostart,
   lobby passwords and safety, updating the relay, checking it works. Absorbs
   `docs/relay-setup.md` (which then becomes a short pointer to the wiki page, or is
   kept in sync — builder's call, but only one place may be the source of truth).
6. **Menus and windows** — one page per window: board/map, Ledger (tabs, counter
   images, filters, CSV), search, rulebooks/PDF/OCR, launcher, settings, phases,
   notes, inventory, diagnostics, etc. Every window the app has gets covered.
7. **Making a rules pack** — step-by-step Workbench walkthrough, empty to working:
   properties, formulas, tables, phases, alerts, visibility, using the invented demo
   module.
8. **Formula reference** — every function, operator, property accessor, value type,
   with a short example each; common error messages and what they mean.
9. **FAQ & troubleshooting.**

Plus a `_Sidebar` for navigation.

## Decided how (my calls, user did not object)

- Source lives in the main repo under `docs/wiki/` (one `.md` per page, GitHub wiki
  naming: `Home.md`, `Install-and-Update.md`, …, `_Sidebar.md`, images under
  `docs/wiki/images/`).
- A script (`build/publish-wiki.ps1`) clones `Quartermaster.wiki.git`, mirrors
  `docs/wiki/` into it and pushes. Publishing is outward-facing: **always ask the
  user before running it.** The wiki must be initialised once by creating any page in
  the GitHub UI if the `.wiki.git` repo does not exist yet — the script detects this
  and tells the user.
- Formula reference accuracy is enforced by a test: a unit test enumerates the
  evaluator's real function/operator set and fails if any is missing from
  `Formula-Reference.md` or the page names one that does not exist.
- Screenshots come from the app's existing `--screenshot-*` flags where possible
  (repeatable), run against the invented demo module. New flags may be added for
  windows that lack one.
- The invented demo module: a tiny self-made `.vmod` plus rules pack (a few made-up
  counters, a small map), created by us, shipped in the repo as an example strangers
  can download and follow along with. Existing test fixtures may be reused if they
  contain no real-game content.

## Order

Wiki first, before the cross-platform port (user agreed): the new app keeps the same
look, so screenshots stay valid. Mac/Linux install sections are added when those
builds ship.

## Settled after builder check (my calls)

- **Demo module** is built from scratch (no fixture reuse unless provably invented),
  sized to show every documented feature once: a small hex map, two invented sides
  with 6–10 counters each (simple self-drawn art), a depot, hidden-information seats,
  and a rules pack with properties, formulas computing supply/fuel/readiness, a
  lookup table, phases, an alert and a visibility rule. Shipped as
  `docs/wiki/demo/` (module + pack) and linked from the walkthrough. I check it for
  real-game content before any publish.
- **Formula coverage test**: functions are dispatched by a `switch` in
  `src/Quartermaster.Rules/Expr/Evaluator.cs` (if, min, max, round, floor, ceil, abs,
  clamp, lookup, dice, has, startswith, contains, name, marker, …) and operators in
  the parser/evaluator. Add a public static list of function and operator names next
  to the switch, plus a test that the switch handles exactly that list, and a test in
  the normal suite that `Formula-Reference.md` documents exactly that list. It fails
  the build like any test.
- **Screenshots**: 31 `--screenshot-*` flags exist already (alerts, batch, ledger,
  ledger-tabs, workbench, ocr, search, online, turns, launcher, settings, …). Add
  flags only for windows that lack one; all run against the demo module. Windows
  without a flag may be shot by hand once if a flag is not worth it.
- **"Menus and windows" list** is enumerated from the code (every Form/dialog in
  `src/Quartermaster.App`), grouped sensibly; review checks completeness, no test.
- **`docs/relay-setup.md`** becomes a short pointer to the wiki page; the README and
  any in-app links to it point to the wiki instead.
- **Publish script** has a `-DryRun` switch (mirror into a temp clone, show the diff,
  no push) for testing; a real push only after asking the user.
- **Version**: documents the current release. Home says Windows today and that Mac
  and Linux builds are in progress, with no install steps until they ship.

## Left to the builder

Page wording and depth, screenshot choice, exact page split inside "Menus and
windows", how the coverage test parses the markdown.
