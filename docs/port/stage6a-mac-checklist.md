# Stage 6a checklist for the M1 Mac

This tests **installing updates** on macOS: Update now downloads a release, swaps the whole `Quartermaster.app` for the new one, restarts, and says "Updated to 0.0.2.". A broken new version must roll back. You do not need to be technical; follow the steps in Terminal and write down what you see.

The finished `.app` bundle and its installer come in stage 6b. For now you build a **hand-made bundle** from the `osx-arm64` publish. Allow about 30 minutes. Everything happens in one scratch folder, `qm-test` in your home folder (`~/qm-test`; the test's data folder is `~/qm-test/data`); the update feed is a local folder, so nothing touches GitHub.

## A. Build two versions and a hand-made bundle

1. In Terminal, in the project folder, publish version 0.0.1 and 0.0.2 (the `-p:QmPackageKind=mac-app` part makes the package marker say `mac-app`):

   ```
   dotnet publish src/Quartermaster.Desktop -c Release -r osx-arm64 --self-contained -p:Version=0.0.1 -p:QmPackageKind=mac-app -o ~/qm-test/pub-0.0.1
   dotnet publish src/Quartermaster.Desktop -c Release -r osx-arm64 --self-contained -p:Version=0.0.2 -p:QmPackageKind=mac-app -o ~/qm-test/pub-0.0.2
   ```

2. Make a bundle from each publish: the publish folder becomes `Contents/MacOS` (the marker `quartermaster-package.json` and `quartermaster-files.txt` must end up beside the `Quartermaster` program, inside `Contents/MacOS`):

   ```
   cd ~/qm-test
   for v in 0.0.1 0.0.2; do mkdir -p app-$v/Quartermaster.app/Contents; mv pub-$v app-$v/Quartermaster.app/Contents/MacOS; done
   ```

3. Install 0.0.1: `cp -R app-0.0.1/Quartermaster.app ~/qm-test/Quartermaster.app`, then clear the quarantine flag: `xattr -cr ~/qm-test/Quartermaster.app`.
4. Package 0.0.2 as the release asset (the name must be exactly this) and write its hash:

   ```
   mkdir -p feed && cd app-0.0.2 && zip -qry ../feed/Quartermaster-0.0.2-osx-arm64.zip Quartermaster.app && cd ..
   cd feed && shasum -a 256 Quartermaster-0.0.2-osx-arm64.zip > Quartermaster-0.0.2-osx-arm64.zip.sha256 && cd ..
   ```

5. Write `feed/release.json` (the shape GitHub's "latest release" API returns; replace nothing, the file URLs only need the right file names):

   ```
   {
     "tag_name": "quartermaster-v0.0.2",
     "draft": false,
     "prerelease": false,
     "html_url": "https://example.invalid/releases/quartermaster-v0.0.2",
     "body": "TEST notes\n- one",
     "assets": [
       { "name": "Quartermaster-0.0.2-osx-arm64.zip", "size": 1, "browser_download_url": "https://example.invalid/Quartermaster-0.0.2-osx-arm64.zip" },
       { "name": "Quartermaster-0.0.2-osx-arm64.zip.sha256", "size": 1, "browser_download_url": "https://example.invalid/Quartermaster-0.0.2-osx-arm64.zip.sha256" }
     ]
   }
   ```

   Replace the `size` of the zip with its real byte size (`stat -f%z feed/Quartermaster-0.0.2-osx-arm64.zip`); the other size is not checked.

## B. Update to 0.0.2

6. Start the 0.0.1 app **with the feed and a scratch data folder** (so the test stays off your real Quartermaster settings; the app hands both variables on to the updater helper, so every restart keeps them): `QUARTERMASTER_LAYOUT_ROOT=~/qm-test/data QUARTERMASTER_UPDATE_FEED=~/qm-test/feed ~/qm-test/Quartermaster.app/Contents/MacOS/Quartermaster` (use the full path if `~` is not expanded). Start the app this way every time below. Expected: the launcher with a pale-yellow bar naming 0.0.2 and the buttons **What's new**, **Update now**, **Later**, **Skip this version**; the bottom line shows 0.0.1.
7. Click **Update now**. Expected: the Update window shows progress (download, check, install), the app closes and, after a few seconds, starts again by itself.
8. Expected: the launcher shows **Updated to 0.0.2.** once (and the bottom line shows 0.0.2). In `~/qm-test` there is now a hidden `.Quartermaster.app.qm-backup-0.0.1` beside `Quartermaster.app` (`ls -a`). Restart the app: the notice is gone.
9. `ls ~/qm-test/data/Updates` shows `helper.log`, `state.json`, `started-0.0.2` (the mark the new version writes once it started) and the folder `0.0.2`, and **no** `swap-journal.json` (the journal is deleted once the new version confirmed its start). `ls ~/qm-test/data/Updates/0.0.2` shows only `helper` (the copy of the updater that ran the update): the downloaded zip and the unpacked `staged` folder are deleted once 0.0.2 has started. The `0.0.2` folder itself goes away with the next update.

## C. A broken 0.0.3 rolls back

10. Build 0.0.3 like 0.0.2 (steps 1, 2, 4, 5 with `0.0.3` everywhere, tag `quartermaster-v0.0.3`, `feed/release.json` replaced; keep the 0.0.2 zip in `feed` if you like), then **break it before zipping**: replace its program with a script that fails at once:

    ```
    printf '#!/bin/sh\nexit 1\n' > app-0.0.3/Quartermaster.app/Contents/MacOS/Quartermaster && chmod +x app-0.0.3/Quartermaster.app/Contents/MacOS/Quartermaster
    ```

11. Start the 0.0.2 app as in step 6 (feed and data folder) and click **Update now**. Expected: the app closes, the new version does not start, and after up to about 30 seconds the old 0.0.2 starts again with the notice **The update to 0.0.3 didn't start, so Quartermaster went back to 0.0.2.** After it: `Quartermaster.app` is 0.0.2 again (the bottom line says so). `ls -a ~/qm-test` shows one backup, `.Quartermaster.app.qm-backup-0.0.1`, left from step 8 (the app keeps the newest backup and deletes older ones only after a *successful* update starts). There is no `.Quartermaster.app.qm-backup-0.0.2`: the helper moved the 0.0.2 bundle into that name for the swap and moved it back on the rollback. `~/qm-test/data/Updates/swap-journal.json` is gone and `helper.log` there tells the story ("New version did not confirm startup; rolling back"). Write down anything that looks different.

## Starting a second copy (for a refusal)

With the first copy open at the update bar, open a second Terminal window and run the same command as in step 6 again (`.../Quartermaster.app/Contents/MacOS/Quartermaster` directly; a second copy started with `open -n` also counts). Click **Update now** in the first copy. Expected: before anything is downloaded the app says "Close every other Quartermaster window first." (a message with OK); no Update window opens, nothing is downloaded, nothing is swapped and the app does not restart. Close the second copy afterwards; Update now then works again.

## Recovering by hand

If the Mac lost power (or the helper was killed) in the middle of an update and `Quartermaster.app` is **missing**, the old copy is not lost: it is the hidden folder `.Quartermaster.app.qm-backup-<old version>` beside where the app was. In Finder press Cmd+Shift+. to show hidden items (or use `ls -a` in Terminal), then rename it back to `Quartermaster.app`. Starting the app once more cleans up what the failed update left (the next update finishes or undoes the interrupted swap on its own).

## Clean up

12. Delete the whole test folder: `rm -rf ~/qm-test`. Tell Claude what you saw in each step.
