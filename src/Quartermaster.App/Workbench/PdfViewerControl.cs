using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Quartermaster.Render;
using Quartermaster.Render.Search;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// Document viewer: PDF/HTML in WebView2, text in a read-only TextBox. Hosted by <see cref="PdfWindow"/> and the rules workbench.
/// The page box is the source of truth for the current page (WebView2 does not expose a reliable one).
/// </summary>
sealed class PdfViewerControl : UserControl
{
    public const string RuntimeUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    readonly ModuleSession _s;
    readonly bool _allowWeb;
    readonly Panel _body = new() { Dock = DockStyle.Fill };
    readonly ToolStrip? _bar;
    readonly ToolStripComboBox? _docs;
    readonly NumericUpDown? _pageBox;
    readonly List<DocumentDef> _docList = new();
    readonly InitGate _gate = new();
    WebView2? _web;
    string? _file;
    int _page = 1;
    string? _search;
    bool _rebuildWeb, _loading;
    readonly Panel? _usedPanel;
    readonly ListBox? _used;
    readonly List<(string Kind, string Id)> _usedIds = new();
    readonly Font? _boldFont; // "Used on this page" title; disposed with the control

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing); // the label using the font goes first
        if (disposing) _boldFont?.Dispose();
    }

    /// <param name="toolbar">Show the document combo and page controls.</param>
    /// <param name="allowWeb">False in smoke runs: PDF/HTML show a placeholder instead of starting WebView2.</param>
    public PdfViewerControl(ModuleSession s, bool toolbar, bool allowWeb = true)
    {
        _s = s;
        _allowWeb = allowWeb;
        Controls.Add(_body);
        if (!toolbar) return;
        _bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        _docs = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 180 };
        foreach (var d in s.Model.Documents.Where(d => d.Kind == DocumentKind.Pdf))
        {
            _docList.Add(d);
            _docs.Items.Add(d.Title);
        }
        _pageBox = new NumericUpDown { Minimum = 1, Maximum = 9999, Value = 1, Width = 56 };
        var prev = new ToolStripButton("<") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Previous page" };
        var next = new ToolStripButton(">") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Next page" };
        _bar.Items.Add(_docs);
        _bar.Items.Add(new ToolStripLabel("Page:"));
        _bar.Items.Add(new ToolStripControlHost(_pageBox));
        _bar.Items.Add(prev);
        _bar.Items.Add(next);
        // "Used on this page" sits under the toolbar (docked before it); hidden until the host turns it on.
        _used = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control };
        _usedPanel = new Panel { Dock = DockStyle.Top, Height = 50, Visible = false, Padding = new Padding(4, 0, 4, 2) };
        _usedPanel.Controls.Add(_used);
        _boldFont = new Font(Font, FontStyle.Bold);
        _usedPanel.Controls.Add(new Label { Text = "Used on this page", Dock = DockStyle.Top, Height = 16, Font = _boldFont });
        Controls.Add(_usedPanel);
        Controls.Add(_bar);
        _used.DoubleClick += (_, _) => ActivateUsed();
        _used.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ActivateUsed(); e.Handled = e.SuppressKeyPress = true; } };
        prev.Click += (_, _) => GoToPage(_page - 1);
        next.Click += (_, _) => GoToPage(_page + 1);
        _pageBox.ValueChanged += (_, _) => { if (!_loading) GoToPage((int)_pageBox.Value); };
        _docs.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _docs.SelectedIndex < 0) return;
            var i = _docs.SelectedIndex;
            if (i < _docList.Count) { Open(_docList[i]); return; }
            var image = i - _docList.Count - 1; // after the "Images" separator
            if (image < 0 || image >= _imageCount) { SyncBar(); return; }
            SyncBar(); // the PDF stays the viewer's document; the host switches to the image pane
            ImageDocumentChosen?.Invoke(image);
        };
        ShowText(_docList.Count == 0 ? "This module has no PDF documents." : "Choose a document above.");
    }

    /// <summary>The non-selectable line between the PDFs and the images in the document list.</summary>
    public const string ImagesSeparator = "──── Images ────";

    int _imageCount;

    /// <summary>Lists images (module image entries, opened scans) under an "Images" separator after the PDFs.</summary>
    public void SetImageDocuments(IReadOnlyList<string> titles)
    {
        if (_docs == null) return;
        _loading = true;
        try
        {
            while (_docs.Items.Count > _docList.Count) _docs.Items.RemoveAt(_docs.Items.Count - 1);
            _imageCount = titles.Count;
            if (titles.Count > 0)
            {
                _docs.Items.Add(ImagesSeparator);
                foreach (var t in titles) _docs.Items.Add(t);
            }
        }
        finally { _loading = false; }
        SyncBar();
    }

    /// <summary>An image in the document list was chosen (index into the list given to <see cref="SetImageDocuments"/>).</summary>
    public event Action<int>? ImageDocumentChosen;

    /// <summary>The PDF documents in the list, in order.</summary>
    public IReadOnlyList<DocumentDef> PdfDocuments => _docList;

    /// <summary>Adds a host button to the viewer's toolbar.</summary>
    public void AddToolItem(ToolStripItem item) => _bar?.Items.Add(item);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DocumentDef? Document { get; private set; }

    /// <summary>The current 1-based page (the page field's value).</summary>
    public int Page => _page;

    public event Action<int>? PageChanged;

    /// <summary>Another document was opened (the page may be unchanged).</summary>
    public event Action? DocumentOpened;

    /// <summary>A "Used on this page" line was opened: kind (Formula, Table, Phase) and id.</summary>
    public event Action<string, string>? UsedItemActivated;

    /// <summary>The term the viewer was last asked to highlight (<c>#search=</c>), null when none.</summary>
    public string? SearchTerm => _search;

    /// <summary>Opens the document at a page; <paramref name="search"/> asks the PDF viewer to highlight a term where it can.</summary>
    public void Open(DocumentDef d, int page = 1, string? search = null)
    {
        if (IsDisposed || Disposing) return;
        page = Math.Clamp(page, 1, 9999);
        if (Equals(Document, d) && _file != null && _web != null)
        {
            GoToPage(page, search);
            return;
        }
        _search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var newDoc = !Equals(Document, d);
        Document = d;
        var changed = page != _page;
        _page = page;
        SyncBar();
        ClearBody();
        try
        {
            _file = DocumentCache.Extract(_s.Archive, _s.CacheDir, d.EntryName);
            if (d.Kind == DocumentKind.Text)
            {
                _body.Controls.Add(new TextBox
                {
                    Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                    Font = new Font("Consolas", 9f), Text = File.ReadAllText(_file), BackColor = SystemColors.Window,
                });
                if (newDoc) DocumentOpened?.Invoke();
                if (changed) PageChanged?.Invoke(_page);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _file = null;
            Fail("This document could not be opened: " + ex.Message, ex);
            if (newDoc) DocumentOpened?.Invoke();
            return;
        }
        if (!_allowWeb) ShowText($"{d.Title} (page {_page})" + (_search != null ? $" — find \"{_search}\"" : "") + "\r\n\r\nThe document viewer is not started in smoke mode.");
        else
        {
            AddWeb();
            StartInit();
        }
        if (newDoc) DocumentOpened?.Invoke();
        if (changed) PageChanged?.Invoke(_page);
    }

    /// <summary>Re-navigates to the given 1-based page (PDF only); <paramref name="search"/> as in <see cref="Open"/> (a plain page move clears it).</summary>
    public void GoToPage(int page, string? search = null)
    {
        page = Math.Clamp(page, 1, 9999);
        var changed = page != _page;
        _page = page;
        _search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        SyncBar();
        Navigate();
        if (changed) PageChanged?.Invoke(_page);
    }

    void SyncBar()
    {
        if (_pageBox == null || _docs == null) return;
        _loading = true;
        try
        {
            if (_pageBox.Value != _page) _pageBox.Value = _page;
            _docs.SelectedIndex = Document == null ? -1 : _docList.IndexOf(Document);
        }
        finally { _loading = false; }
    }

    // ---------- WebView2 lifecycle ----------

    void AddWeb()
    {
        _web = new WebView2 { Dock = DockStyle.Fill };
        _body.Controls.Add(_web);
    }

    void StartInit()
    {
        if (_web == null) return;
        var web = _web;
        var ticket = _gate.Next(); // one init per control instance; a newer ticket makes older inits stale
        void Go() => _ = InitAsync(web, ticket); // InitAsync has its own guarded error handling
        if (IsHandleCreated) BeginInvoke(new Action(Go));
        else
        {
            void OnHandle(object? s, EventArgs e) { HandleCreated -= OnHandle; Go(); }
            HandleCreated += OnHandle;
        }
    }

    // Moving between MDI and desktop recreates the handle, which can leave a WebView2 control blank. The control is
    // discarded before the move and rebuilt (same file, same page) after it.
    public void BeforeReparent()
    {
        if (_web == null || IsDisposed) return;
        _gate.Invalidate();
        _body.Controls.Remove(_web);
        _web.Dispose();
        _web = null;
        _rebuildWeb = true;
    }

    public void AfterReparent()
    {
        if (!_rebuildWeb || IsDisposed) return;
        _rebuildWeb = false;
        AddWeb();
        StartInit();
    }

    static string UserDataFolder => AppDataRoot.Sub("webview2");

    bool Stale(WebView2 web, int ticket) =>
        IsDisposed || Disposing || !_gate.IsCurrent(ticket) || !ReferenceEquals(web, _web);

    async Task InitAsync(WebView2 web, int ticket)
    {
        try
        {
            if (Stale(web, ticket)) return;
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            if (Stale(web, ticket)) return;
            await web.EnsureCoreWebView2Async(env);
            if (Stale(web, ticket)) return;
            Navigate();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (!Stale(web, ticket)) ShowMissingRuntime();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (Stale(web, ticket)) return; // superseded or disposed: not an error
            Fail("The document viewer could not be started: " + ex.Message, ex);
        }
    }

    void Navigate()
    {
        try
        {
            if (_web?.CoreWebView2 == null || _file == null || Document == null) return;
            var uri = new Uri(_file).AbsoluteUri;
            if (Document.Kind == DocumentKind.Pdf) uri += SearchPanelModel.ViewerFragment(_page, _search);
            _web.CoreWebView2.Navigate(uri);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    // ---------- used on this page ----------

    /// <summary>Shows the "Used on this page" list under the toolbar (workbench only).</summary>
    public void EnableUsedOnPage()
    {
        if (_usedPanel != null) _usedPanel.Visible = true;
    }

    /// <summary>Fills "Used on this page" with (kind, id, text) lines; an empty list shows a greyed note.</summary>
    public void SetUsedOnPage(IReadOnlyList<(string Kind, string Id, string Text)> items)
    {
        if (_used == null) return;
        _used.BeginUpdate();
        try
        {
            _used.Items.Clear();
            _usedIds.Clear();
            foreach (var (kind, id, text) in items) { _usedIds.Add((kind, id)); _used.Items.Add(text); }
            _used.ForeColor = items.Count == 0 ? SystemColors.GrayText : SystemColors.ControlText;
            if (items.Count == 0) _used.Items.Add(Document == null ? "No document open." : "Nothing in the rules pack points here.");
        }
        finally { _used.EndUpdate(); }
    }

    /// <summary>The "Used on this page" lines (empty when nothing points here); for smoke checks.</summary>
    public IReadOnlyList<string> UsedOnPageLines =>
        _used == null || _usedIds.Count == 0 ? Array.Empty<string>() : _used.Items.Cast<object>().Select(o => o.ToString() ?? "").ToList();

    void ActivateUsed()
    {
        if (_used == null || _used.SelectedIndex < 0 || _used.SelectedIndex >= _usedIds.Count) return;
        var (kind, id) = _usedIds[_used.SelectedIndex];
        UsedItemActivated?.Invoke(kind, id);
    }

    // ---------- messages ----------

    void ClearBody()
    {
        _gate.Invalidate();
        if (_web != null) { _body.Controls.Remove(_web); _web.Dispose(); _web = null; }
        foreach (var c in _body.Controls.Cast<Control>().ToList()) { _body.Controls.Remove(c); c.Dispose(); }
    }

    void Fail(string message, Exception ex)
    {
        Program.AppLog("document viewer failed: " + ex);
        _s.Log.Add(DiagnosticSeverity.Warning, "document", message, Document?.Title ?? "");
        ShowText(message);
    }

    void ShowText(string text)
    {
        if (IsDisposed || Disposing) return;
        ClearBody();
        _body.Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(12), Text = text });
    }

    void ShowMissingRuntime()
    {
        ClearBody();
        var link = new LinkLabel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoSize = false };
        link.Text = "The Microsoft Edge WebView2 runtime is not installed, so this document cannot be shown.\r\n\r\nInstall it from " + RuntimeUrl;
        var start = link.Text.IndexOf(RuntimeUrl, StringComparison.Ordinal);
        link.LinkArea = new LinkArea(start, RuntimeUrl.Length);
        link.LinkClicked += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RuntimeUrl) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        };
        _body.Controls.Add(link);
    }
}
