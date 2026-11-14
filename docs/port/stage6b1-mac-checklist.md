# Stage 6b-1 checklist for the M1 Mac (the .dmg)

This tests the **macOS installer**: a disk image you open and drag Quartermaster out of, then an update installed into that app. You do not need to be technical; follow the steps and write down what you see. Allow about 40 minutes.

Before you start, note your **macOS version** (Apple menu > About This Mac). Quartermaster needs macOS 14 Sonoma or later. If it refuses to open on 14 or later, report that.

## A. Get and open the dmg

1. Sign in to GitHub in your browser (downloading artifacts needs an account). Open the repository's **Actions** tab, open the latest green **cross-platform** run for the branch or pull request being tested, scroll to **Artifacts** and download **Quartermaster-macOS**. GitHub wraps it in a zip; unzip it. It holds `Quartermaster-<v>-osx-arm64.dmg`, `.zip` and `.sha256` files.
2. Optional: check the download. In Terminal, in the folder with the files: `shasum -a 256 Quartermaster-<v>-osx-arm64.dmg` should print the same hash that is at the start of the `.dmg.sha256` file.
3. Double-click the `.dmg`. Expected: a window titled **Quartermaster <v>** with a light grey background, an arrow, the caption "Drag Quartermaster to Applications", **Quartermaster.app** on the left and an **Applications** shortcut on the right. Write down anything that looks off (icons overlapping, wrong text, no background).
4. Drag **Quartermaster** onto **Applications**. Eject the disk image (the eject button next to it in Finder's sidebar).

## B. First open

5. Open **Quartermaster** from Launchpad (it should be listed there; if not, find it in `/Applications`). Expected: macOS shows a warning, because the app is ad-hoc signed and not notarised. This is normal.
6. Get past it: on macOS 15 or later go to **System Settings > Privacy & Security**, scroll down and press **Open Anyway** (right-click > Open may not offer Open on 15+). On macOS 14 right-click > Open works. Alternative that always works, in Terminal: `xattr -dr com.apple.quarantine /Applications/Quartermaster.app`. If macOS says the app **"is damaged"**, that same command fixes it. Write down which way you used and the exact message you saw.
7. Expected: the launcher opens. The menu bar shows **Quartermaster**. **Help > About** shows the version from the dmg's name. Quit with Cmd+Q.
8. Optional: in Terminal, `/Applications/Quartermaster.app/Contents/MacOS/Quartermaster --version` prints the version and exits.

## C. Install an update into the dmg-installed app

This reuses the local-feed approach of `docs/port/stage6a-mac-checklist.md` (sections A and B), but the app being updated is the one in `/Applications`, not a hand-made bundle. You need a newer version than the one installed: either the `.zip` from a second CI run with a higher version, or one you build yourself with `-p:Version=<higher>` (6a step 1, then 6a step 4 to zip it; the zip must be named `Quartermaster-<higher>-osx-arm64.zip`, with its `.sha256` and a `release.json` as in 6a steps 4 and 5).

9. Put the zip, its `.sha256` and `release.json` in `~/qm-test/feed`. Start the installed app with the feed and a scratch data folder: `QUARTERMASTER_LAYOUT_ROOT=~/qm-test/data QUARTERMASTER_UPDATE_FEED=~/qm-test/feed /Applications/Quartermaster.app/Contents/MacOS/Quartermaster`. Expected: the update bar names the newer version.
10. Click **Update now**. Expected: progress, the app closes, restarts by itself and shows **Updated to <higher>.** (6a steps 7 to 9 describe what to expect in `~/qm-test/data/Updates` and the hidden backup, here `/Applications/.Quartermaster.app.qm-backup-<v>`. If `/Applications` refuses the swap, write down the message.)
11. Close the app. Open **Quartermaster** again from Launchpad or `/Applications` (without the environment variables). Expected: it opens with **no** Gatekeeper warning at all. Write down if any warning appears.
12. In Terminal run `codesign --verify --strict /Applications/Quartermaster.app; echo exit=$?` and write down the full output and exit code. **Report the result either way, pass or fail.** The update zip carries no extended signature attributes on its files; this step checks that the updated app still runs and verifies.
13. Remove the leftovers: `rm -rf ~/qm-test`, and the hidden backup if you want: `rm -rf /Applications/.Quartermaster.app.qm-backup-*`.

## D. Uninstall

14. Drag **Quartermaster** from `/Applications` to the Bin.
15. Settings and data stay in `~/Library/Application Support/Quartermaster`. First confirm it exists: `ls ~/Library/Application\ Support/Quartermaster` and write down what it lists; nothing is deleted yet. Only if you want everything gone: `rm -rf ~/Library/Application\ Support/Quartermaster`. Skip this if the folder is missing or holds nothing you recognise.

Tell Claude what you saw in each step, your macOS version, and the `codesign` output from step 12.
