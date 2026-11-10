# Stage 3b checklist for the M1 Mac

This is a test of the fifth cross-platform build of Quartermaster. On top of stage 3a (Ledger, Outliner, Inventory, Notes, Diagnostics, Overview, Save map as picture) it adds the **document and chart windows**: charts open in their own window, rulebook PDFs open one page at a time with their own page bar, text documents show their text, HTML documents open in your web browser, and the **Rule ref** buttons of the formula window and the phase panel now open the referenced page. Searching a rulebook, scanning pages and OCR come later (stage 4). You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 30 minutes. The Riverland demo module has no rulebooks or charts, so this test needs **a module of your own that has at least one chart, one PDF rulebook, and if possible one text or HTML document**, plus its rules pack if it has Rule ref links. Copy them to your Mac first, for example to the Desktop. Please use a module you may use freely; nothing of it leaves your Mac.

What this build does not have yet, so you know what to expect:

- No search box, no "scan" pane and no OCR. The document window has only the page bar.
- PDFs show one page at a time (no continuous scrolling through the whole book).
- Windows of charts and documents float: they are separate windows owned by the main window, not tabs.

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you). Open the repository on github.com and click the **Actions** tab at the top.
2. Click the newest run named **cross-platform**. Wait until all three lines (windows-latest, macos-latest, ubuntu-latest) have a green tick. If one is red, click it and send me a screenshot of the red step; stop here. (A red step called "Publish macOS" that says a file is MISSING means a PDF or OCR library did not make it into the build; send me that text.)
3. Scroll to the bottom of the run page to the **Artifacts** box. Click **Quartermaster-Desktop-osx-arm64**. Your Mac downloads a zip into Downloads.
4. Double-click the zip in Finder to unzip it. You get a file `Quartermaster-Desktop-osx-arm64.tar.gz`. Double-click that too. You now have a folder `osx-arm64`. Move it to your Desktop (replace the stage 3a folder).

## B. First start (Gatekeeper)

The app is not signed, so macOS blocks it the first time (again, because this is a new download). This build also contains new libraries: `libpdfium.dylib` (draws PDF pages), and `libtesseract` and `libleptonica` (the text-recognition libraries, not used yet but shipped). macOS may block each of them separately the first time they are loaded.

5. Open the `osx-arm64` folder and find the file named **Quartermaster** (no extension). **Right-click** it (or Ctrl+click) and choose **Open**. A warning appears; click **Open** again.
6. If nothing happens, or it says the file cannot be opened or "permission denied", open Terminal (Spotlight: press Cmd+Space, type Terminal) and paste these two lines, adjusting the folder if you put it elsewhere, then press Return after each:
   `xattr -dr com.apple.quarantine ~/Desktop/osx-arm64`
   `chmod +x ~/Desktop/osx-arm64/Quartermaster`
   The first line also clears the block on every library in the folder, including `libpdfium.dylib` and the tesseract and leptonica `.dylib` files. Then try step 5 again.
   - A Terminal window opening next to the app is normal for this test build: it is a bare program, not a `.app` bundle yet. Leave Terminal open while testing.
   - If the "cannot verify / malware" warning still appears (macOS 15 Sequoia and newer no longer accept right-click > Open): start it from Terminal with `~/Desktop/osx-arm64/Quartermaster`. If it is still blocked, click **Done** on the warning, open **System Settings > Privacy & Security**, scroll down, click **Open Anyway** next to Quartermaster and confirm with your password.
7. **Libraries blocked.** If, when you open the first PDF (step 14), a box says the document could not be opened and mentions `libpdfium` or "cannot be opened because the developer cannot be verified", then macOS blocked that library. Quit the app and run the `xattr -dr com.apple.quarantine ~/Desktop/osx-arm64` line from step 6 again (it must be run on the **folder**, not only on the Quartermaster file), then start the app again. If a similar box names `libtesseract` or `libleptonica` at any time, do the same and tell me when it appeared. Each library can also be allowed one by one in **System Settings > Privacy & Security > Open Anyway**.
8. The window should open with a menu bar and a toolbar. Note anything that looks different from the Windows Quartermaster.

## C. Charts

9. **File > Open Module...** (Cmd+O), pick your module, keep "New game from module", click **OK**. If you have its rules pack, **File > Open Rules Pack...** too.
10. If the module tree is not showing, turn it on in the **View** menu. Find a **chart** in the tree (charts are listed with the module's windows) and double-click it. A separate window opens with the chart's name as title and the picture on a grey background. Across the top: **Fit** and **100%**.
11. **Zoom and pan.** Use the trackpad scroll or a mouse wheel over the picture: it zooms in and out around the pointer. Drag with the left mouse button (or the trackpad): the picture pans, and the pointer shows a four-arrow cursor while dragging. Click **Fit**: the whole picture fits. Click **100%**: one picture pixel is one point on screen. On the Retina screen the text on the chart should look sharp at 100%, not blurry. Zoom out far: it should stay smooth, not jagged.
12. Close the chart window and open it again: it reopens in the same place and size. Move it to a second display if you have one and close and reopen it. Open a second chart (if the module has one): it gets its own window.
13. Double-click the same chart in the tree while its window is open: no second window; the existing one comes to the front.

## D. PDF documents

14. Find a **PDF document** in the module tree (documents are listed with the module's windows) and double-click it. A window opens with a bar at the top: **Page:** [number] **of N**, **<**, **>**, **Zoom out**, **Zoom in**, **Fit width**, **Fit page**, and the zoom in percent. The first page is drawn below. It may be drawn a moment after the window appears.
15. **Page through.** Click **>** and **<**; type a page number in the box and press Return; press the Page Down and Page Up keys (Home and End jump to the first and last page). Only one page is shown at a time. The **<** button is greyed on page 1 and **>** on the last page.
16. **Zoom.** Click **Zoom in** several times and **Zoom out**: the percent changes, the page gets bigger or smaller, and scroll bars appear when it is bigger than the window. Cmd and the wheel over the page zoom too (a plain wheel scrolls the page), and Cmd+plus, Cmd+minus and Cmd+0 (Fit width) work from the keyboard.
17. Click **Fit width**: the page is as wide as the window. Make the window wider or narrower: the page follows. Click **Fit page**: the whole page is visible.
18. **Retina sharpness.** Zoom to about 150% and look at small text on the page. It should be crisp, like Preview, not soft. Zoom in more: it should stay sharp (the page is drawn again at the new size after a short moment; a short blur while it redraws is fine, a blur that stays is not). Say what you see.
19. Open a second PDF (or the same module's other document): it gets its own window. Close both and open one again: it reopens at the same place and size.
20. Double-click the same document in the tree again while its window is open: the window comes to the front, it does not open twice.

## E. Text and HTML documents

21. If the module has a **text document**, open it from the tree: the text is shown in a plain grey-bordered box with a fixed-width font, with scroll bars; you cannot change it, but you can select and copy.
22. If the module has an **HTML document**, open it from the tree: the window says "This document opens in your web browser." with an **Open in browser** button, and your browser (Safari or your default one) opens the page by itself. Click **Open in browser** again: it opens again. Does the page look as expected in the browser?
23. If a document is damaged or not a real PDF (you can try renaming a text file to `.pdf` inside a copy of a module, only if you know how), the window shows "This document could not be opened: ..." instead of crashing. Skip this if it is too fiddly.

## F. Rule ref

24. Open **Calculate** on a unit that has a formula with a rule reference (right-click a counter > Calculate), in Semi-automatic or Hardcore mode. The window has a **Rule ref** button. If the formula names a rule, the button is enabled; click it: the rulebook window opens (or comes to the front if it was open) **at the referenced page**. Check the page is the right one.
25. Start the game (**Start game**) and look at the **phase panel** (docked at the right, or popped out). Each phase that has a rule reference shows a rule link. Click it: the rulebook opens at that page. Do the same from a second phase: the same window jumps to that page.
26. If a reference points to a document your module does not have (the rules pack and module do not match), an error box says that the reference points to a document that is not in this module. This is expected for a wrong pairing; it is not a crash.

## G. Things from earlier stages that must still work

27. Ledger (Cmd+L), Outliner, Inventory, Notes, Overview, Save map as picture, Next Phase (Cmd+Right), Step Back (Cmd+Left), the formula window, the alert bar. Keep a chart and a PDF window open while using them and check nothing looks stale or jumps.
28. Save the game (Cmd+S), quit (Cmd+Q) and start again with the same module: the open chart and document windows are not reopened by themselves (known), but their positions are remembered when you open them again.
29. **Close the game or open another module** while a chart and a PDF window are open: both windows close with it. No error appears.

## H. Graphics

30. While you page through a PDF fast (hold **>** or press Page Down repeatedly), while you zoom, and while you resize the window, did anything flicker, show black or white patches, show a half-drawn page, or lag? Did the app stay responsive? On a long PDF (hundreds of pages) does opening and paging stay quick?

## What to send back

- Screenshots: a chart window at Fit (step 10), a PDF page at 150% (step 18, so I can judge the sharpness), the HTML document window (step 22), the Rule ref opening a page (step 24).
- For each numbered step: OK or not OK. For anything not OK: what you did, what you expected, what happened.
- Whether macOS blocked any library (`libpdfium`, `libtesseract`, `libleptonica`) and which step you were on (step 7), and whether the `xattr` line on the folder was enough.
- How sharp the PDF text looks on the Retina screen at 100% and 150% (step 18), compared with Preview.
- Which Mac and macOS version you have (Apple menu > About This Mac).
- If the app crashed or showed an error text, a screenshot of it. Crash reports are in Console.app, under Crash Reports.
