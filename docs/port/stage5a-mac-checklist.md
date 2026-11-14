# Stage 5a checklist for the M1 Mac

> **Note (stage 6b-1):** the `Quartermaster-Desktop-osx-arm64` artifact named below no longer exists. Download the **Quartermaster-macOS** artifact instead and install the app from its `.dmg` (`docs/port/stage6b1-mac-checklist.md`, section A); where these steps use the `osx-arm64` folder, use `/Applications/Quartermaster.app` (the program is `/Applications/Quartermaster.app/Contents/MacOS/Quartermaster`).

This is a test of the ninth cross-platform build of Quartermaster. On top of stage 4c it adds the **app lifecycle**: the app now starts with a **launcher** (your modules, Play offline), **File > Back to launcher** returns there from a game, **Help > Interface Tour** walks through the main window, **Help > About**, and the **update screens** (only with a test feed, see part F; since stage 6a Update now really installs, which `docs/port/stage6a-mac-checklist.md` tests). Button and message wording below was written from the code: check it on a running build. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 25 minutes. Use the Riverland demo module and its rules pack (`Riverland-Demo.vmod` and `Riverland-Demo.qrules` in `docs/wiki/demo` of the project; copy both to the Desktop). Part F uses Terminal for two lines.

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you), open the **Actions** tab, newest run **cross-platform**, wait for three green ticks. Download **Quartermaster-Desktop-osx-arm64**, unzip it and the `.tar.gz`, move the `osx-arm64` folder to the Desktop (replace the old one). Start it as in the stage 4a checklist (right-click > Open, or the two `xattr` / `chmod` lines), **without** a module: just the program.

## B. The launcher

2. Expected: a window titled **Quartermaster** with a list (columns Module, Version, File, Last played), the line **Rules pack: (none)**, the buttons **Add module…**, **Remove**, **Online lobby ▶** (grey; hovering it shows "Online play comes with stage 5c.") and **Play offline ▶**, and at the bottom **Quartermaster** and the version. There is **no** "Check for updates" link and no yellow bar (they only exist with the test feed of part G). The list may already hold modules if you used the launcher before; that is fine.
3. Click **Add module…** and pick `Riverland-Demo.vmod`. Expected: a row **Riverland**, version 1.0, file `Riverland-Demo.vmod`; for a moment a grey progress text while the module is checked. While it is checked **Play offline ▶** is grey with the tooltip "This module is still being checked; try again in a moment."; afterwards it is enabled.
4. With Riverland selected click **change…** next to Rules pack and pick `Riverland-Demo.qrules`. Expected: **Rules pack: Riverland-Demo.qrules** and a **use (none)** link. (Leave the pack chosen.)

## C. Play offline and a new game

5. Click **Play offline ▶** (or double-click the row). Expected: a window **Play offline – Riverland** with **Continue** and the line "Continue: (no autosave yet)" (Continue is grey), a **Recent games** list (Saved column), and the buttons **New game…**, **Open saved game…**, **Open turn file…** (grey; tooltip "Turn files come with stage 5d."), **Open selected** (grey until a row is selected) and **Back**.
6. Click **Back**: you are in the launcher again. Click **Play offline ▶** again, then **New game…**. Expected: the familiar "New game" dialog; OK opens the game window with the Riverland map and the rules pack loaded (phase strip and phase panel visible). The launcher and the Play offline window are gone.
7. If this is the first game you ever opened in the Desktop build, about half a second later the **Interface Tour** balloon appears by itself (step 8 covers it). If it does not appear, that is fine when you have done the tour before; say which.

## D. The Interface Tour and About

8. Choose **Help > Interface Tour** (if the tour is not already open). Expected: a small pale-yellow balloon with a black border and a pointer toward the module tree, the title "Module tree  (1 of 7)" (the number can be lower if you hid panels), a short text and the buttons **Skip tour**, **< Back** (grey on the first step) and **Next >**. **Look closely at the pointer and the balloon's corners:** around the pointer there must be no black or white box; you see the map or panel behind it. Write down what you see (a screenshot helps).
9. Click **Next >** through all steps. The balloon moves to each target (map, phase strip, phase panel, alert bar, View menu, Edit menu ...) and the pointer points at it. On the last step the button reads **Done**; click it: the balloon closes. Open the tour again and press **Esc**: it closes (skipped).
10. Choose **Help > About**. Expected: a box "About Quartermaster" with "Quartermaster" and the version, and "A VASSAL module reader and engine.". Help has no **Check for updates…** item (it only appears with the test feed).

## E. Back to launcher and Continue

11. Use **Next Phase** once (the button in the phase panel, or **Cmd+Right**): an autosave is written at the phase change. Move a counter so the game has unsaved changes.
12. Open the **File** menu. Expected: **Back to launcher** just above **Exit**, without a shortcut; the other items show Mac shortcut signs (for example **Save Game ⌘S**, **Open Module… ⌘O**), never "Ctrl". Choose **Back to launcher**. Expected: the unsaved-changes question first (as on Exit); answer **No** (do not save). The game window closes and the launcher opens with **Riverland selected** and **Last played** filled with today's date and time.
13. Click **Play offline ▶**. Expected: **Continue** is now enabled with "Continue: autosave <date time> (turn …, <phase>)". Click **Continue**: the game opens where the autosave was taken (after the phase change, without the moved counter). The tour does not start by itself again.
14. **File > Save Game As…**, save as `Mac test` on the Desktop. **File > Back to launcher**, **Play offline ▶**: `Mac test.qsave` is in **Recent games** with its time. Select it and click **Open selected**: it opens. Back to the launcher once more and quit the app by closing the launcher window: the app ends (no window left, the Terminal program stops).
15. Start the app from Terminal with the module: `~/Desktop/osx-arm64/Quartermaster ~/Desktop/Riverland-Demo.vmod`. Expected: no launcher; the "New game" dialog appears and OK opens the game directly. **File > Back to launcher** is there and opens the launcher with Riverland selected.

## F. Update screens with a test feed (Terminal)

These steps use a scratch folder, so your real settings are not touched. The test feed is a folder with an invented release 9.9.9 (TEST notes, a dummy download that is not a Quartermaster build); nothing is downloaded from the internet.

16. Quit the app. In Terminal run these two lines (the first writes the scratch folder, the module list and the test feed, and draws a picture `bar.png` of the launcher with the update bar; the second starts the app on that folder and feed):
    `mkdir -p ~/qm-5a-test && QUARTERMASTER_LAYOUT_ROOT=~/qm-5a-test ~/Desktop/osx-arm64/Quartermaster ~/Desktop/Riverland-Demo.vmod --screenshot-update-bar ~/qm-5a-test/bar.png`
    `QUARTERMASTER_LAYOUT_ROOT=~/qm-5a-test QUARTERMASTER_UPDATE_FEED=~/qm-5a-test/test-update-feed ~/Desktop/osx-arm64/Quartermaster`
    Expected after the first line: no window opens, and `bar.png` in the `qm-5a-test` folder in your home folder shows the launcher with a yellow bar. After the second line: the launcher with Riverland, and at the bottom a **Check for updates** link next to the version.
17. Click **Check for updates**. Expected: a yellow bar at the top, "Quartermaster 9.9.9 is available." with the links **What's new**, **Update now**, **Later** and **Skip this version**.
18. Click **What's new**. Expected: a window "What's new in Quartermaster 9.9.9" with "TEST release notes for the update bar screenshot." and two TEST lines, a link **Open the release page** and **Close**. Close it.
19. Click **Update now**. Expected: no download starts; instead a question "This copy of Quartermaster can't update itself; download the new version from the release page. Open the download page?" with **Yes** and **No**. Answer **No** (Yes would open the TEST release page address in your browser, which leads nowhere). This is right here: the plain `osx-arm64` publish folder is not inside a `Quartermaster.app`, so it cannot update itself. Nothing on your Mac changes (installing a real release from a `.app` is tested by `docs/port/stage6a-mac-checklist.md`).
20. Click **Later**: the bar goes away. Start a game (Play offline ▶ > New game…). **Help > Check for updates…** is now in the Help menu; choose it. Expected: "Close the game and use the launcher to update." and "Go back to the launcher now (File → Back to launcher)?". Answer **Yes**: you are back in the launcher and it checks again (the bar comes back).
21. Click **Skip this version**: the bar goes away. Quit the app and run the second line of step 16 again, click **Check for updates**: you are asked "Quartermaster 9.9.9 is available, but you chose to skip it. Offer it again?". Answer **No**: no bar. (Yes would bring the bar back.)
22. Quit the app and delete the `qm-5a-test` folder in your home folder.

Write down every step that did not behave as described, with a screenshot if you can. Especially: step 8 (the tour pointer and corners), step 12 (shortcut signs in the File menu) and whether quitting the launcher in step 14 really ended the app.
