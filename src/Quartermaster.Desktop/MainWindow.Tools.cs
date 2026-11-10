using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Desktop.Tools;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>
/// The Inventory, Notes and Diagnostics windows (WinForms MainForm.Inventory and ShowDiagnostics), the status-bar link to the diagnostics and
/// the notes flush. One window of each kind per module, owned by the main window and not modal; their bounds are remembered per module under
/// the WinForms layout keys ("doc:inventory", "doc:notes", "diag").
/// </summary>
public partial class MainWindow
{
    InventoryWindow? _inventoryWin;
    NotesWindow? _notesWin;
    DiagnosticsWindow? _diagWin;
    bool _noticePending;
    int _problemCount;

    /// <summary>The open tool windows (null while closed).</summary>
    internal InventoryWindow? InventoryTool => _inventoryWin;
    internal NotesWindow? NotesTool => _notesWin;
    internal DiagnosticsWindow? DiagnosticsTool => _diagWin;

    /// <summary>True while the status bar message is the clickable diagnostics notice.</summary>
    internal bool StatusIsLink => _problemCount > 0;

    void InitTools()
    {
        // WinForms MainForm.BuildStatus: clicking the message opens the diagnostics while there is a problem to see.
        SbMessage.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(SbMessage).Properties.IsLeftButtonPressed && _problemCount > 0) Guard(ShowDiagnostics);
        };
        _router.SetHandler("view.diagnostics", () => Available(Avail("view.diagnostics"), ShowDiagnostics));
    }

    void CreateTools(ModuleSession s)
    {
        s.Log.Added += OnLogAdded;
        RefreshProblemNotice();
    }

    /// <summary>Before the session goes: pending notes are committed, the tool windows close and the log is let go.</summary>
    void DestroyTools()
    {
        FlushNotes();
        foreach (var w in new Window?[] { _inventoryWin, _notesWin, _diagWin })
            try { w?.Close(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("closing a tool window: " + ex.Message); }
        _inventoryWin = null;
        _notesWin = null;
        _diagWin = null;
        if (_session != null) _session.Log.Added -= OnLogAdded;
        _problemCount = 0;
        ShowLinkStyle(false);
    }

    void ShowInventory()
    {
        if (_session is not { } s) return;
        Open(ref _inventoryWin, "doc:inventory", () => new InventoryWindow(s, CentreOnPiece));
    }

    void ShowNotes()
    {
        if (_session is not { } s) return;
        Open(ref _notesWin, "doc:notes", () => new NotesWindow(s, () => Actions));
    }

    void ShowDiagnostics()
    {
        if (_session is not { } s) return;
        Open(ref _diagWin, "diag", () => new DiagnosticsWindow(s.Log));
    }

    /// <summary>Commits an open notes window's pending text into the current game (before it is replaced or closed).</summary>
    void FlushNotes() => _notesWin?.FlushNow();

    /// <summary>Shows the window made by <paramref name="create"/>, or brings the one already open to the front (single instance).</summary>
    void Open<T>(ref T? slot, string layoutKey, Func<T> create) where T : Window
    {
        if (slot is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var w = create();
        slot = w;
        var key = _session!.Archive.Sha256;
        var restored = Restore(w, key, layoutKey);
        if (!restored) w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.Closing += (_, _) => SaveBounds(w, key, layoutKey);
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_inventoryWin, w)) _inventoryWin = null;
            if (ReferenceEquals(_notesWin, w)) _notesWin = null;
            if (ReferenceEquals(_diagWin, w)) _diagWin = null;
            if (ReferenceEquals(_workbenchWin, w)) _workbenchWin = null;
        };
        w.Show(this);
    }

    /// <summary>Puts a window where the module's layout memory had it, moved onto a screen that exists now.</summary>
    bool Restore(Window w, string moduleKey, string layoutKey)
    {
        if (!_layout.LoadModule(moduleKey).TryGetValue(layoutKey, out var saved)) return false;
        var screens = ScreensProvider?.Invoke() ?? PanelFloat.ScreensOf(this);
        var fitted = ScreenFit.Restore(saved.Bounds, saved.Dpi, screens, screens[0]);
        var scale = RenderScaling;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Position = new PixelPoint(fitted.X, fitted.Y);
        w.Width = Math.Max(1, fitted.Width / scale);
        w.Height = Math.Max(1, fitted.Height / scale);
        return true;
    }

    void SaveBounds(Window w, string moduleKey, string layoutKey)
    {
        try
        {
            var scale = w.RenderScaling;
            var rect = new System.Drawing.Rectangle(w.Position.X, w.Position.Y, (int)Math.Round(w.Width * scale), (int)Math.Round(w.Height * scale));
            var all = new Dictionary<string, SavedWindow>(_layout.LoadModule(moduleKey)) { [layoutKey] = new SavedWindow(true, rect, false, (int)Math.Round(scale * 96)) };
            _layout.SaveModule(moduleKey, all);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("saving a tool window's bounds: " + ex.Message); }
    }

    // ---------- the status-bar notice ----------

    // Log.Added can fire from any thread, and an exception here would propagate into the logger.
    void OnLogAdded(Diagnostic d)
    {
        try
        {
            if (d.Severity < DiagnosticSeverity.Warning || _noticePending) return;
            _noticePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _noticePending = false;
                RefreshProblemNotice();
            });
        }
        catch (Exception) { /* shutting down */ }
    }

    void RefreshProblemNotice()
    {
        var items = _session?.Log.Items;
        var warnings = items?.Count(d => d.Severity == DiagnosticSeverity.Warning) ?? 0;
        var errors = items?.Count(d => d.Severity > DiagnosticSeverity.Warning) ?? 0;
        _problemCount = warnings + errors;
        SbMessage.Text = DiagnosticsView.StatusLinkText(warnings, errors);
        ShowLinkStyle(_problemCount > 0);
    }

    /// <summary>A WinForms link label: blue, underlined, hand cursor.</summary>
    void ShowLinkStyle(bool link)
    {
        if (link) SbMessage.Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0xFF));
        else SbMessage.ClearValue(TextBlock.ForegroundProperty);   // a null local value would hide the text
        SbMessage.TextDecorations = link ? TextDecorations.Underline : null;
        SbMessage.Cursor = link ? new Cursor(StandardCursorType.Hand) : null;
    }
}
