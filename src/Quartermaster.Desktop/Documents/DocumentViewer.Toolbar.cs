using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Documents;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Documents;

/// <summary>
/// The <c>toolbar: true</c> parts of the viewer (WinForms PdfViewerControl): the document list before the navigation bar, host tool
/// items, and the "Used on this page" panel. With <c>toolbar: false</c> none of this exists.
/// </summary>
public sealed partial class DocumentViewer
{
    /// <summary>The non-selectable line between the PDFs and the images in the document list.</summary>
    public const string ImagesSeparator = "──── Images ────";

    public const string NoPdfDocumentsText = "This module has no PDF documents.";
    public const string ChooseDocumentText = "Choose a document above.";
    public const string UsedOnPageTitle = "Used on this page";
    public const string NoDocumentOpenText = "No document open.";
    public const string NothingPointsHereText = "Nothing in the rules pack points here.";

    /// <summary>GrayText of the classic theme.</summary>
    static readonly IBrush GrayText = new SolidColorBrush(Color.FromRgb(0x6D, 0x6D, 0x6D));

    readonly List<DocumentDef> _pdfDocs = new();
    readonly List<(string Kind, string Id)> _usedIds = new();
    ComboBox? _docList;
    StackPanel? _toolRow;
    Border? _usedPanel;
    ListBox? _usedList;
    TextBlock? _usedTitle;
    int _imageCount;
    bool _listSync;

    /// <summary>Adds the toolbar row (and the Used panel) to the root; called by the constructor when the toolbar flag is set.</summary>
    void InitToolbar(DockPanel root)
    {
        foreach (var d in _s.Model.Documents.Where(d => d.Kind == DocumentKind.Pdf)) _pdfDocs.Add(d);
        _docList = new ComboBox { Width = 180, VerticalAlignment = VerticalAlignment.Center };
        foreach (var d in _pdfDocs) _docList.Items.Add(d.Title);
        _toolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _toolRow.Children.Add(_docList);
        _toolRow.Children.Add(_nav);
        _docList.SelectionChanged += (_, _) => OnListChanged();
        DocumentOpened += SyncList;

        _usedTitle = new TextBlock { Text = UsedOnPageTitle, FontWeight = FontWeight.Bold };
        _usedList = new ListBox();
        var inner = new DockPanel();
        DockPanel.SetDock(_usedTitle, Dock.Top);
        inner.Children.Add(_usedTitle);
        inner.Children.Add(_usedList);
        _usedPanel = new Border { Height = 50, Padding = new Thickness(4, 0, 4, 2), IsVisible = false, Child = inner };
        _usedList.AddHandler(InputElement.DoubleTappedEvent, (_, _) => ActivateUsed());
        _usedList.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ActivateUsed(); e.Handled = true; } };

        DockPanel.SetDock(_toolRow, Dock.Top);
        DockPanel.SetDock(_usedPanel, Dock.Top);
        root.Children.Add(_toolRow);
        root.Children.Add(_usedPanel);
    }

    /// <summary>The module's PDF documents in the list, in model order (empty without a toolbar).</summary>
    public IReadOnlyList<DocumentDef> PdfDocuments => _pdfDocs;

    /// <summary>Appends a host control to the toolbar (after the navigation bar).</summary>
    public void AddToolItem(Control item) => _toolRow?.Children.Add(item);

    /// <summary>An image in the document list was chosen (index into the list given to <see cref="SetImageDocuments"/>).</summary>
    public event Action<int>? ImageDocumentChosen;

    /// <summary>A "Used on this page" line was opened: kind (Formula, Table, Phase) and id.</summary>
    public event Action<string, string>? UsedItemActivated;

    /// <summary>Lists images (module image entries, opened scans) under an "Images" separator after the PDFs.</summary>
    public void SetImageDocuments(IReadOnlyList<string> titles)
    {
        if (_docList == null) return;
        _listSync = true;
        try
        {
            while (_docList.Items.Count > _pdfDocs.Count) _docList.Items.RemoveAt(_docList.Items.Count - 1);
            _imageCount = titles.Count;
            if (titles.Count > 0)
            {
                _docList.Items.Add(ImagesSeparator);
                foreach (var t in titles) _docList.Items.Add(t);
            }
        }
        finally { _listSync = false; }
        SyncList();
    }

    void OnListChanged()
    {
        if (_listSync || _docList == null) return;
        var i = _docList.SelectedIndex;
        if (i < 0) return;
        if (i < _pdfDocs.Count) { Open(_pdfDocs[i]); return; }
        var image = i - _pdfDocs.Count - 1;   // after the separator
        SyncList();   // the PDF stays the viewer's document; the host switches to the image pane
        if (image >= 0 && image < _imageCount) ImageDocumentChosen?.Invoke(image);
    }

    void SyncList()
    {
        if (_docList == null) return;
        _listSync = true;
        try { _docList.SelectedIndex = Document == null ? -1 : _pdfDocs.IndexOf(Document); }
        finally { _listSync = false; }
    }

    // ---------- used on this page ----------

    /// <summary>Shows the "Used on this page" panel under the toolbar (workbench only).</summary>
    public void EnableUsedOnPage()
    {
        if (_usedPanel != null) _usedPanel.IsVisible = true;
    }

    /// <summary>Fills "Used on this page" with (kind, id, text) lines; an empty list shows a grey note.</summary>
    public void SetUsedOnPage(IReadOnlyList<(string Kind, string Id, string Text)> items)
    {
        if (_usedList == null) return;
        _usedIds.Clear();
        _usedList.Items.Clear();
        foreach (var (kind, id, text) in items)
        {
            _usedIds.Add((kind, id));
            _usedList.Items.Add(text);
        }
        if (items.Count == 0) _usedList.Foreground = GrayText;
        else _usedList.ClearValue(TemplatedControl.ForegroundProperty);
        if (items.Count == 0) _usedList.Items.Add(Document == null ? NoDocumentOpenText : NothingPointsHereText);
    }

    /// <summary>The "Used on this page" lines (empty when nothing points here).</summary>
    public IReadOnlyList<string> UsedOnPageLines =>
        _usedList == null || _usedIds.Count == 0 ? Array.Empty<string>() : _usedList.Items.Select(o => o?.ToString() ?? "").ToList();

    void ActivateUsed()
    {
        if (_usedList == null || _usedList.SelectedIndex < 0 || _usedList.SelectedIndex >= _usedIds.Count) return;
        var (kind, id) = _usedIds[_usedList.SelectedIndex];
        UsedItemActivated?.Invoke(kind, id);
    }

    // ---------- test access ----------

    internal ComboBox? DocumentList => _docList;
    internal StackPanel? ToolRow => _toolRow;
    internal Border? UsedPanel => _usedPanel;
    internal ListBox? UsedList => _usedList;
    internal TextBlock? UsedTitle => _usedTitle;
}
