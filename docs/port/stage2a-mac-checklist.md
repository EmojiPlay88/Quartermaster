# Stage 2a checklist for the M1 Mac

This is a test of the second cross-platform build of Quartermaster. On top of stage 1 (menu bar, map, counters, palette) it adds: rules packs, the right-click menu, the hover popup with its breakdown tips, line of sight, hiding counters, the last-moved highlight, dice and turn tools, the main toolbar and the log panel. The formula dialogs, phases, step back and the module tree come in stage 2b; ledger, notes and inventory come later. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 30 minutes. Use the Riverland demo module and its rules pack. Both are in `docs/wiki/demo` of the project: `Riverland-Demo.vmod` and `Riverland-Demo.qrules`. Copy both to your Mac first, for example to the Desktop. You can also download them directly:

- https://github.com/EmojiPlay88/Quartermaster/raw/master/docs/wiki/demo/Riverland-Demo.vmod
- https://github.com/EmojiPlay88/Quartermaster/raw/master/docs/wiki/demo/Riverland-Demo.qrules

(If the browser shows the `.qrules` file as text instead of saving it, use File > Save As in the browser, or right-click the link and choose Download Linked File.)

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you). Open the repository on github.com and click the **Actions** tab at the top.
2. Click the newest run named **cross-platform**. Wait until all three lines (windows-latest, macos-latest, ubuntu-latest) have a green tick. If one is red, click it and send me a screenshot of the red step; stop here.
3. Scroll to the bottom of the run page to the **Artifacts** box. Click **Quartermaster-Desktop-osx-arm64**. Your Mac downloads a zip into Downloads.
4. Double-click the zip in Finder to unzip it. You get a file `Quartermaster-Desktop-osx-arm64.tar.gz`. Double-click that too. You now have a folder `osx-arm64`. Move it to your Desktop (replace the stage 1 folder).

## B. First start (Gatekeeper)

The app is not signed, so macOS blocks it the first time (again, because this is a new download).

5. Open the `osx-arm64` folder and find the file named **Quartermaster** (no extension). **Right-click** it (or Ctrl+click) and choose **Open**. A warning appears; click **Open** again.
6. If nothing happens, or it says the file cannot be opened or "permission denied", open Terminal (Spotlight: press Cmd+Space, type Terminal) and paste these two lines, adjusting the folder if you put it elsewhere, then press Return after each:
   `xattr -dr com.apple.quarantine ~/Desktop/osx-arm64`
   `chmod +x ~/Desktop/osx-arm64/Quartermaster`
   Then try step 5 again.
   - A Terminal window opening next to the app is normal for this test build: it is a bare program, not a `.app` bundle yet. Leave Terminal open while testing.
   - If the "cannot verify / malware" warning still appears (macOS 15 Sequoia and newer no longer accept right-click > Open): start it from Terminal with `~/Desktop/osx-arm64/Quartermaster`. If it is still blocked, click **Done** on the warning, open **System Settings > Privacy & Security**, scroll down, click **Open Anyway** next to Quartermaster and confirm with your password.
7. The window should open with a grey menu bar (File, Edit, View, **Tools**) and, below it, a **toolbar** with buttons. Note anything that looks different from the Windows Quartermaster.

## C. Rules pack: new, open, save, remembered

8. **File > Open Module...** (Cmd+O), pick `Riverland-Demo.vmod`, keep "New game from module", click **OK**. The window title should show the module and version, with no rules name yet.
9. **File > Open Rules Pack...** and pick `Riverland-Demo.qrules`. The title should now also show the rules pack name. Check the Edit and Tools menus: some items may have become available.
10. **File > Save Rules Pack As...** The file dialog should suggest the pack's name. Save it as `my-rules.qrules` on the Desktop. Does the dialog look and behave like a normal Mac one?
11. **File > New Rules Pack...** A box asks for a name. Type `Test` and click OK. The title shows the new pack. (If the previous pack had unsaved changes, you are asked first; that is expected.)
12. Open the demo rules pack again (step 9), then quit with Cmd+Q. Start the app again (right-click > Open if asked), open the demo module and start a new game. The rules pack from step 9 should load by itself (it is remembered per module). Say if it did not.

## D. Right-click and Ctrl+click menus

13. **Right-click** (two-finger click) a counter. A menu appears next to the pointer. Does it open at the pointer? Does it show the counter's entries, for example Unit Sheet and Calculate (the formula entries do nothing yet, that is stage 2b)? Press Esc to close it.
14. Do the same with **Ctrl+click** on a counter. It should open the same menu and must not add to the selection.
15. Right-click empty map. A different menu appears (Zoom to Fit, Zoom 100%, Hide counters, Centre on selected and so on). Choose an entry, for example **Hide counters**, then press **H**: the counters come back.
16. In the counter menu choose **Unit Sheet** (the property sheet). A dialog with rows of names and values appears. Change a value, press OK, and check the hover popup (step 18) shows the new value. Press Cancel once: nothing changes.
17. Cmd+click: still adds to or removes from the selection as in stage 1.

## E. Hover popup and breakdown tips

18. Rest the pointer on a counter for a moment (without clicking). After about half a second a small popup with the counter's picture and properties (Side, Kind, Type, Ammo, Fuel...) appears next to the pointer, not under it. Is the text sharp? Does the font look right?
19. Move the pointer onto a **ledger line** in the popup (for example "Fuel 4"). A second, monospace tip appears beside the line with the breakdown of the number. Move off the line but stay on the popup: the tip goes, the popup stays. Move away from the popup: it closes.
20. Hover a counter near the edge of the window: the popup stays inside the window (it flips to the other side). This is expected to differ from Windows.
21. Click a counter while the popup is open, or scroll: the popup closes and the click goes to the map.
22. **Double-click** a stack of two or more counters: it expands into a fan, double-click again collapses it. (Drag one counter on top of another to make a stack first, if there is none.)

## F. Line of sight, hide, last moved

23. Press **L** or click **Line of sight** on the map toolbar. The button looks pressed. Drag from one hex to another with the trackpad: a red line appears with a label such as "6 hexes", and the status bar at the bottom shows the same. Does the line stay sharp on the Retina screen?
24. Press **Esc**: the line goes, the tool stays on. Draw another line, then right-click (or Ctrl+click): the line goes too and no menu opens. Press L again to turn the tool off.
25. Press **H**: all counters disappear (board only). Press H again: they return.
26. Click **Last moved** on the map toolbar, then move a counter: it gets a highlight. Undo with Cmd+Z: the highlight follows. Click Last moved again to turn it off.
27. **Centre on selected** and the **Zoom** menu of the map toolbar: do they work?

## G. Tools: dice and turn

28. **Tools > Dice**: it lists 1d6, 2d6, the module's dice, Custom and Multi-roll. Roll **1d6** from the menu. The status bar shows the roll for a few seconds.
29. Press **F5** (1d6) and **F6** (2d6). **On Mac keyboards the F keys often control brightness or volume by default, so you may need fn+F5 and fn+F6**, or switch "Use F1, F2, etc. keys as standard function keys" on in System Settings > Keyboard. Say what was needed. Do the menu shortcut hints match the keys?
30. Try **Custom** and **Multi-roll** from Tools > Dice: a dialog asks for the dice; roll something and cancel once.
31. **Tools > Turn**: Next, Previous and Set. The status bar shows "Turn N". Click the map first, then press **Cmd+Right** (the menu item shows "⌘→"): it should step the turn. It does nothing while you are typing in the log Search box. (With the demo rules pack, which has no phases, this works; with a phased pack the key does nothing until stage 2b.)
32. The **main toolbar** under the menu bar: Open Module, New Game, Load Setup, Open Game, Save Game, LOS, Undo, Redo, 1d6, 2d6, Dice, Next turn. Click a few. Are buttons that cannot work right now greyed (for example Undo before any move)?

## H. Log panel

33. **View > Log** (Cmd+Shift+L) shows or hides the panel at the bottom. It lists time, kind, turn/phase and text. Your dice rolls from section G should be in it, as "dice" lines; moves appear as "move" lines.
34. Click the kind buttons at the top of the log (dice, move, place, delete, formula, ledger, note, turn, Calculations, chat, system): each hides or shows that kind.
35. Type in the log's **Search** box: the list narrows. Clear it.
36. Select a few lines and click **Copy**, then paste somewhere (Notes, Cmd+V). Click **Export log...**, save as `.txt`, then again as `.html`; open both. Note: the file dialog has one filter for both types, the format follows the extension you type.
37. Double-click a "move" line: the map centres on that counter and selects it.
38. Drag the splitter above the log to change its height. Hide and show the log: the height should be remembered.

## I. Stage 1 things that must still work

39. Pan with two fingers, pinch zoom, Cmd+scroll zoom, drag a counter, Esc during a drag, Cmd+Z / Cmd+Y, Save Game As (Cmd+Shift+S) and reopening it. Quick check only.

## J. Graphics

40. While panning, zooming, drawing a line of sight line and hovering, did anything flicker, show black or white patches, or lag badly?

## What to send back

- Screenshots: the window with a right-click menu open (step 13), with the hover popup and breakdown tip (step 19), with a line of sight line (step 23) and with the log panel showing dice lines (step 33).
- For each numbered step: OK or not OK. For anything not OK: what you did, what you expected, what happened.
- Which keys you needed for F5 and F6 (step 29), and whether Ctrl+click behaved like right-click (step 14).
- Whether the rules pack was remembered after a restart (step 12).
- Whether the popups, menus and the file dialogs opened where you expected on the Mac (they were only tested without a real screen).
- Which Mac and macOS version you have (Apple menu > About This Mac).
- If the app crashed or showed an error text, a screenshot of it. Crash reports are in Console.app, under Crash Reports.
