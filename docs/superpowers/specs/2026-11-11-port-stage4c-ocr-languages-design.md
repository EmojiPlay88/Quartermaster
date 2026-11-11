# Port stage 4c: OCR language downloads — design

*2026-11-11. Branch `port-stage4c` (from master dc42c9f). Status: draft for user approval.*

## Intent

The cross-platform app (`Quartermaster.Desktop`, Avalonia) reads scans with OCR. Today Tesseract only has the bundled
English file (`tessdata/eng.traineddata`, from tessdata_fast), so a rulebook or chart in another language can't be read on
Mac or Linux. On Windows it can only be read if Windows itself has that OCR language installed. Stage 4c lets the user download more
Tesseract languages from inside the app. The WinForms app never had this; it is a new feature of the port (parity.md
already plans it as 4c).

The user skipped the design questions. Every choice below that isn't the download source is **Claude's call**, marked as such.

## Decided by the user

- **Download source:** the official Tesseract project, `github.com/tesseract-ocr/tessdata_fast`, **pinned to tag `4.1.0`**,
  fetched from `https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/<code>.traineddata`. Apache-2.0. This is the same
  data set as the bundled English file. No other host, no mirror, no fallback URL.

## Goals

1. On every OS, the user can pick one or more Tesseract languages from a list, download them with a click, see progress,
   cancel, and remove them again.
2. Downloaded languages appear in the OCR panel's language list straight away, with no restart, and read correctly.
3. Every downloaded file is checked against a SHA-256 recorded in the app before it is used. A mismatch is never kept.
4. Works offline gracefully: no network means a clear message, and nothing else breaks.

## Non-goals

- No automatic downloads, no background checks, no "suggested language" prompts. Nothing touches the network without a click.
- No `tessdata_best`, no script models (`script/` folder), no user-supplied URLs.
- No changes to the WinForms app (`Quartermaster.App`).
- No installing Windows' own OCR languages. That stays in Windows Settings, as the existing message already says.

## Anti-goals (failure even if it "works")

- A download that runs without the user clicking, or that sends anything beyond a plain GET for the file. No telemetry and
  no user details in headers. The User-Agent is the generic `Quartermaster/<version>`.
- A corrupt or tampered file reaching Tesseract.
- Writing into the app folder. The updater swaps that folder, and on macOS it's read-only.
- Breaking the bundled English or the Windows OCR path when nothing is downloaded.

## Design (Claude's calls unless marked)

### Catalog (Claude's call)
- A committed catalog `src/Quartermaster.Documents/Ocr/tessdata-fast-4.1.0.json` holds one entry per language file in the
  tag's root: `code` (e.g. `deu`), English display `name` (e.g. "German"), `bytes` and `sha256`.
- The catalog is generated **once at development time** by a script `build/ocr-catalog.ps1`. The script downloads every
  root `.traineddata` at the tag, hashes it, and writes the JSON. The builder runs it and commits the output. The app never
  fetches a list or index at run time; the catalog in the build is the whole truth.
- Display names come from a small hand-written ISO 639-2/T table in the script. A code without a name shows the code.
- `eng` is in the catalog but marked bundled. It can't be removed, and its hash must equal the bundled file's (a test pins
  this). **Checked 2026-11-11:** the bundled `src/Quartermaster.Documents/tessdata/eng.traineddata` (4,113,088 bytes, SHA-256
  `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`) is byte-identical to the tag-4.1.0 file, so nothing is
  re-bundled and the 3b benchmark stands. THIRD-PARTY-NOTICES should name the tag.

### Storage (Claude's call)
- Downloaded files go to `AppDataRoot.Sub("OcrLanguages")`, named `<code>.traineddata`.
- They are written to `<code>.traineddata.part` first, hashed while streaming, and renamed into place only when size and
  SHA-256 match. A mismatch or a cancel deletes the `.part`. Stale `.part` files are deleted on start.
- Removing a language deletes its file. A file whose hash doesn't match the catalog (e.g. edited by hand) is ignored and
  shown as "damaged — download again".

### Recognizers (Claude's call)
- `TesseractRecognizer` reads two folders: the bundled `tessdata/` and the user's `OcrLanguages/`. Bundled wins on a clash.
- Its language list must be refreshable without a restart. Engines already loaded stay. A removed language's engine is
  disposed the next time it's asked for, or at removal. The builder picks the mechanism; the requirement is that the OCR
  panel shows the new list right after a download or a removal.
- **Windows (Claude's call):** the OCR engine becomes per-language. The panel's list is Windows OCR's languages plus the
  downloaded Tesseract languages, without duplicates. A language Windows has is read by Windows OCR (better at 24/24 vs 21/24 in
  the 3b benchmark). Any other language is read by Tesseract. `QUARTERMASTER_OCR_ENGINE` keeps its meaning: `tesseract` uses
  Tesseract only, and `windows` uses Windows only, as today. The existing fallback rules in `OcrEngines` are otherwise unchanged.
- Language tags differ: Windows uses BCP-47 (`de-DE`), Tesseract uses ISO 639-2 (`deu`). A Windows language is not
  "the same" as a Tesseract one; both may appear. The list shows the display name, and the engine name where two
  entries share a name, e.g. "German (Windows)" and "German (Tesseract)".
- **Bundled English on Windows (Claude's call):** the router lists the *downloaded* Tesseract languages only, so the
  bundled `eng` stays hidden while Windows OCR is in use, as today. The default language stays Windows' profile language.
  When Windows has no OCR language, the existing wholesale fallback to Tesseract in `OcrEngines.Choose` is kept. That
  Tesseract instance then lists the bundled English plus every download. The router is used only when Windows OCR loads
  with at least one language.
- The OCR panel shows display names (from the catalog for Tesseract; from Windows' own display name for Windows) and keeps
  passing the engine tag underneath.

### UI (Claude's call)
- **New Settings page "OCR languages"** (after Folders). It shows a filterable list of catalog languages with columns
  Name, Code, Size and Status (Bundled / Installed / Not installed / Damaged / Downloading n%).
- Buttons: **Download** (the selected rows, several allowed, one at a time in a queue), **Cancel** (the current
  download plus the queue) and **Remove** (the selected installed rows; not bundled).
- A line under the list names the source: "Downloads from github.com/tesseract-ocr/tessdata_fast (tag 4.1.0, Apache-2.0).
  Nothing is downloaded unless you click Download."
- On Windows, a second line: "Windows OCR languages are added in Windows Settings."
- Downloads act at once; they aren't held until OK/Apply. OK, Cancel and Restore defaults don't touch installed languages.
  Restore defaults doesn't remove downloads.
- Closing Settings during a download cancels it, after a yes/no question, default No.
- **OCR panel** (Workbench → scan pane): the language dropdown gains a last item, "More languages…", which opens Settings on
  the OCR languages page.
  - **Owner (Claude's call):** it goes through the existing `MainWindow.ShowSettingsAsync`, extended with a start-page
    argument. Settings stays owned by the main window, as today. If Settings is already open, the call brings it to the
    front on the OCR languages page instead of opening a second one (the `_settingsOpen` guard stays).
  - Choosing "More languages…" never becomes the selected language. The dropdown goes back to the previous language.
  - **OCR during a download (Claude's call):** the Workbench and its reads keep working. A language being downloaded isn't
    listed until its file is verified and in place.
  - **Notification (Claude's call):** one app-wide OCR language service, owned by the main window next to `AppOcr()`, holds
    the catalog, the download queue and a `LanguagesChanged` event. The Settings page and the recognizer both use it.
    The OCR panel refreshes its dropdown on the event, on the UI thread.
  - Removing a language that a read is using waits for that read to finish. Removing the language the panel has selected
    switches the panel to the default.
- Errors appear inline on the page as plain text, and each also gets one log line. Covered: no network, HTTP error with its
  status, hash mismatch, disk full, folder not writable.

### Networking (Claude's call)
- One shared `HttpClient` with the system proxy, a 30 s connect timeout and no overall timeout, so big files on slow links
  still finish. Cancellation goes through `CancellationToken`.
- Streaming with progress. Redirects allowed only to `https`. No retries beyond the user clicking Download again.
- Tests never touch the real network. They use an `HttpMessageHandler` stub, or a local folder feed through
  `QUARTERMASTER_OCR_FEED=<folder>`, as the updater's `QUARTERMASTER_UPDATE_FEED` does. The catalog script is the only code
  that touches the real host, and it runs only by hand.

### Licence and docs
- THIRD-PARTY-NOTICES: the tessdata_fast entry already covers `eng`. Extend it to say further languages are downloaded on
  request from the same tag under Apache-2.0.
- `docs/port/parity.md`: a 4c row and a "Stage-4c deviations" note (a new feature with no WinForms counterpart). Also update
  the four places that say "English only until 4c".
- A Mac checklist `docs/port/stage4c-mac-checklist.md`: download a language, read a German page, remove it, and check the
  offline message.
- Screenshot states in `build/port-screenshots.ps1`: the Settings OCR page (with a stubbed feed) and the panel dropdown.

## Constraints

- C# .NET 10, Avalonia; existing projects only (`Documents`, `Render`, `Desktop`). No new NuGet packages.
- Tests headless (Avalonia.Headless). Never open visible windows. No real network in tests.
- Public repo: no personal paths, no game content. The catalog JSON is public data (language codes, sizes, hashes).
- Process as before: subagent-driven, task reviews, opus final review, ask before every push.

## Left to the builder

- How the catalog script lists the tag's files (GitHub API tree listing or a hard-coded code list checked against it).
- The refresh mechanism for `Languages` and the per-language router's exact shape (a wrapper `IOcrRecognizer` is
  expected).
- The page layout details, within the existing Settings page style.
- Whether the download queue lives in Documents (UI-free) or Desktop. UI-free and testable is preferred.
