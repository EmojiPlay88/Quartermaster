# Stage 4b checklist for the M1 Mac

This is a test of the seventh cross-platform build of Quartermaster. On top of stage 4a it adds **Settings** (Tools > Settings...), which applies at once to the open windows, **File > Open Autosave...**, **physical dice entry**, and remembers the difficulty and the Outliner layout between runs. Button and menu labels below were written from the code: check the wording on a running build. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 30 minutes. Use your own module (or the Riverland demo in `docs/wiki/demo`) with its rules pack. Back up your settings first if you care about them: the app writes the same `settings.json` as the Windows app.

What this build does not have yet:

- Settings > Game shows Difficulty only; "Players: comes with multiplayer (stage 5)" sits where the Players box will be.
- No extra OCR languages (stage 4c); no launcher or update check (stage 5), although their two General checkboxes are stored.
- The counter palette shows only in game master mode (Settings > General).

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you), open the **Actions** tab, newest run **cross-platform**, wait for three green ticks. Download **Quartermaster-Desktop-osx-arm64**, unzip it and the `.tar.gz`, move the `osx-arm64` folder to the Desktop (replace the old one). Start it as in the stage 4a checklist (right-click > Open, or the two `xattr` / `chmod` lines).
2. Open your module (File > Open Module..., "New game from module", OK) and its rules pack.

## B. Opening Settings

3. Press **Cmd+,** (Command and comma): the Settings window opens with the pages General, Game, Display, Map, Dice & Log, Toolbar, Keyboard, Folders. Close it with Cancel. Open **Tools > Settings...**: the menu shows **⌘,** next to it. It must not read "OemComma" or "Ctrl".
4. Switch between the pages **back and forth several times** (click each tab, then in a different order, ten times or more). Expected: every page keeps its values and nothing freezes, flickers or goes blank.

## C. Number boxes

5. On the Map page, click into a number box (for example the wheel zoom step or the hover delay), type a number that is **out of range** (far too big, then 0, then a negative one) and **leave the box** (Tab or click elsewhere). Expected: the box settles on a value within its limits, no error box, no crash. Press OK: the saved value is within the limits too.

## D. Keyboard page and capture

6. Open the **Keyboard** page, select a command (for example "Save Game As") and click **Set...**. The capture window opens. Press **Cmd+Shift+K**: it shows **⌘⇧K** (not "Ctrl+Shift+K" or "Meta"). 
7. In the capture window, press **Tab**, **Return** and the **arrow keys**, each alone. Expected: each is taken as the shortcut (or refused with the reserved-key text where the app reserves it), none of them moves focus out of the window or closes it by itself.
8. Press the **Control key (⌃) alone**, then ⌃ with a letter. Expected: nothing happens (the key is swallowed); Cmd with a letter works.
9. Accept ⌘⇧K, press OK. Open the menu that holds the command: its menu text shows **⌘⇧K**. Press the new keys: the command runs. Press the old keys: nothing happens. Hover the toolbar button of a command you changed (for example 1d6): the tooltip shows the new key, e.g. "1d6 (⌘…)".
10. Set the same keys on two commands: OK / Apply refuse with a message and the Keyboard page is selected. Use **Clear** and **Default** on a command and check the text changes.

## E. Colours

11. On the Display or Map page, click the **colour swatch**. The small colour window opens with 48 swatches and a box for #RRGGBB. Pick a swatch, then type a hex value and OK: the swatch on the page shows the colour, and after OK in Settings the map's highlight (or the setting you changed) uses it at once.

## F. Live apply

12. With a map open, change the wheel zoom step, hover delay, snap and label size on the Map page, press **Apply** without closing: every open map tab takes them at once. Toggle the alert bar, the log panel and the toolbar buttons (Toolbar page): they appear and disappear at once.
13. Tick **Game master mode** on General: the counter palette column shows when a game is open; untick: it goes.
14. Game page: change **Difficulty** with a game open; the phase panel follows. Quit and start a new game: the new game offers the difficulty you last chose.

## G. Open Autosave

15. Play until a phase boundary (or wait for the autosave interval you set on the Folders page), then choose **File > Open Autosave...**. The macOS file panel must **start in the autosave folder** (the folder set on the Folders page, or the default), listing the autosaves. Open one: the game loads. Change the folder on the Folders page and check the panel starts there next time.

## H. Physical dice

16. On Dice & Log tick **Physical dice**, press OK. Roll **1d6** (F5 or the toolbar): the app asks for the die value; type a number from 1 to 6 and OK. Type a letter, 0 and 7: each time it asks again with "Enter a whole number from 1 to 6." Press Cancel on a 2d6 roll after entering the first die: nothing is logged. Roll a multi-roll: it asks for every die.

## I. Settings file and the Windows app

17. Quit and start again: every setting is still as you left it (including the Outliner grouping and visibility, and the remembered last roll).
18. If you have the Windows app at hand, copy `settings.json` over and open it: it starts without an error and shows the same values. Open a `settings.json` written by Windows in the Mac build too.

Write down every step that did not behave as described, with a screenshot if you can.
