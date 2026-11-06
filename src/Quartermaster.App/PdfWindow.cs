using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>MDI child showing one document: PDF/HTML in WebView2, text in a read-only TextBox.</summary>
sealed class PdfWindow : Form, WindowManager.IReparentAware
{
    public const string RuntimeUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    readonly ModuleSession _s;
    WebView2? _web;
    string? _file;
    int _page;

    public PdfWindow(ModuleSession s, DocumentDef d, int page = 1)
    {
        _s = s;
        Document = d;
        _page = Math.Max(1, page);
        Text = d.Title;
        ShowIcon = false;
        try
        {
            _file = DocumentCache.Extract(s.Archive, s.CacheDir, d.EntryName);
            if (d.Kind == DocumentKind.Text)
            {
                Controls.Add(new TextBox
                {
                    Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                    Font = new Font("Consolas", 9f), Text = File.ReadAllText(_file), BackColor = SystemColors.Window,
                });
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail("This document could not be opened: " + ex.Message, ex);
            return;
        }
        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);
        Shown += async (_, _) => await InitAsync();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DocumentDef Document { get; }

    bool _rebuildWeb;

    // Moving between MDI and desktop recreates this window's handle, which can leave a WebView2 control blank. The control is
    // discarded before the move and rebuilt (same file, same page) after it.
    void WindowManager.IReparentAware.BeforeReparent()
    {
        if (_web == null || IsDisposed) return;
        Controls.Remove(_web);
        _web.Dispose();
        _web = null;
        _rebuildWeb = true;
    }

    void WindowManager.IReparentAware.AfterReparent()
    {
        if (!_rebuildWeb || IsDisposed) return;
        _rebuildWeb = false;
        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);
        BeginInvoke(new Action(async () => await InitAsync())); // InitAsync has its own guarded error handling
    }

    void Fail(string message, Exception ex)
    {
        Program.AppLog("document window failed: " + ex);
        _s.Log.Add(DiagnosticSeverity.Warning, "document", message, Document.Title);
        ShowMessage(message);
    }

    static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster", "webview2");

    async Task InitAsync()
    {
        try
        {
            if (_web == null || IsDisposed || Disposing) return;
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            if (IsDisposed || Disposing) return;
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
        if (_web?.CoreWebView2 == null || _file == null) return;
        var uri = new Uri(_file).AbsoluteUri;
        if (Document.Kind == DocumentKind.Pdf) uri += "#page=" + _page;
        _web.CoreWebView2.Navigate(uri);
    }

    /// <summary>Re-navigates to the given 1-based page (PDF only).</summary>
    public void GoToPage(int page)
    {
        _page = Math.Max(1, page);
        Navigate();
    }

    void ShowMissingRuntime()
    {
        if (_web != null) { Controls.Remove(_web); _web.Dispose(); _web = null; }
        var link = new LinkLabel { Dock = DockStyle.Fill, Padding = new Padding(12), AutoSize = false };
        link.Text = "The Microsoft Edge WebView2 runtime is not installed, so this document cannot be shown.\r\n\r\nInstall it from " + RuntimeUrl;
        var start = link.Text.IndexOf(RuntimeUrl, StringComparison.Ordinal);
        link.LinkArea = new LinkArea(start, RuntimeUrl.Length);
        link.LinkClicked += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RuntimeUrl) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        };
        Controls.Add(link);
    }

    void ShowMessage(string text)
    {
        if (IsDisposed || Disposing) return;
        if (_web != null) { Controls.Remove(_web); _web.Dispose(); _web = null; }
        Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(12), Text = text });
    }
}