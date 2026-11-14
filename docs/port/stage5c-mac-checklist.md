# Stage 5c checklist for the M1 Mac

> **Note (stage 6b-1):** the `Quartermaster-Desktop-osx-arm64` artifact named below no longer exists. Download the **Quartermaster-macOS** artifact instead and install the app from its `.dmg` (`docs/port/stage6b1-mac-checklist.md`, section A); where these steps use the `osx-arm64` folder, use `/Applications/Quartermaster.app` (the program is `/Applications/Quartermaster.app/Contents/MacOS/Quartermaster`).

This is a test of the eleventh cross-platform build of Quartermaster. On top of stage 5b it adds **live online play**: the launcher's **Online lobby ▶** (list, join, create a lobby, host a saved game), the host's **Game > Players…** window, the **side question** a joining player is asked, a **chat line** under the log, **Leave Online Game**, and **rejoining** after a lost connection. Turn files, Send Turn and Export Viewer Copy are not in this build (stage 5d). Button and message wording below was written from the code: check it on a running build. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 40 minutes and **a second computer** (the Windows PC with the WinForms app is the easiest) plus a relay both computers can reach. Use the Riverland demo module and its rules pack (`Riverland-Demo.vmod` and `Riverland-Demo.qrules` in `docs/wiki/demo` of the project; copy both to the Desktop of each computer). The pack has two sides, Northern League and Southern Compact. Use an invented lobby name (for example "TEST lobby") and a throw-away password; do not put anything private in the lobby, the chat or the player names. The relay address is the one in `docs/relay-setup.md` (your own relay, or a local one on the Windows PC); ask Claude if you do not have one running.

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you), open the **Actions** tab, newest run **cross-platform**, wait for three green ticks. Download **Quartermaster-Desktop-osx-arm64**, unzip it and the `.tar.gz`, move the `osx-arm64` folder to the Desktop (replace the old one). Start it as in the stage 4a checklist (right-click > Open, or the two `xattr` / `chmod` lines), **without** a module: just the program. In the launcher add `Riverland-Demo.vmod` and choose `Riverland-Demo.qrules` as its rules pack. On the Windows PC start the WinForms app with the same module and pack.

## B. The lobby and the relay certificate

2. Select Riverland in the launcher and click **Online lobby ▶**. Expected: the window "Online lobby — Riverland" with a **Relay:** box, an empty table (Lobby, Host, Free sides, Players) and the buttons **Join…**, **Create lobby…**, **Host a saved game…**, **Refresh**, **Close**; the state text asks for a relay address.
3. Type the relay address and press **Enter** (or **Refresh**). **If the relay uses a certificate Quartermaster has not seen before, a question appears** naming the relay and the certificate fingerprint, with **Yes** / **No**. Expected: it comes up over the lobby window, the window stays responsive, the question is not hidden behind another window (try Cmd+Tab while it is up). Answer **No** first: the state text shows the refusal and nothing is listed. Press **Refresh** and answer **Yes**: the state text reads "Connected to ... — 0 lobbies". Close the window, open it again: the relay is remembered and no question comes this time.
4. If you can, change the relay's certificate (or use a second relay at the same address): the question must now be a **warning** that the certificate changed, with **No** as the default button. Write down what you saw; skip this step if you cannot.

## C. Create a lobby and join it from the other machine

5. Click **Create lobby…**. Expected: the setup chooser with the lobby fields (lobby name, your name, password) and the **sides panel** (a tick box per side: the sides you will hold). Type the lobby name, your name, a password; tick **Northern League** only. With the password box empty the warning "Without a password, the relay can read this game." is shown; typing a password hides it. Click **OK**.
6. Expected: the game opens; the status bar shows **Online: CODE · 1 players**, a message says "Your lobby is open: TEST lobby (code ...)", the **Game** menu appears (**Players…** and **Leave Online Game** only), and a chat line ("Chat — press Enter to send") appears under the log.
7. On the Windows PC (WinForms app) open the Online lobby with the same relay. Expected: "TEST lobby" with a lock glyph, your name as host, **Southern Compact** as the free side, 1/2 players. Join it with a name and the password. Expected: on the PC the side question "Choose your side" lists Southern Compact; tick it and click **OK**. On the Mac the status bar now shows 2 players.
8. On the Mac open **Game > Players…**. Expected: the window "Players" with a row for you (**host (you)**, Northern League) and one for the guest (Southern Compact, connected). Close it.

## D. Chat

9. On the Mac type a line in the chat box and press **Enter** (or click **Send**). Expected: the line appears in the log on both computers with your name; the box empties. Type with **Cmd+A**, **Cmd+C**, **Cmd+V** in the chat box: they work as in any text box. Send a line from the PC: it appears on the Mac.
10. Move a counter on the Mac and click **Next Phase**: the PC follows. On the PC try **File > Save Game**: refused (a guest cannot save).

## E. Joining from the Mac against a WinForms host

11. Leave the Mac's game (**Game > Leave Online Game**, answer **Yes**: the Mac goes back to the launcher). On the PC create a lobby in the WinForms app (any sides, with a password), then on the Mac open **Online lobby ▶**, select that lobby and click **Join…** (or double-click the row). Expected: the join window shows "Hosted by ..." and the note "The host's copy runs the game."; enter your name and the password; the game opens as a guest; if the host left a side free you are asked for it (**Choose your side**, a tick box per side; **OK** with nothing ticked says to tick one and stays open). The host's moves appear on the Mac; the Mac's **Game > Players…** is disabled (hover: the sides are fixed) once you hold a side; **File > Save Game**, **New Game** and **Undo** are off. Open the **Rules Workbench**: it opens, is tinted and cannot be edited.

## F. Leaving and rejoining

12. As the guest choose **Game > Leave Online Game**. Expected: a question (**Yes** / **No**); **Yes** returns to the launcher; the host's Players window shows your seat as gone. Join the same lobby again: you get your old seat back without a side question (rejoin).
13. **Turn Wi-Fi off on the Mac for about 20 seconds, then on again** while joined as a guest. Expected: the status bar and a banner say the connection is lost or paused; no crash, no hang; when Wi-Fi is back the game resumes by itself, you still hold your seat and the host's later moves arrive. Write down how long the resume took.
14. Do the same on the host (Mac as host, the PC as guest): Wi-Fi off and on. Expected: the banner says the relay is lost while it is off; the guest sees "paused" / host disconnected; when it returns the game continues without recreating the lobby.
15. As the host choose **Game > Players…**, select a guest, **Free seat** (a question appears) and answer **Yes** if it asks; as a guest that holds no side, the seat is freed.

## G. Host a saved game

16. In a game that is saved (**File > Save Game As…**, `Mac online`), go back to the launcher, **Online lobby ▶ > Host a saved game…**, pick the save, tick your sides, set a password and click **OK**. Expected: the game opens with the saved position and the lobby is listed; a save from a different module is refused with "This save belongs to a different module".

## H. Closing

17. While online as the host press **Cmd+Q**. Expected: the question about unsaved changes if there are any, otherwise the app ends; the guest is told the host left (banner "Host disconnected — waiting for the host to return.") and no Quartermaster window is left on the Mac. Try the same as a guest: **Cmd+Q** ends the app without a question about the game being lost.
18. After quitting the host, start it again and open the same lobby list: the old lobby is gone from the list within a few seconds (the relay drops it when the host leaves).

Write down every step that did not behave as described, with a screenshot if you can. Especially: step 3 (the certificate question over the lobby window and whether Cmd+Tab can hide it), steps 7 and 11 (a WinForms host with a Mac guest and the other way round), steps 13 and 14 (Wi-Fi off and on) and step 17 (Cmd+Q while online).
