using Avalonia.Controls;
using Quartermaster.Desktop.Documents;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>
/// Document windows (WinForms MainForm.OpenDocumentAtPage): one per document entry, owned by the main window and not modal, their bounds
/// remembered per module under "doc:{entry}". All of them close with the session.
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<string, DocumentWindow> _docWins = new();

    /// <summary>The open document windows by entry name (tests).</summary>
    internal IReadOnlyDictionary<string, DocumentWindow> DocumentWindows => _docWins;

    /// <summary>Opens the document's window at page 1, or brings the one already open to the front.</summary>
    public void OpenDocument(DocumentDef d) => OpenDocumentWindow(d, 1);

    /// <summary>Opens (or activates) a module document and jumps to a 1-based page; an entry the module lacks is an error box.</summary>
    public void OpenDocumentAtPage(string documentEntry, int page)
    {
        if (_session is not { } s) return;
        var doc = s.Model.Documents.FirstOrDefault(d => d.EntryName == documentEntry);
        if (doc == null)
        {
            ShowError($"The rule reference points to '{documentEntry}', which is not in this module.");
            return;
        }
        Guard(() => OpenDocumentWindow(doc, page));
    }

    void OpenDocumentWindow(DocumentDef d, int page)
    {
        if (_session is not { } s) return;
        if (_docWins.TryGetValue(d.EntryName, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.GoToPage(page);
            existing.Activate();
            return;
        }
        var w = new DocumentWindow(s, d, page, _dialogs);
        _docWins[d.EntryName] = w;
        var module = s.Archive.Sha256;
        var layoutKey = "doc:" + d.EntryName;
        if (!Restore(w, module, layoutKey)) w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.Closing += (_, _) => SaveBounds(w, module, layoutKey);
        w.Closed += (_, _) => { if (_docWins.TryGetValue(d.EntryName, out var cur) && ReferenceEquals(cur, w)) _docWins.Remove(d.EntryName); };
        w.Show(this);
    }

    /// <summary>Before the session goes: every document window closes.</summary>
    void DestroyDocuments()
    {
        var all = _docWins.Values.ToList();
        _docWins.Clear();
        foreach (var w in all)
            try { w.Close(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("closing a document window: " + ex.Message); }
    }
}
