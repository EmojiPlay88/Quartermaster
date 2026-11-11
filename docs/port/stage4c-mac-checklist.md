# Stage 4c checklist for the M1 Mac

This is a test of the eighth cross-platform build of Quartermaster. On top of stage 4b it adds **OCR languages**: a new last page in Settings where you can download more languages for reading scanned pages, and a language list in the Workbench that shows readable names. Button and message wording below was written from the code: check it on a running build. You do not need to know anything technical. Follow the steps and write down what you see.

The test needs about 20 minutes and an internet connection (you switch it off in step 9). Each language is a download of a few megabytes from github.com/tesseract-ocr/tessdata_fast (tag 4.1.0). You need a scan with a few lines of German text: any picture or PDF page of German text works, for example a screenshot of a German web page.

## A. Get the build

1. Push the branch to GitHub (Claude will not do this for you), open the **Actions** tab, newest run **cross-platform**, wait for three green ticks. Download **Quartermaster-Desktop-osx-arm64**, unzip it and the `.tar.gz`, move the `osx-arm64` folder to the Desktop (replace the old one). Start it as in the stage 4a checklist (right-click > Open, or the two `xattr` / `chmod` lines).

## B. Download German

2. Open **Tools > Settings...** (Cmd+,) and select the last page, **OCR languages**. Expected: a list with the columns Name, Code, Size and Status, a **Filter:** box, and the line "Downloads from github.com/tesseract-ocr/tessdata_fast (tag 4.1.0, Apache-2.0). Nothing is downloaded unless you click Download." There is no Windows line on a Mac. English shows **Bundled**; every other language shows **Not installed**.
3. Type "German" in the filter, select **German** (code deu) and click **Download**. Expected: the status goes to Queued, then "Downloading n%", then **Installed**. Nothing else is downloaded.

## C. Read a German line

4. Close Settings with OK. Open the Workbench (Cmd+Shift+W), open your German scan (Open scan...), draw a region around a line or two of German text.
5. In the OCR panel open the language list. Expected: names such as **English** and **German** (not bare codes), and **More languages...** at the end. Choose **German** and click **Read text**. Expected: the text comes back with the German letters (umlauts and the sharp s) read correctly or nearly so. Choose English on the same region: the reading is clearly worse, which shows the language is in use.
6. Choose **More languages...** in the list. Expected: Settings opens straight on the OCR languages page, and the list goes back to the language you had chosen (it never stays on "More languages...").

## D. Remove German

7. On the OCR languages page select German and click **Remove**. Expected: the status goes back to **Not installed**, no error. Try to select English: **Remove** stays disabled (English is bundled).
8. Back in the Workbench the language list no longer offers German (or reading with it is refused with a clear message); English still reads.

## E. Network off

9. Switch the Mac's network off (Wi-Fi off). On the OCR languages page select any other language and click **Download**. Expected: a short message appears below the list, for example "Could not reach the server. Check the network connection.", the app does not freeze, and nothing is left half installed (the status is Not installed). Switch the network on again and download works.
10. Start a download and press **Cancel** while it runs: the status goes back to Not installed. Start one and close Settings: the app asks "A language download is still running. Stop it and close Settings?".

## F. Nothing new for Gatekeeper

11. This build adds no native libraries: the downloaded files are plain data (`.traineddata`) in the app data folder (`OcrLanguages`), not in the app folder. Confirm macOS shows no new Gatekeeper or security prompt during the steps above.

Write down every step that did not behave as described, with a screenshot if you can.
