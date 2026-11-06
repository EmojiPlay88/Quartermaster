using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>MDI child showing one document: PDF/HTML in WebView2, text in a read-only TextBox.</summary>
sealed class PdfWindow : Form
{
    public const string RuntimeUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    WebView2? _web;
    readonly string? _file;
    int _page;

    public PdfWindow(ModuleSession s, DocumentDef d, int page = 1)
    {
        Document = d;
        _page = Math.Max(1, page);
        Text = d.Title;
        ShowIcon = false;
        try
        {
            _file = Extract(s, d);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowMessage("This document could not be extracted: " + ex.Message);
            return;
        }
        if (d.Kind == DocumentKind.Text)
        {
            Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Font = new Font("Consolas", 9f), Text = File.ReadAllText(_file), BackColor = SystemColors.Window,
            });
            return;
        }
        _web = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_web);
        Shown += async (_, _) => await InitAsync();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public DocumentDef Document { get; }

    public static string SanitizeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return t.Length == 0 ? "document" : t;
    }

    static string Extract(ModuleSession s, DocumentDef d)
    {
        var dir = Path.Combine(s.CacheDir, "docs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SanitizeName(d.EntryName));
        if (!File.Exists(path))
        {
            var tmp = path + ".tmp";
            using (var src = s.Archive.OpenEntry(d.EntryName))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                src.CopyTo(dst);
            File.Move(tmp, path, overwrite: true);
        }
        return path;
    }

    static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster", "webview2");

    async Task InitAsync()
    {
        if (_web == null || IsDisposed) return;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            await _web.EnsureCoreWebView2Async(env);
            Navigate();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowMissingRuntime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Program.AppLog("webview2 failed: " + ex.Message);
            ShowMessage("The document viewer could not be started: " + ex.Message);
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

    void ShowMessage(string text) =>
        Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(12), Text = text });
}
