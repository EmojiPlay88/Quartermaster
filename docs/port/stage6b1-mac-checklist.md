# Stage 6b-1 checklist for the M1 Mac (the .dmg)

This tests the **macOS installer**: a disk image you open and drag Quartermaster out of, then an update installed into that app. You do not need to be technical; follow the steps and write down what you see. Allow about 40 minutes.

Before you start, note your **macOS version** (Apple menu > About This Mac). Quartermaster needs macOS 14 Sonoma or later. If it refuses to open on 14 or later, report that.

## A. Get and open the dmg

1. Sign in to GitHub in your browser (downloading artifacts needs an account). Open the repository's **Actions** tab, open the latest green **cross-platform** run for the branch or pull request being tested, scroll to **Artifacts** and download **Quartermaster-macOS**. GitHub wraps it in a zip; unzip it. It holds `Quartermaster-<v>-osx-arm64.dmg`, `.zip` and `.sha256` files. Artifacts expire after the repository's retention period (90 days by default); if the run is older, ask for a fresh one.
2. Optional: check the download. In Terminal, in the folder with the files: `shasum -a 256 Quartermaster-<v>-osx-arm64.dmg` should print the same hash that is at the start of the `.dmg.sha256` file.
3. Double-click the `.dmg`. Expected: a window titled **Quartermaster <v>** with a light grey background, an arrow, the caption "Drag Quartermaster to Applications", **Quartermaster.app** on the left and an **Applications** shortcut on the right. Write down anything that looks off (icons overlapping, wrong text, no background).
4. Drag **Quartermaster** onto **Applications**. Eject the disk image (the eject button next to it in Finder's sidebar).

## B. First open

5. Open **Quartermaster** from Launchpad (it should be listed there; if not, find it in `/Applications`). Expected: macOS shows a warning, because the app is ad-hoc signed and not notarised. This is normal.
6. Get past it: on macOS 15 or later go to **System Settings > Privacy & Security**, scroll down and press **Open Anyway** (right-click > Open may not offer Open on 15+). On macOS 14 right-click > Open also works. Alternative that always works, in Terminal: `xattr -dr com.apple.quarantine /Applications/Quartermaster.app`. If macOS says the app **"is damaged"**, that same command fixes it. Write down which way you used and the exact message you saw.
7. Expected: the launcher opens. The menu bar shows **Quartermaster**. **Help > About** shows the version from the dmg's name. Quit with Cmd+Q.
8. Optional: in Terminal, `/Applications/Quartermaster.app/Contents/MacOS/Quartermaster --version` prints the version and exits.

## C. Install an update into the dmg-installed app

To test an update you need a version higher than the one in the dmg (0.1.0). You build it on the Mac with the real scripts, so it is signed the same way as the release. You need the **.NET 10 SDK** and a clone of the repository on the branch being tested; run the commands from its top folder. This uses a local folder as the update feed (like the 6a checklist), so nothing touches GitHub. Note: `~` may not expand in `VAR=~/x cmd`; use full paths if so.

9. Build the higher version, bundle and sign it, and zip it exactly as the release build does:

   ```
   dotnet publish src/Quartermaster.Desktop -c Release -r osx-arm64 --self-contained -p:Version=0.1.1 -o ~/qm-test/pub-0.1.1
   bash build/macos/make-app-bundle.sh ~/qm-test/pub-0.1.1 0.1.1 ~/qm-test/out
   mkdir -p ~/qm-test/feed
   ditto -c -k --norsrc --noextattr --keepParent ~/qm-test/out/Quartermaster.app ~/qm-test/feed/Quartermaster-0.1.1-osx-arm64.zip
   cd ~/qm-test/feed && shasum -a 256 Quartermaster-0.1.1-osx-arm64.zip > Quartermaster-0.1.1-osx-arm64.zip.sha256 && stat -f%z Quartermaster-0.1.1-osx-arm64.zip; cd -
   ```

   The last command prints the zip's size in bytes; you need it in the next step.

10. Write `~/qm-test/feed/release.json` (replace `ZIPSIZE` with the number just printed; the other size is not checked):

    ```
    {
      "tag_name": "quartermaster-v0.1.1",
      "draft": false,
      "prerelease": false,
      "html_url": "https://example.invalid/releases/quartermaster-v0.1.1",
      "body": "TEST notes\n- one",
      "assets": [
        { "name": "Quartermaster-0.1.1-osx-arm64.zip", "size": ZIPSIZE, "browser_download_url": "https://example.invalid/Quartermaster-0.1.1-osx-arm64.zip" },
        { "name": "Quartermaster-0.1.1-osx-arm64.zip.sha256", "size": 1, "browser_download_url": "https://example.invalid/Quartermaster-0.1.1-osx-arm64.zip.sha256" }
      ]
    }
    ```

11. Start the dmg-installed app with the feed and a scratch data folder: `QUARTERMASTER_UPDATE_FEED=~/qm-test/feed QUARTERMASTER_LAYOUT_ROOT=~/qm-test/data /Applications/Quartermaster.app/Contents/MacOS/Quartermaster` (full paths if `~` does not expand). Expected: the update bar names 0.1.1. Click **Update now**. Expected: progress, the app closes, restarts by itself and shows **Updated to 0.1.1.** (6a steps 7 to 9 describe what to expect in `~/qm-test/data/Updates` and the hidden backup, here `/Applications/.Quartermaster.app.qm-backup-0.1.0`. If `/Applications` refuses the swap, write down the message.)
12. Quit the app. Open **Quartermaster** from Launchpad or Finder (no environment variables). Expected: it opens with **no** Gatekeeper warning. Then run `codesign --verify --strict /Applications/Quartermaster.app; echo "exit=$?"` and write down the full output. A pass prints nothing and `exit=0`. Any other result: report it, with the text. This checks whether the update zip (which has no extended-attribute signatures on the managed files) leaves a valid bundle.
13. Remove the leftovers: `rm -rf ~/qm-test`, and the hidden backup if you want: `rm -rf /Applications/.Quartermaster.app.qm-backup-*`.

## D. Uninstall

14. Drag **Quartermaster** from `/Applications` to the Bin.
15. Settings and data stay in `~/Library/Application Support/Quartermaster`. First confirm it exists: `ls ~/Library/Application\ Support/Quartermaster` and write down what it lists; nothing is deleted yet. Only if you want everything gone: `rm -rf ~/Library/Application\ Support/Quartermaster`. Skip this if the folder is missing or holds nothing you recognise.

Tell Claude what you saw in each step, your macOS version, and the `codesign` output from step 12.
