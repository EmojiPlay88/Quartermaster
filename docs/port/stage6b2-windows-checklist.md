# Stage 6b-2 checklist for Windows (the setup.exe)

This tests the **Windows installer**: a per-user setup program, an update installed into that copy, and an uninstall that keeps your data. Follow the steps and write down what you see. Allow about 40 minutes. The update test uses a scratch data folder, so your real settings stay untouched.

## A. Get and install

1. Sign in to GitHub in your browser (downloading artifacts needs an account). Open the repository's **Actions** tab, open the latest green **cross-platform** run for the branch or pull request being tested, scroll to **Artifacts** and download **Quartermaster-Windows**. GitHub wraps it in a zip; unzip it. It holds `Quartermaster-Setup-<v>.exe`, `Quartermaster-<v>-win-x64.zip` and a `.sha256` file for each.
2. Optional: in PowerShell, `(Get-FileHash .\Quartermaster-Setup-<v>.exe -Algorithm SHA256).Hash` should match the start of the `.sha256` file (case aside).
3. Double-click the setup. Expected: **SmartScreen** may say "Windows protected your PC" (the setup is unsigned). Choose **More info**, then **Run anyway**. Write down anything else.
4. Expected: no administrator (UAC) prompt; a wizard that asks where to install (the default folder is fine; try a folder under Program Files once and expect a refusal message), with a "Create a desktop shortcut" box (unticked) and, at the end, "Launch Quartermaster" (unticked). Finish the install.
5. Expected: the Start menu has **Quartermaster**; the program is in `%LOCALAPPDATA%\Programs\Quartermaster` (it holds `Quartermaster.exe` and `quartermaster-package.json`). Windows Settings > Apps lists **Quartermaster** once.
6. Check the marker and version from PowerShell (no window opens; the `| Out-String` is needed, because the program has no console and PowerShell would otherwise show nothing): `Get-Content "$env:LOCALAPPDATA\Programs\Quartermaster\quartermaster-package.json"` should say `"package":"win-setup"`, and `& "$env:LOCALAPPDATA\Programs\Quartermaster\Quartermaster.exe" --version | Out-String` prints `Quartermaster <v>`.
7. Start **Quartermaster** from the Start menu. Expected: the launcher opens. **Help > About** shows the version. Close it.
8. Run the same setup again. Expected: it upgrades in place, and Settings > Apps still lists Quartermaster **once**.

## B. Update the setup install from a test feed

The update must be a version higher than the installed one (the CI build, for example 0.1.0), so every command below uses the made-up version `9.9.9`; it exists only in this local test feed. The update feed is a local folder, so nothing touches GitHub. In the project folder (with the .NET 10 SDK):

9. Build the update and zip it as the release does:

   ```
   dotnet publish src/Quartermaster.Desktop -c Release -r win-x64 --self-contained -p:Version=9.9.9 -o $env:TEMP\qm-test\pub-9.9.9
   New-Item -ItemType Directory -Force $env:TEMP\qm-test\feed | Out-Null
   Compress-Archive -Path $env:TEMP\qm-test\pub-9.9.9\* -DestinationPath $env:TEMP\qm-test\feed\Quartermaster-9.9.9-win-x64.zip
   $h = (Get-FileHash $env:TEMP\qm-test\feed\Quartermaster-9.9.9-win-x64.zip -Algorithm SHA256).Hash.ToLower()
   "$h  Quartermaster-9.9.9-win-x64.zip" | Set-Content -Encoding ascii $env:TEMP\qm-test\feed\Quartermaster-9.9.9-win-x64.zip.sha256
   (Get-Item $env:TEMP\qm-test\feed\Quartermaster-9.9.9-win-x64.zip).Length
   ```

   The last line prints the zip's size; you need it in the next step.
10. Write `%TEMP%\qm-test\feed\release.json` (replace `ZIPSIZE`):

    ```
    {
      "tag_name": "quartermaster-v9.9.9",
      "draft": false,
      "prerelease": false,
      "html_url": "https://example.invalid/releases/quartermaster-v9.9.9",
      "body": "TEST notes\n- one",
      "assets": [
        { "name": "Quartermaster-9.9.9-win-x64.zip", "size": ZIPSIZE, "browser_download_url": "https://example.invalid/Quartermaster-9.9.9-win-x64.zip" },
        { "name": "Quartermaster-9.9.9-win-x64.zip.sha256", "size": 1, "browser_download_url": "https://example.invalid/Quartermaster-9.9.9-win-x64.zip.sha256" }
      ]
    }
    ```

11. Start the installed app with the feed and a scratch data folder, from PowerShell: `$env:QUARTERMASTER_UPDATE_FEED="$env:TEMP\qm-test\feed"; $env:QUARTERMASTER_LAYOUT_ROOT="$env:TEMP\qm-test\data"; & "$env:LOCALAPPDATA\Programs\Quartermaster\Quartermaster.exe"`. Expected: the launcher shows the pale-yellow update bar naming 9.9.9.
12. Click **Update now**. Expected: progress, the app closes and starts again by itself and shows **Updated to 9.9.9.**; the bottom line shows 9.9.9. `%LOCALAPPDATA%\Programs\Quartermaster.backup-<old version>` holds the old files. Two things are expected here: Settings > Apps still shows the **old** version number (the in-app update does not touch the installer's registry entry), and `quartermaster-package.json` now says `win-zip` (the update is the zip build).
13. Quit the app. Expected: Settings > Apps still lists Quartermaster once.

## C. Uninstall keeps your data

14. Make sure `%LOCALAPPDATA%\Quartermaster` exists (your settings, modules, saves). If you never ran the app without the scratch variable, create the folder and put a dummy file in it.
15. Uninstall from Settings > Apps. Expected: no administrator prompt; `%LOCALAPPDATA%\Programs\Quartermaster` and its `.backup-*` folders are gone; the Start menu entry is gone; `%LOCALAPPDATA%\Quartermaster` and what you put in it is still there.
16. Clean up: `Remove-Item -Recurse -Force $env:TEMP\qm-test`, and `$env:QUARTERMASTER_UPDATE_FEED=$null; $env:QUARTERMASTER_LAYOUT_ROOT=$null`.

## D. The zip is portable (short)

17. Unzip `Quartermaster-<v>-win-x64.zip` into a folder you can write to and run `Quartermaster.App.exe`. Expected: the launcher opens; `quartermaster-package.json` there says `win-zip`. Nothing is added to the Start menu or the Apps list. Delete the folder afterwards.

Tell Claude what you saw in each step.
