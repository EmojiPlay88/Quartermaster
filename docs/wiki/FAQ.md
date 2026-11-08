# FAQ and Troubleshooting

Quick answers to the questions people ask most. If yours is not here, the page for the window you are using is probably the best next stop: see [Menus and Windows](Menus-and-Windows).

- [Windows says "Windows protected your PC"](#windows-says-windows-protected-your-pc)
- [A module will not open](#a-module-will-not-open)
- [The rules pack says it was made for a different module](#the-rules-pack-says-it-was-made-for-a-different-module)
- [Windows asks about the firewall](#windows-asks-about-the-firewall)
- [I cannot connect to the relay](#i-cannot-connect-to-the-relay)
- [The update failed or was rolled back](#the-update-failed-or-was-rolled-back)
- [Where does Quartermaster keep my files?](#where-does-quartermaster-keep-my-files)
- [Can I use it with other VASSAL modules?](#can-i-use-it-with-other-vassal-modules)
- [Does it run on Mac or Linux?](#does-it-run-on-mac-or-linux)

## Windows says "Windows protected your PC"

This is SmartScreen. It appears because the program is new and is not code-signed. Choose **More info**, then **Run anyway**. The `.sha256` file next to each download only checks that the download is not damaged; it does not prove who made the file. If you want to be certain what you are running, build the program from source (the steps are in the repository's README). More on this in [Install and Update](Install-and-Update#the-smartscreen-warning).

## A module will not open

Quartermaster opens ordinary VASSAL `.vmod` files. If one will not open, the message names the file and says why. The usual causes:

- **"Not a VASSAL module: no buildFile.xml".** The file is not a VASSAL module, or it is damaged. A `.vmod` is a zip file with a `buildFile.xml` inside. Download it again, and check that you picked the module and not a saved game (`.vsav`) or a different file.
- **The file moved.** **Add module…** only lists the file's location; it does not copy it. If you move or rename the file, the launcher can no longer find it. Choose **Remove**, then add it again from its new place.
- **It opens, but something is missing.** Parts of a module that Quartermaster does not know about are skipped, not fatal. Open **View > Diagnostics** (Ctrl+D) to see what was skipped. See [Can I use it with other VASSAL modules?](#can-i-use-it-with-other-vassal-modules).

Rulebook PDFs need the Microsoft Edge WebView2 Runtime, which Windows 11 includes. See [Install and Update](Install-and-Update#requirements) if PDFs stay blank.

## The rules pack says it was made for a different module

A rules pack records the exact module file it was made for (its SHA-256 fingerprint, a long code worked out from the file's contents). When you open a pack with a module whose fingerprint is different, Quartermaster adds a warning, "Rules pack was made for a different module", to **View > Diagnostics**, and still loads the pack.

This usually means one of these:

- **You picked the wrong pack.** Choose **change…** in the launcher and pick the right `.qrules` file.
- **The module is another version.** Even a small edit to the `.vmod` file (a new release of the module, say) changes the fingerprint. The pack may still work if the new version kept the same pieces, but nothing guarantees it, so test your formulas. Formulas and filters that depend on things that changed will show errors in the [Rules Workbench](Window-Rules-Workbench) and in Diagnostics. See [Making a Rules Pack](Making-a-Rules-Pack).

A saved game or turn file made with a different module is refused instead: "This save was made with a different module and cannot be loaded here" or "This turn file belongs to a different module". For a shared game, everyone needs the same module file and rules pack. In an online game, guests receive the host's rules pack automatically.

## Windows asks about the firewall

The Quartermaster app only makes outgoing connections (to GitHub to check for updates, and to a relay for online play). It does not listen for incoming ones, so players do not open ports or forward anything. If Windows asks you about the program, you can allow it for private networks.

The relay is different: it is a server, and it must be reachable from outside. If you host one, you open one TCP port (47820 by default) on the relay machine and, at home, forward it on your router. Windows may also ask on that machine the first time the relay runs. The steps are in [Hosting a Relay](Hosting-a-Relay#4-let-players-reach-it-router-and-firewall).

## I cannot connect to the relay

In the lobby window, the line at the bottom shows what happened. Work down this list:

1. **"Could not reach the relay: …".** The address or port is wrong, the relay is not running, or something is blocking the port. Check the address in the **Relay:** box, written like `relay.example.org:47820`. Ask the person who runs the relay whether it is up. If you run it, follow the checks in [Hosting a Relay](Hosting-a-Relay#8-check-that-it-works-and-share-the-fingerprint): test the port from another network (a phone on mobile data is ideal), and check the router forwarding and the firewall.
2. **"The relay's certificate was not trusted".** The first time you connect, the app shows the relay's certificate fingerprint and asks whether to trust it. Compare it with the one the relay's owner gave you, and say Yes only if they match. If you said No, connect again. If a relay you already trusted shows a changed fingerprint, a warning appears: ask the owner whether they reset the relay's data folder before you accept.
3. **"Wrong code or password".** Either the lobby code or its password is wrong. Check both with the host.
4. **A message about a different network version, ending "update both copies".** The relay, the host and the guests must all run versions that speak the same network version. Update the app (and the relay, if you run it).
5. **The lobby list is empty.** The list shows lobbies for the module you have selected, with the same module file. Check that you and the host have the same `.vmod`. The list refreshes every ten seconds; **Refresh** reloads it now.
6. **"Lost connection to the relay".** Your network dropped. Quartermaster keeps trying to reconnect and re-join the same game by itself. See [Playing With Others](Playing-With-Others).

## The update failed or was rolled back

Quartermaster only updates when you click **Update now**, and only when no game is open. See [Install and Update](Install-and-Update#updating). If something goes wrong you will see one of these:

- **"The update to … was not installed; nothing was changed. Close every other Quartermaster window and try again, or download it from the release page."** The updater refused before it touched anything, because another copy of Quartermaster was running from the same folder, or the copy has no file list. Close every other window and try again.
- **"This copy of Quartermaster can't update itself; download the new version from the release page."** The copy was built from source, or sits in a folder that is not writable (for example under `Program Files`). Download the zip and unzip it by hand. Your saves and settings are kept.
- **The download did not pass its checks.** The messages say the download's SHA-256 or size does not match the release, or the zip is not valid. Nothing was installed. Try again; if it keeps happening, download the zip from the release page yourself.
- **"The update to … didn't start, so Quartermaster went back to …".** The new version did not confirm that it had started, so the updater put the old files back. You are on your previous version and can keep playing. If the message adds "Some files may need a fresh download from the release page", download the zip and unzip it over the folder.

The previous version is kept in a `Quartermaster.backup-<version>` folder beside the program folder. Your saves and settings are never part of an update.

## Where does Quartermaster keep my files?

Your own files stay where you put them. **Add module…** only lists a `.vmod` file where it already is, and **Save Game As…** and **Save Rules Pack As…** save where you choose.

Everything else Quartermaster keeps for itself is in one folder, `%LOCALAPPDATA%\Quartermaster` (paste that into the Explorer address bar). In it:

| What | Where, inside that folder |
|---|---|
| Settings, including your module list | `settings.json` |
| Which rules pack goes with which module | `rules-index.json` |
| Your Ledger tabs (the ★ tabs you make with **Edit tabs…**) | `ledger-tabs.json` |
| Automatic saves, one folder per module | `autosave` |
| Prepared map tiles and documents | `cache` (the Folders tab of [Settings](Window-Settings#folders) shows the size and lets you clear it) |
| Scans you read with OCR, kept so they are not read twice | `scan-cache` |
| Window layouts | `layout.json` and the `layout` folder |
| Relays you have chosen to trust | `relay-pins.json` |
| Rejoin tokens for online games | `online` |
| Log files, useful when reporting a problem | `app.log` and `crash.log` |

Deleting `settings.json` puts everything back to the defaults (and empties your module list). The cache is safe to delete; it is rebuilt when needed. To move to a new computer, copy your `.vmod`, `.qrules` and `.qsave` files, plus `settings.json`, `rules-index.json` and `ledger-tabs.json` if you want your lists and tabs. If `settings.json` cannot be read, Quartermaster starts with the defaults, keeps a copy of the unreadable file and does not overwrite it until it can be read again.

## Can I use it with other VASSAL modules?

Yes, that is what it is for, with some honest limits.

- **What it does.** It opens standard `.vmod` files directly: boards, grids (hex, square and zoned), counters and their traits, setups, the turn track and rulebook PDFs. It can import saved games (`.vsav`). It does not change your module file.
- **What it may not support.** VASSAL is large and modules can do many things. Anything Quartermaster does not know about is skipped rather than guessed at. The skipped parts are listed in **View > Diagnostics** with messages such as "Unsupported trait … ignored", "Unsupported grid type … ignored" and "unmodelled element(s) … ignored". A module that relies on those parts, for example automated behaviours coded as unusual traits, may not play the same way as it does in VASSAL. The best test is to open your module and look at the Diagnostics list.
- **Rules packs are made per module.** Quartermaster ships no game content. A rules pack holds the tables, formulas and phases for one specific module file, and you (or someone sharing theirs) write it in the Rules Workbench. A pack made for one module will not carry over to another. See [Making a Rules Pack](Making-a-Rules-Pack).

## Does it run on Mac or Linux?

Windows today; Mac and Linux builds are in progress.
