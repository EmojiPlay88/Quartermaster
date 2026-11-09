using Avalonia.Controls;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Documents;

/// <summary>A floating window showing one module document (WinForms PdfWindow): hosts a <see cref="DocumentViewer"/> without its toolbar.
/// Closing it releases the document.</summary>
public sealed class DocumentWindow : Window
{
    readonly DocumentViewer _viewer;

    /// <param name="dialogs">Opens HTML documents in the browser; the main window's dialogs when given.</param>
    public DocumentWindow(ModuleSession s, DocumentDef d, int page = 1, IDialogs? dialogs = null)
    {
        Document = d;
        Title = d.Title;
        ShowInTaskbar = false;
        Width = 800;
        Height = 700;
        _viewer = new DocumentViewer(s, toolbar: false, dialogs);
        Content = _viewer;
        _viewer.Open(d, page);
    }

    public DocumentDef Document { get; }

    internal DocumentViewer Viewer => _viewer;

    /// <summary>Shows a 1-based page.</summary>
    public void GoToPage(int page) => _viewer.GoToPage(page);

    protected override void OnClosed(EventArgs e)
    {
        _viewer.Dispose();
        base.OnClosed(e);
    }
}
