using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>
/// The Overview window (WinForms MainForm.ShowOverview) and "Save map as picture" (WinForms MapExport.ShowAsync). The overview opens floating
/// beside the main window unless its bounds were remembered under "doc:overview"; one export runs at a time and is cancelled when the session
/// or the main window goes.
/// </summary>
public partial class MainWindow
{
    /// <summary>Caption of the export's dialogs (WinForms MessageBox caption).</summary>
    const string ExportCaption = "Save map as picture";

    OverviewWindow? _overviewWin;
    bool _exporting;
    CancellationTokenSource? _exportCts;

    /// <summary>The open overview window (null while closed).</summary>
    internal OverviewWindow? OverviewTool => _overviewWin;

    /// <summary>The progress dialog of the latest whole-map export, kept after it closed (tests).</summary>
    internal ExportProgressDialog? LastExportDialog { get; private set; }

    /// <summary>Called on the render thread with each progress value before it is shown (tests cancel from here).</summary>
    internal Action<double>? ExportProgressTap { get; set; }

    internal bool ExportRunning => _exporting;

    // ---------- Overview ----------

    void ShowOverview()
    {
        if (_session is not { } s) return;
        if (_overviewWin is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var w = new OverviewWindow(Maps);
        _overviewWin = w;
        var key = s.Archive.Sha256;
        if (!Restore(w, key, "doc:overview")) PlaceOverviewBesideMain(w);
        w.Closing += (_, _) => SaveBounds(w, key, "doc:overview");
        w.Closed += (_, _) => { if (ReferenceEquals(_overviewWin, w)) _overviewWin = null; };
        w.Show(this);
    }

    /// <summary>A mini-map wants to sit beside the map: (Left + Width - 300, Top + 110), 260 x 240, moved onto a screen that exists.</summary>
    void PlaceOverviewBesideMain(OverviewWindow w)
    {
        var scale = RenderScaling;
        var rect = new System.Drawing.Rectangle(Position.X + (int)Math.Round(Math.Max(0, Width - 300) * scale), Position.Y + (int)Math.Round(110 * scale),
            (int)Math.Round(260 * scale), (int)Math.Round(240 * scale));
        var screens = ScreensProvider?.Invoke() ?? PanelFloat.ScreensOf(this);
        var fitted = ScreenFit.Restore(rect, (int)Math.Round(scale * 96), screens, screens[0]);
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Position = new PixelPoint(fitted.X, fitted.Y);
        w.Width = Math.Max(1, fitted.Width / scale);
        w.Height = Math.Max(1, fitted.Height / scale);
    }

    // ---------- Save map as picture ----------

    /// <summary>The map toolbar button and the empty-map menu entry: the choice, the file picker, then the picture. Only one export runs at a time.</summary>
    internal async Task SaveMapAsPictureAsync(MapView view)
    {
        if (_exporting) { SetStatus(MapExportFlow.AlreadyRunningText); return; }
        _exporting = true;
        try { await ExportCoreAsync(view); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("map export failed: " + ex);
            await _dialogs.ShowWarningAsync(this, MapExportFlow.FailurePrefix + ex.Message, ExportCaption);
        }
        finally
        {
            _exporting = false;
            _exportCts = null;
        }
    }

    async Task ExportCoreAsync(MapView view)
    {
        if (_session is not { } s) return;
        if (s.Game == null) { await _dialogs.ShowWarningAsync(this, MapExportFlow.NoGameText, ExportCaption); return; }
        var choice = await _dialogs.AskExportChoiceAsync(this, view.Map.Name);
        if (choice == null) return;
        var path = await _dialogs.SaveFileAsync(this, ExportCaption, new FileFilter("PNG picture (*.png)", "*.png"), "png", MapExportFlow.SanitizeFileName(view.Map.Name));
        if (path == null || !ReferenceEquals(_session, s)) return;

        if (!choice.WholeMap)
        {
            try
            {
                using var bmp = view.Snapshot() ?? throw new InvalidOperationException("The view has no size.");
                MapImageExporter.SavePng(bmp, path);
                SetStatus(MapExportFlow.SavedText(path));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.WriteLine("map export failed: " + ex);
                await _dialogs.ShowWarningAsync(this, MapExportFlow.FailurePrefix + ex.Message, ExportCaption);
            }
            return;
        }

        // The tiles the view shows are reused when it has them all; else the flow builds them.
        var tiles = view.Map.Boards.All(b => view.Tiles.ContainsKey(BoardTiler.TileKey(b))) ? view.Tiles : null;
        using var cts = new CancellationTokenSource();
        _exportCts = cts;
        var dlg = new ExportProgressDialog(cts);
        LastExportDialog = dlg;
        var progress = new TapProgress(p => ExportProgressTap?.Invoke(p), p => Dispatcher.UIThread.Post(() => dlg.SetProgress(p)));
        dlg.Show(this);
        ExportResult r;
        try
        {
            // The snapshot of what this player may see is taken inside, before the first await, on this (UI) thread.
            r = await MapExportFlow.RunWholeAsync(s, view.Map.Id, choice.Zoom, path, progress, cts.Token, tiles, m => System.Diagnostics.Trace.WriteLine(m));
        }
        finally { dlg.Finish(); }
        if (!ReferenceEquals(_session, s)) return;   // the window or the game went meanwhile
        if (r.Ok) SetStatus(r.Message);
        else if (r.Cancelled) SetStatus(MapExportFlow.CancelledText);
        else await _dialogs.ShowWarningAsync(this, r.Message, ExportCaption);
    }

    /// <summary>Stops a running export (the session or the main window closes).</summary>
    internal void CancelExport()
    {
        try { _exportCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Before the session goes: the export stops and the overview closes.</summary>
    void DestroyExport()
    {
        CancelExport();
        var w = _overviewWin;
        _overviewWin = null;
        try { w?.Close(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("closing the overview: " + ex.Message); }
    }

    /// <summary>Progress reported on the render thread: first to the tap, then handed to the UI thread.</summary>
    sealed class TapProgress(Action<double> tap, Action<double> show) : IProgress<double>
    {
        public void Report(double value)
        {
            try { tap(value); }
            finally { show(value); }
        }
    }
}
