# Documents and Diagnostics

## Rulebooks and documents

Double-click an entry under **Rulebooks & Documents** in the module tree to open it as a tab. In Riverland there are two: "Riverland Rules", a short text, and "Riverland Rulebook", a three-page PDF.

The document window has the same toolbar as the rulebook pane of the Rules Workbench: a list of the module's documents, **Page:** with **<** and **>** for PDFs, and a **Used on this page** list when rule references point at the page. See [Rulebook search and OCR](Window-Rulebook-Search-and-OCR#rulebook-pane). A **Rule ref…** button in a formula dialog, or the **Rule:** link in the phase panel, opens the document at the right page.

Showing PDFs and other documents needs the Microsoft Edge WebView2 runtime. If it is missing, the window says so and gives the download address.

## Charts

Double-click an entry under **Charts** in the module tree to open the chart picture as a tab.

- The mouse wheel zooms at the pointer, and dragging moves the picture.
- **Fit** shows the whole chart, **100%** shows it at its real size.

Charts are pictures from the module. To turn a chart into a table the rules can use, see [Rulebook search and OCR](Window-Rulebook-Search-and-OCR#scans-and-ocr).

## Diagnostics

**View > Diagnostics** (Ctrl+D, or the toolbar's **Diagnostics** button) lists what Quartermaster noticed while it read the module and ran the game, for example parts of the module it could not read, and actions or formulas that failed. When there are problems, the status bar says so; click the message to open this list.

| Control | What it does |
|---|---|
| **Show:** | **All**, **Warnings and errors** or **Errors only** |
| **Copy all** | Copies the list, for example to paste into a bug report |
| List | Severity, Category, Message and Context (for example the map or piece concerned) |

The list keeps updating while it is open. Errors are worth a look; the [FAQ](FAQ) has more help.

There is no screenshot of the Diagnostics window yet.
