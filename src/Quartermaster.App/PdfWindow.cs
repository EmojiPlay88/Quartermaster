using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>MDI child showing one document; a thin host of <see cref="PdfViewerControl"/>.</summary>
sealed class PdfWindow : Form, WindowManager.IReparentAware
{
    public const string RuntimeUrl = PdfViewerControl.RuntimeUrl;

    readonly PdfViewerControl _viewer;

    public PdfWindow(ModuleSession s, DocumentDef d, int page = 1)
    {
        Document = d;
        Text = d.Title;
        ShowIcon = false;
        _viewer = new PdfViewerControl(s, toolbar: false) { Dock = DockStyle.Fill };
        Controls.Add(_viewer);
        _viewer.Open(d, page);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DocumentDef Document { get; }

    void WindowManager.IReparentAware.BeforeReparent() => _viewer.BeforeReparent();

    void WindowManager.IReparentAware.AfterReparent() => _viewer.AfterReparent();

    /// <summary>Re-navigates to the given 1-based page (PDF only).</summary>
    public void GoToPage(int page) => _viewer.GoToPage(page);
}
