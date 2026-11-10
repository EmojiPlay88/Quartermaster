using Quartermaster.Rules;

namespace Quartermaster.Desktop.Workbench;

/// <summary>Scan pane and OCR of the workbench (port stage 4a task 12 fills this file; until then a stub).</summary>
public sealed partial class WorkbenchWindow
{
    /// <summary>Called once from the constructor, after the tabs and the viewer exist.</summary>
    void WireScans() { }

    /// <summary>"Link to current page" while the scan pane shows a page: the scan's ref. False when no scan is shown.</summary>
    bool TryLinkScanPage(string label, out RuleRef? r)
    {
        r = null;
        return false;
    }

    /// <summary>A rule ref to a module image or an opened scan: shown in the scan pane. False when it is neither.</summary>
    bool TryShowScanRule(RuleRef r) => false;

    /// <summary>A search hit in a PDF opened with Open scan… (keyed by its path): shown in the scan pane.</summary>
    bool TryShowOpenedScan(string key, int page) => false;

    /// <summary>"Scan page 1" on a not-searchable search row.</summary>
    void ScanFirstPage(string entry) { }

    /// <summary>PDFs opened with Open scan…, for the rulebook search (session only).</summary>
    IEnumerable<(string Key, string Title, Func<Stream> Open)> OpenedScanPdfs() => [];

    /// <summary>Documents a rule ref can point at besides the module's documents: module images and opened scans.</summary>
    IReadOnlyList<(string Entry, string Title)> ExtraRefDocs() => [];

    /// <summary>The window closed: scan loads and OCR reads are cancelled.</summary>
    void OnScansClosed() { }
}
