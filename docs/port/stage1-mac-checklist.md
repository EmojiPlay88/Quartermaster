# Stage 1 checklist for the M1 Mac

This is a test of the first cross-platform build of Quartermaster. It shows the menu bar, the map, the counters and the counter palette. There are no rules, dice or log yet; they come in later stages. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 20 minutes. Use the Riverland demo module (`Riverland-Demo.vmod`, in `docs/wiki/demo` of the project; copy it to your Mac first, for example to the Desktop).

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you). Open the repository on github.com and click the **Actions** tab at the top.
2. Click the newest run named **cross-platform**. Wait until all three lines (windows-latest, macos-latest, ubuntu-latest) have a green tick. If one is red, click it and send me a screenshot of the red step; stop here.
3. Scroll to the bottom of the run page to the **Artifacts** box. Click **Quartermaster-Desktop-osx-arm64**. Your Mac downloads a zip into Downloads.
4. Double-click the zip in Finder to unzip it. You get a file `Quartermaster-Desktop-osx-arm64.tar.gz`. Double-click that too. You now have a folder `osx-arm64`. Move it to your Desktop.

## B. First start (Gatekeeper)

The app is not signed, so macOS blocks it the first time.

5. Open the `osx-arm64` folder and find the file named **Quartermaster** (no extension). **Right-click** it (or Ctrl+click) and choose **Open**. A warning appears; click **Open** again.
6. If nothing happens, or it says the file cannot be opened or "permission denied", open Terminal (Spotlight: press Cmd+Space, type Terminal) and paste these two lines, adjusting the folder if you put it elsewhere, then press Return after each:
   `xattr -dr com.apple.quarantine ~/Desktop/osx-arm64`
   `chmod +x ~/Desktop/osx-arm64/Quartermaster`
   Then try step 5 again.
7. A window should open with a grey menu bar (File, Edit, View). Note: did it open at all? Did a Dock icon appear? The Dock icon may be a plain default one at this stage.

## C. Look

8. Does the window look like the Windows Quartermaster: classic grey, small text, flat menu bar inside the window (not in the top bar of the screen)? Take a screenshot (Cmd+Shift+4, then drag over the window).
9. Is the text sharp and readable, not blurry? (Retina screens should look crisp.)

## D. Open the demo and start a game

10. Choose **File > Open Module...** (shortcut Cmd+O). Pick `Riverland-Demo.vmod`. A dialog "New game" appears.
11. Keep "New game from module" selected, click **OK**. After a moment the Riverland hex map should appear with blue and red counters, and a palette (search box, folder tree, counter pictures) on the right.
12. Check the map is sharp when you look closely at the hex numbers and counter text (Retina). Say if it looks blurry or pixelated.

## E. Move around

13. **Pan**: slide two fingers on the trackpad. The map should move with your fingers. Note which direction it moves and whether it feels natural.
14. **Zoom**: pinch with two fingers. The map should zoom around the point between your fingers. Also try holding Cmd and scrolling with two fingers: that should zoom too.
15. **View > Zoom to Fit** and **View > Zoom 100%** should work.
16. Move the pointer over the map. The bottom bar should show a map location and the zoom, for example 100%.

## F. Counters

17. Click one counter: it gets selected (a highlight). Click empty map: it deselects.
18. Hold **Cmd** and click two or three counters one after the other: all stay selected. Cmd+click a selected one: it deselects.
19. Drag on an empty spot to draw a selection box around several counters; they all become selected.
20. Drag a counter to another hex. It should snap into the hex. Try dragging several selected counters together.
21. Drag one counter on top of another: they should stack.
22. Start a drag and press **Esc** before letting go: the counter should jump back.
23. Drag a counter from the palette on the right onto the map. A new counter should appear where you drop it.

## G. Undo, save, reopen

24. Press **Cmd+Z**: the last move is undone. Press **Cmd+Y** (or Cmd+Shift+Z): it is redone. The Edit menu shows the same keys.
25. **File > Save Game As...** (Cmd+Shift+S). Save as `test1.qsave` on the Desktop. The window title should lose the `*` after the name.
26. Move one counter, then quit with Cmd+Q (or File > Exit). The app should ask "The current game has unsaved changes. Discard them?". Answer **No** first (the window stays), then try again and answer **Yes** (it closes).
27. Start the app again (right-click > Open if it asks), choose **File > Open Module...** for the demo (pick **New game from module** and OK), then **File > Open Game...** and pick `test1.qsave`. The counters should be where you saved them.
28. If you have a `.qsave` made by the Windows Quartermaster for the same module, open it here as well and say whether it works. If you saved `test1.qsave` here, you can also send it to a Windows machine and try to open it there.

## H. Graphics

29. While panning and zooming, did the map ever flicker, show black or white patches, or lag badly? (The test machines used for building cannot check the graphics chip; only your Mac can.)

## What to send back

- A screenshot of the main window after step 11 and one after step 20.
- For each numbered step: OK or not OK. For anything not OK: what you did, what you expected, what happened.
- Whether the map looked sharp (steps 9 and 12), and whether the app looked like the Windows one (step 8).
- Which Mac and macOS version you have (Apple menu > About This Mac).
- If the app crashed or showed an error text, a screenshot of it. Crash reports are in Console.app, under Crash Reports.
