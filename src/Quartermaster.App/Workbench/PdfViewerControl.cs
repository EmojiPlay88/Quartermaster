using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Quartermaster.Render;
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
    WebView2? _web;
    string? _file;
    int _page = 1;
    bool _rebuildWeb, _loading;

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
        Controls.Add(_bar);
        prev.Click += (_, _) => GoToPage(_page - 1);
        next.Click += (_, _) => GoToPage(_page + 1);
        _pageBox.ValueChanged += (_, _) => { if (!_loading) GoToPage((int)_pageBox.Value); };
        _docs.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && _docs.SelectedIndex >= 0 && _docs.SelectedIndex < _docList.Count) Open(_docList[_docs.SelectedIndex]);
        };
        ShowText(_docList.Count == 0 ? "This module has no PDF documents." : "Choose a document above.");
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DocumentDef? Document { get; private set; }

    /// <summary>The current 1-based page (the page field's value).</summary>
    public int Page => _page;

    public event Action<int>? PageChanged;

    public void Open(DocumentDef d, int page = 1)
    {
        if (IsDisposed || Disposing) return;
        page = Math.Clamp(page, 1, 9999);
        if (Equals(Document, d) && _file != null && _web != null)
        {
            GoToPage(page);
            return;
        }
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
                if (changed) PageChanged?.Invoke(_page);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _file = null;
            Fail("This document could not be opened: " + ex.Message, ex);
            return;
        }
        if (!_allowWeb) ShowText($"{d.Title} (page {_page})\r\n\r\nThe document viewer is not started in smoke mode.");
        else
        {
            AddWeb();
            StartInit();
        }
        if (changed) PageChanged?.Invoke(_page);
    }

    /// <summary>Re-navigates to the given 1-based page (PDF only).</summary>
    public void GoToPage(int page)
    {
        page = Math.Clamp(page, 1, 9999);
        var changed = page != _page;
        _page = page;
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
        if (IsHandleCreated) BeginInvoke(new Action(async () => await InitAsync())); // InitAsync has its own guarded error handling
        else HandleCreated += OnFirstHandle;
    }

    void OnFirstHandle(object? s, EventArgs e)
    {
        HandleCreated -= OnFirstHandle;
        StartInit();
    }

    // Moving between MDI and desktop recreates the handle, which can leave a WebView2 control blank. The control is
    // discarded before the move and rebuilt (same file, same page) after it.
    public void BeforeReparent()
    {
        if (_web == null || IsDisposed) return;
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
        BeginInvoke(new Action(async () => await InitAsync()));
    }

    static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster", "webview2");

    async Task InitAsync()
    {
        try
        {
            if (_web == null || IsDisposed || Disposing) return;
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            if (IsDisposed || Disposing || _web == null) return;
            await _web.EnsureCoreWebView2Async(env);
            if (IsDisposed || Disposing) return;
            Navigate();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (!IsDisposed && !Disposing) ShowMissingRuntime();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsDisposed || Disposing) return;
            Fail("The document viewer could not be started: " + ex.Message, ex);
        }
    }

    void Navigate()
    {
        try
        {
            if (_web?.CoreWebView2 == null || _file == null || Document == null) return;
            var uri = new Uri(_file).AbsoluteUri;
            if (Document.Kind == DocumentKind.Pdf) uri += "#page=" + _page;
            _web.CoreWebView2.Navigate(uri);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    // ---------- messages ----------

    void ClearBody()
    {
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
