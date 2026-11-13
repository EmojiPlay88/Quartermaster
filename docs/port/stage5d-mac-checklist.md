# Stage 5d checklist for the M1 Mac (and a Linux desktop)

This is a test of the twelfth cross-platform build of Quartermaster. On top of stage 5c it adds **turn files** (play by email): **Game > Send Turn…** with the relay **mailbox**, **File > Open Turn File…** and the launcher's **Open turn file…**, the **replay bar** a received turn plays in, the **"New turn waiting"** notice, and **File > Export Viewer Copy…** for an online guest. On the Mac the mailbox password now goes to the **Keychain**; on Linux to the **Secret Service** (GNOME Keyring, KWallet) through libsecret. Button and message wording below was written from the code: check it on a running build. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 45 minutes, **the Windows PC with the WinForms app** (for the turns that cross between the two apps) and a relay both computers can reach for the mailbox steps. Use the Riverland demo module and its rules pack (`Riverland-Demo.vmod` and `Riverland-Demo.qrules` in `docs/wiki/demo` of the project; copy both to the Desktop of each computer). The pack has two sides, Northern League and Southern Compact. Use invented player names (for example "Anna" and "Ben") and a throw-away mailbox password; do not put anything private in the names, the notes or the password. The relay address is the one in `docs/relay-setup.md`; ask Claude if you do not have one running.

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you), open the **Actions** tab, newest run **cross-platform**, wait for three green ticks. Download **Quartermaster-Desktop-osx-arm64**, unzip it and the `.tar.gz`, move the `osx-arm64` folder to the Desktop (replace the old one). Start it as in the stage 4a checklist (right-click > Open, or the two `xattr` / `chmod` lines), **without** a module: just the program. In the launcher add `Riverland-Demo.vmod` and choose `Riverland-Demo.qrules` as its rules pack. On the Windows PC start the WinForms app with the same module and pack.

## B. The secret store test (Terminal, once)

2. In Terminal, in a copy of the project (`git clone` of the branch; ask Claude if you have none), run:

   ```
   QUARTERMASTER_SECRET_STORE_TESTS=1 dotnet test tests/Quartermaster.Render.Tests --filter SecretStore
   ```

   Expected: all tests pass, **none skipped** (without the variable the real-Keychain test is skipped on purpose). The Mac may ask whether "dotnet" may use the Keychain: answer **Allow** (or **Always Allow**) and write down exactly what the question said. Afterwards open **Keychain Access**, search for "Quartermaster turn mailbox": nothing is left behind by the test.

## C. Send a turn from the Mac

3. Play offline: **New game…** on Riverland, and on the **Players** page add two players, **Anna** (Northern League) and **Ben** (Southern Compact). Start; continue the handover to Anna. Move a Northern League counter.
4. Open the **Game** menu. Expected: **Send Turn…** first, then the online items (hidden offline). Choose **Send Turn…**. Expected: the window **Who are you?** with "Which player are you? This computer remembers it for this game." and a choice per player; pick **Anna** and **OK**.
5. Expected: the window **Send Turn** with "Send Anna's turn. Next you choose where to save the turn file.", **Send to:** (Ben), the box **Also send through my relay** with **Relay:** and **Mailbox password (optional, the same for every player):**, and the note "Turn files carry the whole game; hidden information is only hidden on screen." Tick the box, type the relay address and a password, click **Send…**. The Mac save window opens (title "Save Turn", a name ending in `.qturn`); save it on the Desktop.
6. Expected: the status bar says "Turn saved to … .qturn", then "Sending the turn through the relay…", then "The turn is in the mailbox on the relay." **The first time, the Mac may ask whether Quartermaster may use the Keychain**: answer **Allow** and write down what it said. The handover button in the status bar is gone (a turn game is not hot-seat), only Northern League's private values show, and **Edit > Undo** is off with the tip "This turn was already sent."
7. Open **Keychain Access** and search for "Quartermaster turn mailbox": one entry (its account is a random id, not the game or the password). Save the game (**File > Save Game As…**, `Mac turns`), quit Quartermaster (**Cmd+Q**), start it again and open that save: the mailbox is still watched and **Send Turn…** shows the relay and the password filled in without a question.
8. **Locked keychain:** lock the login keychain (Keychain Access > File > Lock Keychain "login"), then choose **Send Turn…** again and cancel the window. Expected: macOS asks for the keychain password; Quartermaster may pause **briefly** (a second or two) while the question is up, but must not hang or crash. Cancel the macOS question once: the password field is empty and nothing else breaks; then unlock and try again. Write down how long the pause was.

## D. The turn crosses to the WinForms app and back

9. Copy the `.qturn` file from step 5 to the Windows PC. In the WinForms app choose **File > Open Turn File…**, open it, say you are **Ben**, tick the mailbox with the same relay and password, and play the turn through. Move a Southern Compact counter and **Game > Send Turn…** to Anna through the mailbox.
10. On the Mac (the game from step 3 open) wait: within five minutes the alert bar shows **"New turn waiting from Ben"** (also with the alert bar switched off in Settings). Click it. Expected: the **replay bar** under the alert bar ("Turn from Ben:", **Next**, **Play all**, **Stop**, "Step 0 / N"); the status bar says "Replaying the turn from Ben: Next, Play all or Stop."
11. While it replays open the **Ledger** and the **Outliner**: no private value of **either** side shows (not even Anna's own), and the log shows no private line of either side. Click **Next** a few times, then **Play all**; at the end the bar goes, the status bar says "Ben's turn is replayed. Your turn.", and Anna's private values come back (Ben's stay hidden). **Edit > Undo** does not take back Ben's moves.
12. **A turn sent from the WinForms app on Windows, opened on the Mac as a file:** in WinForms send another turn to Anna **without** the mailbox, copy the file to the Mac and use **File > Open Turn File…** (and once from the launcher: **Play offline > Open turn file…**). Expected: it opens and replays the same way, and the end position matches (no "The replayed turn does not match the sender's game." box). Open the same file again: "This turn has already been played." with **Open anyway** / **Cancel**; **Cancel** leaves the game as it was.

## E. A password that cannot be read

13. Quit Quartermaster. In **Keychain Access** delete the "Quartermaster turn mailbox" entry from step 7 (as if the game's settings had come from another computer). Start Quartermaster, open the game and choose **Send Turn…**. Expected: the window **Turn mailbox** with "This game's mailbox password could not be read on this computer. Type it again (leave it empty if the game has none):" comes **once**; type the password and cancel the Send Turn window. Choose **Send Turn…** again: no second question, the password is filled in, and Keychain Access shows a new entry. Write down what you saw if no question came.

## F. Export Viewer Copy (online guest)

14. Join an online game as a guest (as in the stage 5c checklist, the PC hosting). **File > Export Viewer Copy…** is there (it is not for a host or offline). Save it; open the file in a new window: a banner says it is a viewer copy, and **Game > Send Turn…** is off.

## G. Linux (a real Linux desktop, if you have one)

15. **libsecret present** (a normal GNOME or KDE desktop): run the test from step 2 with the same command. Expected: all pass, none skipped; the keyring may ask to unlock. Then do steps 3 to 7 with **Seahorse** ("Passwords and Keys") instead of Keychain Access: one entry "Quartermaster turn mailbox". Locking the keyring and sending again shows the keyring's unlock question; Quartermaster may pause briefly but must not hang.
16. **libsecret absent** (a minimal desktop or a session without a keyring, for example `sudo apt remove libsecret-1-0` in a throw-away VM, or a login without D-Bus): send a turn with the mailbox. Expected: the status bar says "The mailbox password could not be stored on this computer; it is kept until the window closes."; the send still works; after a restart the password is asked for once (step 13's window). The test from step 2 shows the real-store test failing or skipped there: write down which.

Write down every step that did not behave as described, with a screenshot if you can. Especially: step 2 (the test run and the Keychain question), steps 6 and 8 (Keychain prompts and the locked keychain), step 12 (a WinForms turn opened on the Mac), and the Linux steps 15 and 16.
