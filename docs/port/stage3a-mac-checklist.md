# Stage 3a checklist for the M1 Mac

This is a test of the fourth cross-platform build of Quartermaster. On top of stage 2b (phases, formulas, alert bar, module tree, pop-out log and phase panel) it adds: the **Ledger** window (tabs, counter pictures, editing values, CSV export, Edit tabs, Show in Ledger), counter pictures in the batch grid, the **Outliner**, **Inventory**, **Notes**, **Diagnostics**, the **Overview** window and **Save map as picture**. The rulebook (PDF) viewer, charts, search and scanning come later (stage 3b). You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 45 minutes. Use the Riverland demo module and its rules pack. Both are in `docs/wiki/demo` of the project: `Riverland-Demo.vmod` and `Riverland-Demo.qrules`. Copy both to your Mac first, for example to the Desktop. You can also download them directly:

- https://github.com/EmojiPlay88/Quartermaster/raw/master/docs/wiki/demo/Riverland-Demo.vmod
- https://github.com/EmojiPlay88/Quartermaster/raw/master/docs/wiki/demo/Riverland-Demo.qrules

(If the browser shows the `.qrules` file as text instead of saving it, use File > Save As in the browser, or right-click the link and choose Download Linked File.)

What the Riverland demo has and has not got, so you know what to expect:

- 14 units on one map (7 Northern League "NL", 7 Southern Compact "SC"), each with Ammo, Fuel and Readiness values, plus the depots (North Depot, South Depot) and "Global", which hold Stores and Weather. The Ledger's tabs are therefore "Units (14)", "Depots & Global (3)" and "Everything".
- There is only one map, so the Overview has only one map to follow.
- It has no ready-made setups, so always start with **New game from module**.
- It has no rulebook PDFs, charts or scanned pages, so nothing of stage 3b can be tried.
- Its numbers are whole numbers. Step 15 (comma decimals) therefore only checks that your Mac's number style is understood when you type.
- It has no hidden counters, so the "you only see what you may see" rule cannot be checked here. Skip it.

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you). Open the repository on github.com and click the **Actions** tab at the top.
2. Click the newest run named **cross-platform**. Wait until all three lines (windows-latest, macos-latest, ubuntu-latest) have a green tick. If one is red, click it and send me a screenshot of the red step; stop here.
3. Scroll to the bottom of the run page to the **Artifacts** box. Click **Quartermaster-Desktop-osx-arm64**. Your Mac downloads a zip into Downloads.
4. Double-click the zip in Finder to unzip it. You get a file `Quartermaster-Desktop-osx-arm64.tar.gz`. Double-click that too. You now have a folder `osx-arm64`. Move it to your Desktop (replace the stage 2b folder).

## B. First start (Gatekeeper)

The app is not signed, so macOS blocks it the first time (again, because this is a new download).

5. Open the `osx-arm64` folder and find the file named **Quartermaster** (no extension). **Right-click** it (or Ctrl+click) and choose **Open**. A warning appears; click **Open** again.
6. If nothing happens, or it says the file cannot be opened or "permission denied", open Terminal (Spotlight: press Cmd+Space, type Terminal) and paste these two lines, adjusting the folder if you put it elsewhere, then press Return after each:
   `xattr -dr com.apple.quarantine ~/Desktop/osx-arm64`
   `chmod +x ~/Desktop/osx-arm64/Quartermaster`
   Then try step 5 again.
   - A Terminal window opening next to the app is normal for this test build: it is a bare program, not a `.app` bundle yet. Leave Terminal open while testing.
   - If the "cannot verify / malware" warning still appears (macOS 15 Sequoia and newer no longer accept right-click > Open): start it from Terminal with `~/Desktop/osx-arm64/Quartermaster`. If it is still blocked, click **Done** on the warning, open **System Settings > Privacy & Security**, scroll down, click **Open Anyway** next to Quartermaster and confirm with your password.
7. The window should open with a menu bar and a toolbar. In the toolbar, after Save Game, you should now see **Diagnostics, Notes, Overview, Inventory**. Note anything that looks different from the Windows Quartermaster.

## C. Start a game and open the Ledger

8. **File > Open Module...** (Cmd+O), pick `Riverland-Demo.vmod`, keep "New game from module", click **OK**. Then **File > Open Rules Pack...** and pick `Riverland-Demo.qrules` (if it is not loaded already).
9. **View > Ledger** (shortcut shown as "⌘L"). A window called **Ledger** opens. Across the top: Filter, Side, Group by, **Show counters**, **Edit tabs...**, **Export CSV...**, **Show units without values**, **Refresh**. Under that the tabs **Units (14)**, **Depots & Global (3)**, **Everything** and a **+**. The grid shows Name, Kind, Ammo, Fuel, Readiness with a bold "Riverland Map" header row. If the toolbar wraps onto two lines because the window is narrow, make the window wider and note whether it then fits.
10. Close the Ledger, click one counter on the map to select it, and press **Cmd+L**. The Ledger opens with that counter's row selected and the status bar says "Shown in the Ledger." Now Cmd+click two more counters, press Cmd+L again: "3 counters shown in the Ledger."
11. **Right-click** a counter and choose **Show in Ledger**: the same thing. Click the **Depots & Global** tab, then right-click a counter again and choose Show in Ledger: the window must move back to the **Units** tab.
12. Try the tabs: click **Depots & Global** (North Depot, South Depot, Global with Stores and Weather columns), then **Everything**, then **Units** again. Type "Rifles" in **Filter**: only rifle units remain after a moment. Clear it. Change **Side** to one side and **Group by** to Side or None and check the rows follow.
13. Click a column title (for example **Fuel**): rows sort inside their group; click again to reverse. Drag the edge between two column titles to make a column wider. Hover a column title: the property's note appears as a tip.

## D. Counter pictures and hover

14. Click **Show counters**. Small counter pictures appear beside each name (they are drawn a moment after the rows appear; a dotted box may show briefly). **Move the pointer over a counter picture and rest it there**: a larger preview of that counter appears next to the pointer with the unit's name, and goes away when you leave. Check the preview is never half off the screen, also near the bottom edge. Switch **Show counters** off and on again.
    - **Double-click a counter picture**: the main window's map moves so that counter is in the middle. (Double-clicking the name or the Kind cell does the same.)
    - Drag the Ledger window to a second display if you have one, and check the pictures are not blurry on it.

## E. Editing a value (and number style)

15. In the Units tab double-click a **Fuel** value (or select it and press **F2**, or just start typing). A small edit box appears. Type a new whole number and press **Return**. A box asks for a **note** (the reason); type something and click **OK**. The value changes, and the main window's hover popup for that unit shows the new number. **Cmd+Z** in the main window undoes it. Press **Esc** while editing: nothing changes.
    - **Number style.** To find out whether your Mac uses a comma as the decimal sign: Apple menu > System Settings > General > Language & Region, look at **Number format** (the example reads "1.234,56" for a comma Mac and "1,234.56" for a dot Mac). On a comma Mac type `2,5` into a value and also `2.5` once: both must be accepted as two and a half. On a dot Mac type `2.5` and say so. Tell me what the cell shows afterwards.
16. Empty the edit box and press Return: it cancels. Try to edit a **Name** or **Kind** cell with F2 or by typing: on the Windows app nothing happens; in this build it starts editing the first value column instead (a known difference). Say what you see.
17. **Refresh** reloads. Make a change on the main window (for example move a counter, or run a formula): the Ledger updates by itself within a moment.

## F. CSV export, opened in Numbers

18. Click **Export CSV...**. A save box titled **Save As** opens with a name like "Units.csv" ready. (It has one file type only, "CSV files (*.csv)".) Save it to the Desktop.
19. Find the file on the Desktop, right-click > **Open With > Numbers**. Check that the columns are Name, Kind, Ammo, Fuel, Readiness, that the numbers are numbers, and that non-English letters (if any) are not garbled. Say if Numbers asked which character set to use.
20. Click Export CSV... again and Cancel the picker: nothing happens and no error appears.

## G. Edit tabs

21. Click **Edit tabs...**. A window **Edit Ledger tabs** opens (it can be made bigger). On the left a list of your own tabs (empty at first), with **Add, Remove, Move up, Move down**; on the right **Name**, **Unit filter**, **Columns** with tick boxes and **Up / Down**, **Include depots & Global**, **Notes**, then **OK / Cancel**.
22. Click **Add**. A tab "New tab" is added and selected. Name it "Low fuel". In **Unit filter** type `Fuel < 2`. Tick **Fuel** and **Readiness** in Columns; select Readiness and press **Up** so it comes first. Click **OK**.
23. A new tab with a star, "★ Low fuel (n)", appears at the end of the strip. Click it: only the units with fuel under 2 are listed (NL 6 Recon at the start), with the columns you chose in that order. Hover the tab: the Notes appear as a tip.
24. Open Edit tabs again and type a bad filter, such as `Fuel <`. A red message appears under the filter and OK is refused. Correct it, click OK. Click the **+** on the tab strip: the same window opens with a fresh "New tab" ready; click **Cancel** and check no tab was added.
25. Quit the app (Cmd+Q) and start it again with the same module: the Ledger remembers its tab, whether Show counters was on, and the "Low fuel" tab.

## H. Outliner

26. **View > Outliner**. A column appears at the right of the maps, with a **Group** drop-down, a **Pop out** button, a "Now" box at the top and a tree: "Riverland Map" with its units; columns Units, Ammo, Fuel, Readiness. Click the **+** in front of the map to expand it. Counter pictures sit beside the units.
27. Change **Group** to **By side** and back. Hover a number: a tip shows how it is made up. Hover a column title: its note appears. Double-click a unit: the map centres on it.
28. Click **Pop out**: the outliner becomes its own window, the button says **Dock**. **Drag it to a second display** if you have one, make it taller, then click **Dock** (or close the window): it comes back to the right of the maps. Pop out again: it reopens where you left it. If you unplug the second display in between, it must come back on the main display.
29. Know before you test: the Group choice and whether the outliner is shown are not remembered after you quit (they wait for the Settings window).

## I. Inventory, Notes, Diagnostics

30. **Tools > Inventory** (or the toolbar button). A window with **Group by**, a second drop-down, **Filter**, and a list with Group, Count, Total. Choose different groupings, type in the Filter. Hover a Total: a tip. Double-click a line: it steps the map through the pieces of that group.
31. **Tools > Notes**. A plain text box. Type a few lines, wait a second, close the window, and open it again: the text is still there. Save the game (Cmd+S), quit, reopen the save and open Notes: the text comes back.
32. **View > Diagnostics** (shortcut shown as "⌘D") or press **Cmd+D**. A window with Severity, Category, Message, Context, a **Show** drop-down and **Copy all**. It may be empty after a clean start. If the status bar at the bottom shows "n warning(s) - click here to view diagnostics", click it and the window opens; paste the text of **Copy all** into your notes.
33. Press Cmd+D with the window open: it comes to the front, no second window.

## J. Overview and Save map as picture

34. **View > Overview** (or the toolbar button). A small window (about 260 by 240) opens near the right edge of the main window. It shows the whole map small with a red rectangle where the main view is. **Click** somewhere on it: the main map centres there and the red rectangle follows. **Drag** inside the overview: the map follows continuously. Zoom or pan the main map: the rectangle follows. (In this build the Overview is always a separate window; it cannot dock into the tabs.)
35. Move the Overview to the second display, close it, open it again: it reopens in the same place.
36. On the map toolbar click **Save map as picture...** (or right-click an empty part of the map and pick it). A box titled with the map name offers **Current view** and **Whole map at 25% / 50% / 100%**. Choose **Whole map at 50%**, click **OK**, and in the save box pick the **Desktop**, keep the name, save.
37. A progress box appears (it may be too quick to notice at 50%). Try again with **100%** and click **Cancel** on the progress box while it works: no error appears and no half-written picture is left.
38. On the Desktop double-click the saved PNG: it opens in **Preview**. Is the whole map there, sharp, with the counters on it? Do the same for **Current view**: it should look like what is on screen.

## K. Counter pictures in the batch grid

39. Start a new game with **Semi-automatic** difficulty (File > New Game) and press **Start game ▶**. A batch grid for Fuel supply opens. Its **Unit** column now shows a small counter picture beside each name (rows are taller than before). Rest the pointer on a picture: the larger preview appears. Does anything flicker while the pictures arrive?

## L. Things from earlier stages that must still work

40. Phase strip and Next Phase (Cmd+Right), Step Back (Cmd+Left), formula window (right-click a counter > Calculate), hover popup with breakdown tip, alert chips (click **Low fuel**: it selects a counter; any alert that names a depot or Global opens the Ledger on that row), Save Game As (Cmd+Shift+S), dice (F5 / F6), pop-out log and phase panel. Keep the Ledger and the Outliner open while doing these and check nothing looks stale.

## M. Graphics

41. While the Ledger scrolls (drag its scroll bar, use the trackpad), while counter pictures load, and while you move the windows, did anything flicker, show black or white patches, or lag? Does the Ledger list scroll smoothly with Show counters on?

## What to send back

- Screenshots: the Ledger with Show counters on (step 14), the counter preview next to the pointer (step 14), the Edit Ledger tabs window (step 22), the CSV open in Numbers (step 19), the Outliner expanded (step 26), the Overview beside the map (step 34), and the saved map open in Preview (step 38).
- For each numbered step: OK or not OK. For anything not OK: what you did, what you expected, what happened.
- Whether **Cmd+L** worked with and without a counter selected (steps 9 and 10), whether **Cmd+D** opened Diagnostics (step 32), and what your Mac's number format is and what happened when you typed `2,5` or `2.5` (step 15).
- Whether the windows (Ledger, Outliner, Overview, Inventory, Notes, Diagnostics) opened where you expected, and whether the outliner and Overview came back on the right display (steps 28 and 35).
- Which Mac and macOS version you have (Apple menu > About This Mac).
- If the app crashed or showed an error text, a screenshot of it. Crash reports are in Console.app, under Crash Reports.
