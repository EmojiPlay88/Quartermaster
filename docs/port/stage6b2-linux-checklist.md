# Stage 6b-2 checklist for a Linux desktop (AppImage, .deb, .rpm, tar.gz)

This tests the **Linux packages**: running the AppImage and updating it, the .deb or .rpm install, and the package hand-off. It also closes the check parked in stage 6a: that a real AppImage tells Quartermaster which file to replace. Follow the steps in a terminal and write down what you see. Allow about 45 minutes. Note your **distribution and version** first (for example Ubuntu 24.04 or Fedora 42) and whether you use X11 or Wayland.

The update feed is a local folder, so nothing touches GitHub. How to write the feed's `release.json` is in `docs/port/stage6a-linux-checklist.md` (part A, step 3); the same shape is used here.

## A. Get the packages

1. Sign in to GitHub in your browser. Open the repository's **Actions** tab, open the latest green **cross-platform** run for the branch being tested, scroll to **Artifacts** and download **Quartermaster-Linux**; unzip it into `~/qm-test/dl`. It holds `Quartermaster-<v>-x86_64.AppImage`, `quartermaster_<v>_amd64.deb`, `quartermaster-<v>-1.x86_64.rpm`, `Quartermaster-<v>-linux-x64.tar.gz` and a `.sha256` for each.
2. Optional: in that folder, `sha256sum -c *.sha256` should say OK for every file.

## B. AppImage: run

3. `cp ~/qm-test/dl/Quartermaster-<v>-x86_64.AppImage ~/qm-test/Quartermaster.AppImage && chmod +x ~/qm-test/Quartermaster.AppImage`. (The file name stays the same through updates.)
4. `~/qm-test/Quartermaster.AppImage --version`. Expected: it prints `Quartermaster <v>` and exits. If it fails to start, write down the message (a missing `libicu`, OpenSSL or `libfuse.so.2` line is useful) and try `~/qm-test/Quartermaster.AppImage --appimage-extract-and-run --version`.
5. Start it for real with a scratch data folder: `QUARTERMASTER_LAYOUT_ROOT=~/qm-test/data ~/qm-test/Quartermaster.AppImage &`. Expected: the launcher opens.
6. **The parked 6a check.** With the app running, find its process and look at its environment and executable: `pid=$(pgrep -n -f 'usr/bin/Quartermaster')`, then `tr '\0' '\n' < /proc/$pid/environ | grep -E '^(APPIMAGE|APPDIR)='` and `readlink /proc/$pid/exe`. Expected: `APPIMAGE=` is the full path of `~/qm-test/Quartermaster.AppImage`; `APPDIR=` is a folder under `/tmp/.mount_...`; the `readlink` result starts with that `APPDIR` (the program runs from inside the mounted image). Write down all three. If `pgrep` finds nothing or the wrong process, use `ps aux | grep Quartermaster` to pick the right one.
7. Quit the app.

## C. AppImage: update

8. Build a 0.0.2 AppImage as the update asset. In the project folder: `dotnet publish src/Quartermaster.Desktop -c Release -r linux-x64 --self-contained -p:Version=0.0.2 -o ~/qm-test/pub-0.0.2`, then `mkdir -p ~/qm-test/feed && bash build/linux/make-appimage.sh ~/qm-test/pub-0.0.2 0.0.2 ~/qm-test/feed` (it downloads its pinned build tool and checks its hash; it needs network). That leaves `Quartermaster-0.0.2-x86_64.AppImage` and its `.sha256` in the feed. Write `~/qm-test/feed/release.json` listing both (tag `quartermaster-v0.0.2`; the AppImage's real byte size from `stat -c%s` in its `size`).
9. Start the 0.0.1 AppImage as in step 5 with `QUARTERMASTER_UPDATE_FEED=~/qm-test/feed` added. Expected: the update bar names 0.0.2. Click **Update now**. Expected: the AppImage **file** is replaced, the app restarts (still as an AppImage) and shows **Updated to 0.0.2.**; the old file is the hidden `~/qm-test/.Quartermaster.AppImage.qm-backup-0.0.1`.
10. Copy the AppImage into a read-only folder and run it from there with the feed. **Update now** must say "Quartermaster can't update itself in this folder. Open the download page?" and change nothing.

## D. .deb or .rpm: install, run, hand-off

Use the one for your distribution.

11. Install: Debian, Ubuntu and relatives, `sudo apt install ~/qm-test/dl/quartermaster_<v>_amd64.deb`; Fedora and relatives, `sudo dnf install ~/qm-test/dl/quartermaster-<v>-1.x86_64.rpm`. Expected: it pulls in libraries as needed (ICU, OpenSSL, fontconfig, FreeType, OpenGL) and finishes without errors. Write down any dependency it could not satisfy.
12. Check: `quartermaster --version` prints `Quartermaster <v>`; `cat /opt/quartermaster/quartermaster-package.json` says `linux-deb` or `linux-rpm`; **Quartermaster** is in the applications menu with an icon (a grey square with a dark "Q").
13. Make a 0.0.2 package for the feed. Publish as in step 8, then build the package with `bash build/linux/make-deb-rpm.sh ~/qm-test/pub-0.0.2 0.0.2 ~/qm-test/feed` (read the script's header first; it needs network for its pinned nfpm). Write a `release.json` that lists the `.deb` (or `.rpm`) and its `.sha256`.
14. Start the app from a terminal with the feed and a scratch data folder: `QUARTERMASTER_UPDATE_FEED=~/qm-test/feed QUARTERMASTER_LAYOUT_ROOT=~/qm-test/data quartermaster` (the menu entry cannot pass variables). Click **Update now**. Expected: the package is downloaded and checked, then your system's package installer opens it (`xdg-open`, for example GNOME Software or Discover); Quartermaster does not install anything itself and does not restart. The message names the saved file (`~/qm-test/data/Updates/0.0.2/...`). If no installer opens, write down the message; installing the named file by hand with `apt` or `dnf` must work.
15. Remove: `sudo apt remove quartermaster` or `sudo dnf remove quartermaster`. Expected: `/opt/quartermaster` is gone; your data under `~/.local/share/Quartermaster` is untouched.

## E. tar.gz (short)

16. `mkdir ~/qm-test/tar && tar -xzf ~/qm-test/dl/Quartermaster-<v>-linux-x64.tar.gz -C ~/qm-test/tar && ~/qm-test/tar/Quartermaster --version`. Expected: the version, and `quartermaster-package.json` there says `linux-tar`. On a minimal distribution it may fail for a missing `libicu`; write down the message (install your distribution's ICU package, for example `libicu`, and try again).

## Clean up

17. Delete `~/qm-test`. Tell Claude what you saw in each step, especially steps 4, 6, 9, 11 and 14.
