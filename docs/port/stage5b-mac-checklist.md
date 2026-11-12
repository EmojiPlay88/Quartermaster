# Stage 5b checklist for the M1 Mac

This is a test of the tenth cross-platform build of Quartermaster. On top of stage 5a it adds **hot-seat play**: two or more people share one computer. The new game dialog has a **Players page**, **Settings > Game > Players** edits the list in a running game, a **handover screen** ("Pass to Anna (Northern League)") hides every window between players, and a **Player** item in the status bar shows and switches who is at the screen. Button and message wording below was written from the code: check it on a running build. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 20 minutes. Use the Riverland demo module and its rules pack (`Riverland-Demo.vmod` and `Riverland-Demo.qrules` in `docs/wiki/demo` of the project; copy both to the Desktop). The pack has two sides, Northern League and Southern Compact; the players below are Anna and Ben.

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you), open the **Actions** tab, newest run **cross-platform**, wait for three green ticks. Download **Quartermaster-Desktop-osx-arm64**, unzip it and the `.tar.gz`, move the `osx-arm64` folder to the Desktop (replace the old one). Start it as in the stage 4a checklist (right-click > Open, or the two `xattr` / `chmod` lines), **without** a module: just the program. In the launcher add `Riverland-Demo.vmod` and choose `Riverland-Demo.qrules` as its rules pack.

## B. The Players page

2. **Play offline ▶ > New game…**. Expected: the "New game" dialog with the setups list, the difficulty, and the buttons **Next >**, **OK** and **Cancel**. Click **Next >**. Expected: the **Players** page with one row (a name box, a check box for each side, a Remove button), an **Add player** button, a hint line, and now **< Back**, **OK**, **Cancel**.
3. Click **Add player** and type **Anna** and **Ben** as the names; tick one side for each (Northern League for Anna, Southern Compact for Ben). Ticking a side takes it from the other player. Expected: the name boxes accept typing normally (letters appear as typed, Cmd+A / Cmd+C / Cmd+V work); **Remove** removes a row.
4. Untick all of Ben's sides and click **OK**. Expected: a warning titled "Players" with the reason (for example "Player 2 holds no side."), and the dialog stays open on the Players page. Tick Southern Compact for Ben again, click **< Back** and **Next >**: the entries are kept. Click **OK**.

## C. Handover

5. Expected: the game opens and the **handover screen** for Anna comes up: it fills the place of the main window, shows **Pass to Anna (Northern League)** and a **Continue** button, and nothing else. **Open Mission Control (three-finger swipe up) and press Cmd+Tab:** no other Quartermaster window may be listed or visible; the main window must be hidden, not just covered. Write down what you see.
6. Click **Continue**. Expected: the main window comes back with the map, the status bar shows **Player: Anna ▾** at the right, and the map and trees show Anna's maps and values. Windows you had open before (Ledger, Notes ...) are back too.
7. Press **Next Phase** until the phase is one of the Southern Compact's (South Supply). Expected: when Ben's phase starts, the handover screen **Pass to Ben (Southern Compact)** comes up and again nothing else is visible (repeat the Mission Control / Cmd+Tab check). Click **Continue**: Ben's view is shown.

## D. The status-bar switch

8. Click **Player: Ben ▾** in the status bar. Expected: a menu with **Anna (Northern League)** and **Ben (Southern Compact)**, Ben ticked; hovering the item shows "Who is at the screen. Choose a player to hand over.". Choose **Anna (Northern League)**: the handover screen for Anna comes first (a deliberate switch always asks for the screen); **Continue** shows Anna's view.
9. Open some windows (Ledger with Cmd+L, Notes, Overview), then switch to Ben as in step 8. Expected: while the handover screen is up none of them is visible; after Continue they show Ben's view only.

## E. Settings > Game > Players

10. **Tools > Settings > Game**. Expected: Difficulty and a **Players** box with Anna's and Ben's rows (the box is 440 wide; part of each row's Remove button may be cut off at the right edge: the scroll bar at the bottom of the box reaches it). Change a name or move a side, click **Apply**. Expected: the status bar and the menu show the change; if the current player's sides changed, a handover comes up.
11. Untick all sides of one player and click **Apply** or **OK**: a warning "Players" with the reason; the window stays open. Remove the second player and **Apply**: the game is solo again and **Player:** disappears from the status bar.

## F. Closing and undo

12. Add Ben again (Settings > Game > Players) and move a counter so the game has unsaved changes. Use **Next Phase** until a handover screen shows. Press **Cmd+Q** (or the red close button of the screen). Expected: the unsaved-changes question about the game (as on File > Exit); answer **Cancel**: the handover screen stays and nothing else is visible. Press Cmd+Q again and answer **No** (do not save): the app ends and no window is left.
13. Start again (New game, two players). Reach a handover with **Next Phase**, and while the screen is up use **Edit > Undo** if the menu is reachable (otherwise after Continue use **Step Back**): going back into the earlier player's phase must dismiss the handover screen and show that player's view, without a stray screen. Write down what you saw.
14. Save the game (**File > Save Game As…**, `Mac hot-seat`), go back to the launcher and open it from **Recent games**. Expected: the same players, and the current player at the screen after the first Continue.

Write down every step that did not behave as described, with a screenshot if you can. Especially: steps 5 and 7 (nothing but the handover screen in Mission Control and Cmd+Tab), step 12 (Cmd+Q on the handover screen) and whether the windows of step 9 ever show the other player's values.
