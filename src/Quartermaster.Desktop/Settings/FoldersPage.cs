using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

public sealed class FoldersPage : SettingsPage
{
    readonly SettingsContext _ctx;
    internal readonly TextBlock Sizes = new() { Text = "Calculating sizes..." };
    internal readonly Button ClearTiles = new() { Content = "Clear tile cache" };
    internal readonly Button ClearDocs = new() { Content = "Clear document cache" };
    bool _started, _closed;

    /// <summary>How the sizes are computed (a test seam).</summary>
    internal Func<string, (long Tiles, long Docs)> SizesOf { get; set; } = CacheFolders.Sizes;

    public FoldersPage(SettingsContext ctx)
    {
        _ctx = ctx;
        ClearTiles.Click += (_, _) => Clear("tiles");
        ClearDocs.Click += (_, _) => Clear("docs");
        var t = Table();
        Full(t, new TextBlock { Text = "Cache folder:" });
        Full(t, new TextBox { Text = ctx.CacheRoot, IsReadOnly = true, Width = 440, HorizontalAlignment = HorizontalAlignment.Left });
        Full(t, Sizes);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(ClearTiles);
        row.Children.Add(ClearDocs);
        Full(t, row);
        Full(t, new TextBlock { Text = "The module that is open keeps its cache while it is open." });
        Content = Host(t);
        // The sizes are computed the first time the page is shown (a page that is not selected is not in the visual tree).
        AttachedToVisualTree += (_, _) =>
        {
            if (_started) return;
            if (TopLevel.GetTopLevel(this) is Window w) w.Closed += (_, _) => _closed = true;
            Refresh();
        };
    }

    public override string Title => "Folders";

    /// <summary>True once the sizes have been computed and shown.</summary>
    internal bool SizesLoaded { get; private set; }

    /// <summary>Runs work on a worker thread and posts done back to the UI thread (dropped when the window is gone).</summary>
    void OffUi<T>(Func<T> work, Action<T> done)
    {
        var ui = Dispatcher.UIThread;   // taken on the UI thread: the continuation runs on a worker and must not reach the static getter
        _ = Task.Run(work).ContinueWith(r => ui.Post(() =>
        {
            if (_closed) return;
            if (r.IsFaulted)
            {
                ClearTiles.IsEnabled = ClearDocs.IsEnabled = true;
                Sizes.Text = "The folder could not be read: " + r.Exception?.GetBaseException().Message;
                return;
            }
            done(r.Result);
        }));
    }

    void Refresh()
    {
        _started = true;
        Sizes.Text = "Calculating sizes...";
        OffUi(() => SizesOf(_ctx.CacheRoot), r =>
        {
            Sizes.Text = $"Tile cache: {Mb(r.Tiles)}     Document cache: {Mb(r.Docs)}";
            SizesLoaded = true;
        });
    }

    static string Mb(long bytes) => (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    internal void Clear(string kind)
    {
        var open = _ctx.OpenModuleCache();
        ClearTiles.IsEnabled = ClearDocs.IsEnabled = false;
        Sizes.Text = "Clearing...";
        SizesLoaded = false;
        OffUi(() => CacheFolders.Clear(_ctx.CacheRoot, kind, open), r =>
        {
            ClearTiles.IsEnabled = ClearDocs.IsEnabled = true;
            _ = NotifyThenRefresh($"Cleared {Mb(r.Freed)} of cached {(kind == "tiles" ? "tiles" : "documents")}." +
                (r.SkippedOpen ? "\nThe open module's files are in use and were left alone; close the module to clear them." : ""));
        });
    }

    async Task NotifyThenRefresh(string text)
    {
        try { await _ctx.Notify(text); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("settings folders: " + ex.Message); }
        if (!_closed) Refresh();
    }

    public override void Load(Settings s) { }
    public override Settings Collect(Settings s) => s;
}
