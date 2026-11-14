# Stage 6a checklist for a Linux desktop

This tests **installing updates** on Linux: Update now downloads a release, swaps the install, restarts, and says "Updated to 0.0.2.". A broken new version must roll back. Follow the steps in a terminal and write down what you see.

The AppImage, .deb and .rpm packages are built in stage 6b. For now the **tar** install is tested from the plain `linux-x64` publish; parts E and F say what to check when those packages exist (or with hand-made ones). The update feed is a local folder, so nothing touches GitHub. Allow about 30 minutes.

## A. Build two versions (tar install)

1. In the project folder:

   ```
   dotnet publish src/Quartermaster.Desktop -c Release -r linux-x64 --self-contained -p:Version=0.0.1 -o ~/qm-test/app
   dotnet publish src/Quartermaster.Desktop -c Release -r linux-x64 --self-contained -p:Version=0.0.2 -o ~/qm-test/pub-0.0.2
   ```

   The 0.0.1 publish in `~/qm-test/app` is the installed copy (its `quartermaster-package.json` says `linux-tar`).
2. Package 0.0.2 as the release asset (name exactly this) with its hash:

   ```
   mkdir -p ~/qm-test/feed && cd ~/qm-test/pub-0.0.2 && tar -czf ../feed/Quartermaster-0.0.2-linux-x64.tar.gz . && cd ../feed
   sha256sum Quartermaster-0.0.2-linux-x64.tar.gz > Quartermaster-0.0.2-linux-x64.tar.gz.sha256
   ```

3. Write `~/qm-test/feed/release.json` (the shape of GitHub's "latest release" API; only the file names and the tar.gz's size matter):

   ```
   {
     "tag_name": "quartermaster-v0.0.2",
     "draft": false,
     "prerelease": false,
     "html_url": "https://example.invalid/releases/quartermaster-v0.0.2",
     "body": "TEST notes\n- one",
     "assets": [
       { "name": "Quartermaster-0.0.2-linux-x64.tar.gz", "size": 1, "browser_download_url": "https://example.invalid/Quartermaster-0.0.2-linux-x64.tar.gz" },
       { "name": "Quartermaster-0.0.2-linux-x64.tar.gz.sha256", "size": 1, "browser_download_url": "https://example.invalid/Quartermaster-0.0.2-linux-x64.tar.gz.sha256" }
     ]
   }
   ```

   Put the real byte size of the tar.gz (`stat -c%s`) in its `size`.

## B. Update to 0.0.2

4. Start 0.0.1 with the feed and a scratch data folder (so the test stays off your real Quartermaster settings; the app hands both variables on to the updater helper, so every restart keeps them): `QUARTERMASTER_LAYOUT_ROOT=~/qm-test/data QUARTERMASTER_UPDATE_FEED=~/qm-test/feed ~/qm-test/app/Quartermaster`. Start the app this way every time below. Expected: the launcher with a pale-yellow bar naming 0.0.2 (**What's new**, **Update now**, **Later**, **Skip this version**); the bottom line shows 0.0.1.
5. Click **Update now**. Expected: progress in the Update window, the app closes and starts again by itself within a few seconds.
6. Expected: the launcher shows **Updated to 0.0.2.** once and the bottom line shows 0.0.2. `~/qm-test/Quartermaster.backup-0.0.1` holds the old files, and `~/qm-test/app` holds the new ones (the list in `quartermaster-files.txt`). Start the app again: the notice is gone. `ls ~/qm-test/data/Updates` shows `helper.log`, `state.json` and **no** `swap-journal.json` (the journal is deleted once the new version confirmed its start).

## C. A broken 0.0.3 rolls back

7. Build 0.0.3 like 0.0.2 (steps 1 and 2 with `0.0.3`, tag `quartermaster-v0.0.3`, a new `release.json`) and **break it before packaging**: replace its program with a script that fails at once, `printf '#!/bin/sh\nexit 1\n' > ~/qm-test/pub-0.0.3/Quartermaster && chmod +x ~/qm-test/pub-0.0.3/Quartermaster`.
8. Start the 0.0.2 app as in step 4 (feed and data folder) and click **Update now**. Expected: the app closes, the new version does not start, and after up to about 30 seconds 0.0.2 starts again with **The update to 0.0.3 didn't start, so Quartermaster went back to 0.0.2.** The app folder holds 0.0.2 again. `~/qm-test/data/Updates/swap-journal.json` is gone and `helper.log` there tells the story ("New version did not confirm startup; rolling back").

## D. Refusals

9. Start the app as in step 4 in one terminal and, with it still open at the update bar, run the **same command again in a second terminal** (a second copy of the same install). Click **Update now** in the first. Expected: the app restarts with nothing changed and the launcher shows "The update to 0.0.2 was not installed; nothing was changed. Close every other Quartermaster window and try again, or download it from the release page."

## E. AppImage (stage 6b build, or a hand-made one)

10. Copy the 0.0.1 AppImage to `~/qm-test/Quartermaster.AppImage` (the file name stays the same through updates) and run it with the variables set: `QUARTERMASTER_LAYOUT_ROOT=~/qm-test/data QUARTERMASTER_UPDATE_FEED=~/qm-test/feed ~/qm-test/Quartermaster.AppImage`. The AppImage runtime sets `APPIMAGE` to its own path; that is how Quartermaster knows which file to replace. Release asset: `Quartermaster-0.0.2-x86_64.AppImage` plus `.sha256` in the feed.
11. Update now. Expected: the AppImage **file** is replaced by the new one, the app restarts (still as an AppImage) and shows **Updated to 0.0.2.**; the old file is the hidden `~/qm-test/.Quartermaster.AppImage.qm-backup-0.0.1` beside it. Run it from a read-only folder: Update now must say "Quartermaster can't update itself in this folder. Open the download page?" and change nothing.

## F. .deb and .rpm hand-off (stage 6b packages)

12. With a deb / rpm install, Update now downloads and checks `quartermaster_0.0.2_amd64.deb` (or `quartermaster-0.0.2-1.x86_64.rpm`) with its `.sha256`, then **opens the package installer** (`xdg-open <package>`, e.g. GNOME Software or Discover). Expected: the installer window shows the new package; Quartermaster itself does not install anything and does not restart. If no installer is registered the Update window says it could not open the package and names the file so you can install it by hand.

## Recovering by hand

If the machine lost power (or the helper was killed) in the middle of an update and the app is **missing**, the old version is kept beside it. For an AppImage or any other single-file install, the hidden file `.<name>.qm-backup-<old version>` (`ls -a` shows it) renamed back to `<name>` restores it (`mv ~/qm-test/.Quartermaster.AppImage.qm-backup-0.0.1 ~/qm-test/Quartermaster.AppImage`). For the tar install the old files are in `Quartermaster.backup-<old version>` beside the app folder: copy them back into the app folder. Starting the app again afterwards is safe; the next update finishes or undoes the interrupted swap itself.

## Clean up

13. Delete `~/qm-test`. Tell Claude what you saw in each step.
