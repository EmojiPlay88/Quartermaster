# Stage 4a checklist for the M1 Mac

This is a test of the sixth cross-platform build of Quartermaster. On top of stage 3b (charts, documents, PDF viewer, Rule ref) it adds the **Rules Workbench**: the window where rules packs are edited (tables, formulas, properties, phases, alerts, visibility), the **rulebook search** and the **scan pane with text recognition (OCR)** that reads a table from a photo or scan. Settings come later (stage 4b). Button and menu labels below were written from the code: check the wording on a running build. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 45 minutes. Use **your own module and rules pack** (copy them to the Desktop first), or the Riverland demo module and its rules pack from the repository (`docs/wiki/demo`). The search and scan steps need a module that has a **PDF rulebook with real text** (not only pictures) and, for scanning, **a photo or scan of a table** (a screenshot of any table of numbers works; use something you may use freely, nothing leaves your Mac). For the multi-page TIFF step, any multi-page TIFF will do. If you have none, say so and skip that step.

What this build does not have yet, so you know what to expect:

- No Settings dialog (stage 4b).
- On the Mac, text recognition uses Tesseract and reads the bundled **English** unless you add more languages in Settings > OCR languages (stage 4c). On Windows the app uses the Windows recognizer instead, which is a little more accurate. Expect more mistakes in a Mac reading; fixing a cell by hand is part of the test.
- The Workbench is a floating window, not a child of the main window.

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you). Open the repository on github.com and click the **Actions** tab, then the newest run named **cross-platform**. Wait until all three lines have a green tick. If one is red, send me a screenshot of the red step and stop here.
2. At the bottom of the run page, click **Quartermaster-Desktop-osx-arm64** under Artifacts. Unzip the download, then the `.tar.gz` inside, and move the `osx-arm64` folder to your Desktop (replace the old one).

## B. First start (Gatekeeper)

3. Right-click the file **Quartermaster** in the folder and choose **Open**, then **Open** again. If nothing happens, or it says permission denied, open Terminal and run these two lines (adjust the folder if needed):
   `xattr -dr com.apple.quarantine ~/Desktop/osx-arm64`
   `chmod +x ~/Desktop/osx-arm64/Quartermaster`
   On macOS 15 and newer, if the warning still appears, start it from Terminal with `~/Desktop/osx-arm64/Quartermaster`, or use **System Settings > Privacy & Security > Open Anyway**.
4. **Libraries.** The text recognition libraries (`libtesseract`, `libleptonica`) and `libpdfium.dylib` load the first time you open a PDF or read a scan. If a box names one of them as blocked ("developer cannot be verified"), quit, run the `xattr -dr com.apple.quarantine ~/Desktop/osx-arm64` line again on the **folder**, and start again. Tell me which step you were on when it happened. **Expected: no prompt at all** after step 3.
5. Open your module (**File > Open Module...**, Cmd+O, "New game from module", **OK**) and its rules pack (**File > Open Rules Pack...**).

## C. Opening the Workbench

6. Open **Tools > Rules Workbench**. A window "Rules Workbench" opens beside the main window. Close it and press **Cmd+Shift+W**: the same window opens (the shortcut also brings an open one to the front). Close it again.
7. In the module tree, find the **Tables** and **Formulas** folders. Double-click a table: the Workbench opens on the **Tables** tab with that table selected. Double-click a formula: it opens on the **Formulas** tab with that formula selected.
8. Move and resize the Workbench, close it, open it again: it comes back in the same place and size. Drag the divider between the rulebook pane (left) and the tabs (right): both sides resize.

## D. Editing

9. **Tables tab.** Select a table. Click into a cell, type a number, press Return: the cell takes it. Press **Tab** repeatedly, past the last cell of a row, and from the **last cell of the grid**: focus should move on to the next control, not get stuck in the grid. Add a row and a column with the buttons, then remove them again.
10. **Formulas tab.** Edit a formula's expression, and type in the sample-values preview if it shows one: the result updates. Switch to another formula and back.
11. **Properties tab.** Select a property and change its sheet field by **typing a value that is not in the list** (a free value) and pressing Tab: the value stays.
12. **Phases tab.** Select a phase, change its instructions, add a checklist item and a warning, and remove them again.
13. **Alerts** and **Visibility** tabs. On Alerts, add an alert, change it, delete it. On Visibility, open the **Visibility** dropdown of a property (and the **Owner side** dropdown of a holder or map, if the module has one) and use the **arrow keys** and Return to pick an entry; the dropdown must follow the keys. Change a property's visibility, change an owner, and tick **Private dice** on a formula in the Formulas box. There is no add or delete on this tab.
14. **Save and reopen.** Use **File > Save Rules Pack** in the main window (the Workbench has no Save of its own), quit the app, start it again, open the same module and pack: all your edits from steps 9 to 13 are there. **Also open the saved pack in the Windows (WinForms) Quartermaster** if you have it at hand: it must load without an error and show the same edits.
15. Break something on purpose (an unfinished formula such as `1 +`): the **problems list** at the bottom names it; double-click the entry: the Workbench jumps to it. Fix it: the entry disappears.

## E. Searching the rulebook

16. With a module that has a PDF rulebook, wait for the status line to say "Rulebooks indexed: N searchable." Press **Cmd+Shift+F**: the search box is focused. Type a word the rulebook uses and press Return. A results panel opens below the rulebook pane: one row per rulebook, then one row per page with a snippet.
17. **Drag the splitter** between the rulebook pane and the results panel up and down: the panel resizes smoothly; the rulebook pane above it does not flicker or clip while you drag.
18. **Double-click a result** (or select it and press Return): the rulebook opens at that page and the searched word is **highlighted** on the page.
19. **Right-click a result**: a menu opens at the pointer with **Use as rule ref** (and, for a rulebook without text, **Scan page 1**). Select a formula, table or phase on the right first, then right-click a result and choose **Use as rule ref**: the item's rule ref now points at that page; click its rule-ref link: the rulebook opens there.

## F. Scanning and OCR

20. In the rulebook pane click **Scan this page**: the pane switches to a picture of the current page, with the **OCR panel** (Read text, Language, Merge, Split, Import into table...) beside it. Click **Back to PDF** to return.
21. Click **Open scan...** and choose a **photo or screenshot of a table** (PNG or JPEG). It shows in the picture pane. Drag a rectangle round the table cells. Click **Read text**: the status says it is reading, then the grid tab shows the cells it found.
22. Open a **multi-page TIFF** with **Open scan...**: it opens on page 1 with "of N" and the page buttons; flip pages with **<** and **>**. Photos taken with the phone turned sideways should be shown upright.
23. **Fix a cell**: click a wrong cell in the OCR grid, type the right number, press Return. Cells that came back empty are marked; type them in.
24. **Import into a new table**: click **Import into table...**, choose **New table**, accept the name, and confirm. The Workbench switches to the Tables tab with the new table, filled with your cells, and its rule ref points at the scan. Save the pack; the ref reads like `scan:photo.png`.
25. Open a scanned **PDF** with **Open scan...**: it joins the rulebook search (search for a word on its pages after a moment and open the hit: it shows in the scan pane at that page).
26. Zoom the picture with Cmd and the wheel, and drag-scroll: it stays sharp and responsive on a large photo.

## G. Things from earlier stages that must still work

27. Charts and PDF windows, Ledger (Cmd+L), Outliner, Inventory, Notes, Next Phase, Step Back, Save map as picture. Keep the Workbench open while using them.
28. Close the game or open another module while the Workbench is open: it closes with it, asking first if there are unsaved edits. No error appears.

## What to send back

- Screenshots: the Workbench on the Tables tab (step 8), the search results with a highlighted page (step 18), a scan with the OCR grid (step 21).
- For each numbered step: OK or not OK. For anything not OK: what you did, what you expected, what happened.
- Steps 9, 11, 17 and 19 in particular (Tab out of the grid, combo arrow keys, typing a free value, splitter drag, right-click on a result).
- Whether macOS blocked any library (step 4) and whether the `xattr` line on the folder was enough.
- How good the Tesseract reading was compared with the Windows one, if you can compare the same image.
- Which Mac and macOS version you have (Apple menu > About This Mac).
- If the app crashed or showed an error text, a screenshot of it. Crash reports are in Console.app, under Crash Reports.
