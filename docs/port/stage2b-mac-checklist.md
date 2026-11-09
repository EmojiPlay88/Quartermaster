# Stage 2b checklist for the M1 Mac

This is a test of the third cross-platform build of Quartermaster. On top of stage 2a (rules packs, right-click menu, hover popup, line of sight, dice, turn tools, toolbar, log) it adds: the phase strip and phase panel, Next Phase, Step Back, the formula window with its cheat sheet, the batch grid, the alert bar, the module tree and the pop-out log and phase panels. Ledger, notes, inventory and the rulebook viewer come later. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 40 minutes. Use the Riverland demo module and its rules pack. Both are in `docs/wiki/demo` of the project: `Riverland-Demo.vmod` and `Riverland-Demo.qrules`. Copy both to your Mac first, for example to the Desktop. You can also download them directly:

- https://github.com/EmojiPlay88/Quartermaster/raw/master/docs/wiki/demo/Riverland-Demo.vmod
- https://github.com/EmojiPlay88/Quartermaster/raw/master/docs/wiki/demo/Riverland-Demo.qrules

(If the browser shows the `.qrules` file as text instead of saving it, use File > Save As in the browser, or right-click the link and choose Download Linked File.)

What the Riverland demo has and has not got, so you know what to expect:

- Six phases, in order: North Supply, North Movement, North Combat, South Supply, South Movement, South Combat. The North and South Supply phases start calculations for every unit by themselves (a batch of them). North Combat has a short checklist.
- The demo has no ready-made setups (the setups folder in the tree is empty), so always start with **New game from module**.
- Formulas you can use: Weather roll, North/South Depot delivery, Fuel supply, Ammo supply, Fuel for movement, Ammo for firing, Attack roll, Unit position.
- There is no formula with a drop-down list (choice) in the demo, so the "combos" in the batch grid cannot be tested with Riverland. Say so in your notes and skip step 41.
- The demo has one alert, **Low fuel**, plus the built-in "Formula not applied" alert once calculations are waiting.

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you). Open the repository on github.com and click the **Actions** tab at the top.
2. Click the newest run named **cross-platform**. Wait until all three lines (windows-latest, macos-latest, ubuntu-latest) have a green tick. If one is red, click it and send me a screenshot of the red step; stop here.
3. Scroll to the bottom of the run page to the **Artifacts** box. Click **Quartermaster-Desktop-osx-arm64**. Your Mac downloads a zip into Downloads.
4. Double-click the zip in Finder to unzip it. You get a file `Quartermaster-Desktop-osx-arm64.tar.gz`. Double-click that too. You now have a folder `osx-arm64`. Move it to your Desktop (replace the stage 2a folder).

## B. First start (Gatekeeper)

The app is not signed, so macOS blocks it the first time (again, because this is a new download).

5. Open the `osx-arm64` folder and find the file named **Quartermaster** (no extension). **Right-click** it (or Ctrl+click) and choose **Open**. A warning appears; click **Open** again.
6. If nothing happens, or it says the file cannot be opened or "permission denied", open Terminal (Spotlight: press Cmd+Space, type Terminal) and paste these two lines, adjusting the folder if you put it elsewhere, then press Return after each:
   `xattr -dr com.apple.quarantine ~/Desktop/osx-arm64`
   `chmod +x ~/Desktop/osx-arm64/Quartermaster`
   Then try step 5 again.
   - A Terminal window opening next to the app is normal for this test build: it is a bare program, not a `.app` bundle yet. Leave Terminal open while testing.
   - If the "cannot verify / malware" warning still appears (macOS 15 Sequoia and newer no longer accept right-click > Open): start it from Terminal with `~/Desktop/osx-arm64/Quartermaster`. If it is still blocked, click **Done** on the warning, open **System Settings > Privacy & Security**, scroll down, click **Open Anyway** next to Quartermaster and confirm with your password.
7. The window should open with a menu bar and a toolbar. Note anything that looks different from the Windows Quartermaster.

## C. Start a game and the phase strip

8. **File > Open Module...** (Cmd+O), pick `Riverland-Demo.vmod`, keep "New game from module", set **Difficulty** to **Semi-automatic**, click **OK**. Then **File > Open Rules Pack...** and pick `Riverland-Demo.qrules` (if it is not loaded already).
9. Under the toolbar you should see a strip with a **Start game ▶** button, "Before turn 1" and the six phase names. Under the strip is the alert bar with a "Press Start game ▶ to begin turn 1." line. On the left is the module tree (Riverland > Maps, Organisation Displays, ...) with a smaller tree of Tables and Formulas below it. On the right is the phase panel. Is everything in place and readable?
10. Click **Start game ▶** (strip or phase panel). The strip now shows "Turn 1" and the phase names; North Supply is highlighted. The weather roll and the supply calculations open as windows (see section F). For now close them with Cancel. The phase panel on the right shows the phase name, its instructions and a red "N decisions left" while calculations wait.

## D. Next Phase, the open-decisions box and Step Back

11. Click **Next Phase ▶ North Movement** in the phase panel. The phase moves on. If the box "decisions are still open in this phase" appears, go to step 12; otherwise go on to step 13.
12. In that box click **Go back** (it is the default: press Return). Nothing changes. Open it again and click **Continue anyway**: the phase moves on. Double-click one line in the list instead: the box closes and you are taken to that item (a calculation window opens, or the checklist item in the phase panel is selected).
13. Click the map, then press **Cmd+Right**. It does the same as the Next Phase button (the menu entry **Tools > Turn > Next** shows "⌘→"). Do it until you reach **North Combat**. The phase panel now has a checklist with three lines: tick one. The "decisions left" count goes down.
14. Press **Cmd+Right** once more with unticked items left: the open-decisions box lists them. Click **Go back**.
15. **Edit > Step Back to Start of Phase** (shortcut Cmd+Left, shown as "⌘←"). A box asks you to confirm and shows what will be undone. Click **Step Back**. The status bar says "Stepped back — reopen from the phase panel", any calculation windows close, and the strip returns to the start of the phase.
16. **Edit > Step Back To...**. A list appears with Turn, Stage, Side, Phase, Reached and Actions, newest first. Click different lines: the text under the list changes (for example "1 action"). Pick an earlier phase and click **Step Back** (or double-click a line). Check that the strip and panel follow.
17. **Edit > Redo** (Cmd+Y) brings the steps back. The toolbar button **◀ Step Back** does the same as step 15. Is it greyed out at the start of the game, with a tooltip saying why?

## E. Calculate from the counter menu

18. **Right-click** a Northern League unit (for example NL 1 Rifles). In the menu choose **Calculate** and then **Attack roll**. A formula window opens (title "Attack roll — NL 1 Rifles" or similar). Close it with **Cancel**.
19. Do the same with **Ctrl+click** instead of right-click. It should open the same menu and must not add the counter to the selection.
20. Right-click a counter and pick **Fuel for movement**. In Hardcore and Semi-automatic the boxes start empty. Is there a Rule ref button? It is enabled only when the formula has a rule reference that exists in the rules pack; clicking it opens the referenced document at that page.
21. Right-click an empty part of the map: there is no Calculate entry there (only counters have it). Press Esc in an open formula window: it closes without applying anything.

## F. Formula window and the Semi-automatic cheat sheet

22. Open **Attack roll** for a unit (step 18). A second small window, **Cheat sheet**, should open beside it, with the columns Variable, Source, Value. It lists for example "Readiness of NL 1 Rifles = 3" and tells you to roll the die. Is the cheat sheet clear of the formula window and readable?
23. The window shows `[ ] + floor( [ ] ÷ 2) = [ ]`. Click the die box, type a number from 1 to 6 and press Tab. Type the Readiness number the cheat sheet shows. The result box fills in and the Working line shows the sum, for example "4 + floor(3 ÷ 2) = 5".
24. Type a wrong Readiness (for example 9): the box turns red and **Apply to ledger** greys out. Type the right one: Apply comes back.
25. Click **Calculate**: it fills the boxes for you. Tick **Physical dice** and see that the die box waits for your number; untick it. Click the small re-roll button under the die box (if shown) and check the number changes.
26. Click **Apply to ledger**. The window closes, the unit's value changes (hover the counter: the popup shows the new number) and the log shows a "calc" line. Press Cmd+Z: the change goes away.
27. Open the same formula for the same unit twice: you get the same window brought to the front, not a second one.
28. Start a **new game** with **Hardcore** difficulty (File > New Game) and open Attack roll: no cheat sheet this time, and the boxes are empty. Start another game with **Fully automatic**: the boxes are filled in already.

## G. Batch grid and missing-input box

Use a Semi-automatic game again (File > New Game, then Start game ▶ in the strip).

29. When North Supply starts, calculation windows open for the weather and the depot delivery, and **one batch grid** opens for "Fuel supply" (and another for "Ammo supply"): a table with a row per unit (NL 1 Rifles, ...) and columns Unit, Fuel, Hexes, Result, Ledger, with a cheat sheet panel at the right. If several windows opened on top of each other, move them apart; do the windows come up in sensible places?
30. Click into the **Hexes** cell of the first row, type a number such as 2, press Tab. Type the unit's current fuel in the **Fuel** cell (the cheat sheet at the right says where it comes from). **Result** and **Ledger** fill in, for example "5" and "Fuel 3 → 5". A wrong value turns red. Hover the Ledger cell: a tip with the breakdown appears.
31. Click **Calculate all**: all rows fill in. Click **Apply row** with one row selected: only that row is applied. Click **Apply all**: the rest is applied and the grid closes when nothing is left.
32. Double-click the **Unit** cell of a row: that unit's own formula window opens.
33. Scroll the grid if it has more rows than fit. Does scrolling feel smooth, and do typed values stay in the right rows?
34. Start a **Fully automatic** game and press Start game ▶. This time the weather roll and delivery are done by the game and logged. If a calculation needs a value the game cannot know (Hexes, with no default), a **missing input** box appears asking for it. Fill the answers and click **OK**; or click **Per unit...** to get the batch grid or the unit window instead. Does OK stay greyed out until every field has an answer?
35. In the strip or the phase panel, click the line of a waiting calculation under "Decisions left" (double-click): the window for it opens.
36. In the missing-input box (step 34) press Esc: it closes as Cancel and nothing is applied. Close the batch grid with its window button: nothing is applied either.
37. With a batch grid open, **Edit > Step Back to Start of Phase**: the grid and formula windows close by themselves.
38. Counter pictures in the batch grid's Unit column are not there yet (that comes with the Ledger). Just confirm the names are readable.

(Steps 39 to 41 cannot be tested with the Riverland demo.)

39. Drop-down ("choice") columns in the batch grid: not available in Riverland. Skip.
40. Rules pack with its own setups: Riverland has none. Skip.
41. Combo boxes in a formula window: Riverland has none. Skip.

## H. Alert bar

42. With the game started, look at the alert bar under the strip. Left: the "Now: ..." line with the current phase and its instructions. Click it: the phase panel is shown (if hidden). Right: coloured chips such as **Low fuel (N)** and **Formula not applied (N)**.
43. **Click a chip**: the first matching counter is selected and the map centres on it. Click again: it goes to the next match. The status bar names the counter.
44. **Right-click** a chip (or Ctrl+click): a menu lists every match and **Hide this alert until next turn**. Pick a match: it is selected. Pick Hide: the chip goes away until the next turn.
45. Make the window narrow (drag its edge). Chips that do not fit collapse into a **+N** chip; click it: a menu lists them, each with its own matches and Hide entry. Make the window wide again.
46. Make one unit's fuel low (a counter with fuel below 2 is already there at the start) and check the **Low fuel** count changes after a moment (a quarter of a second) when its fuel goes up through a supply calculation.
47. Is there a small grey piece of text at the very left of the "Now:" line, such as "+00"? There should not be (it was a bug, now fixed). Say if you see it.

## I. Module tree and tables tree

48. **View > Module Tree** hides and shows the whole left column. Show it again.
49. In the module tree open the **Maps** folder: the Riverland map is listed. Double-click it: its tab comes to the front. Press Return on the selected node: the same.
50. Double-click **Setups**: the new game dialog opens (Riverland has no setups listed; cancel it).
51. Rulebooks and Documents nodes: double-click one. Its document window opens (a second double-click on the same node only brings that window to the front and keeps the page).
52. In the Tables and Formulas tree below it: the table **SupplyByDistance** and the eight formulas are listed. Double-clicking does nothing yet (that is expected).
53. Drag the line between the module tree and the Tables tree up and down. Quit (Cmd+Q) and start again with the same module: the split should be remembered.

## J. Pop out the log and the phase panel, second display

54. At the log's top row click **Pop out**. The log leaves the main window and opens as its own window; the button now says **Dock**. Move and resize it. The main window's map gets the freed space.
55. If you have a second display, drag the log window onto it. Click **Dock** (or close the log window): the log is back at the bottom of the main window. Click **Pop out** again: it opens on the second display where you left it. (Position and size are remembered for this module.)
56. In the phase panel click **Pop out**: a window called **Phases** opens beside the main window's right edge. Move it to the second display and make it taller. **Next Phase ▶ ...** still works from there. Close it with the red button: the panel docks back into the main window.
57. With the phase panel and log both floating, quit the app, start it again, open the same module and start a game. Pop each one out again: do they come back where you left them? If you unplugged the second display in between, they must come back on the main display, not off screen.
58. **View > Log** (Cmd+Shift+L) and **View > Phase panel** while they float: they dock the panel back, as on Windows.

## K. Things from earlier stages that must still work

59. Right-click menu, hover popup with a breakdown tip, line of sight (L), hide counters (H), dice (F5 / F6 or fn+F5 / fn+F6), Cmd+Z, Save Game As (Cmd+Shift+S) and reopening the save. In a game that has gone through several phases, save, quit, reopen the save: the strip shows the same phase.
60. Open a game saved by the Windows Quartermaster (if you have one) and check the strip shows its phase.

## L. Graphics

61. While the windows open, while you drag the pop-out windows and while you type in the batch grid, did anything flicker, show black or white patches, or lag?

## What to send back

- Screenshots: the main window in North Combat with the phase panel and alert bar (step 13), the open-decisions box (step 14), the Step Back To list (step 16), the formula window with its cheat sheet (step 22), the batch grid with a row filled in (step 30), the alert bar with a chip menu open (step 44), the module tree (step 49), and the log or phase panel as a pop-out window (step 54 or 56).
- For each numbered step: OK or not OK. For anything not OK: what you did, what you expected, what happened.
- Whether **Cmd+Right** and **Cmd+Left** worked (steps 13 and 15), whether **Ctrl+click** behaved like right-click (step 19), and which keys you needed for F5 and F6.
- Whether the formula window, cheat sheet and batch grid opened where you expected, and whether pop-out windows came back on the right display (steps 55 to 57).
- Which Mac and macOS version you have (Apple menu > About This Mac).
- If the app crashed or showed an error text, a screenshot of it. Crash reports are in Console.app, under Crash Reports.
