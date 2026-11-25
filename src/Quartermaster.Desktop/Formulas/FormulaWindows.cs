using Avalonia.Controls;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Formulas;

/// <summary>Links a formula window to one unit of a pending run: Apply marks that unit done. Moved is the measured distance for OnMove
/// runs (WinForms RunLink; null = unknown).</summary>
public sealed record RunLink(string RunId, double? Moved);

/// <summary>The open formula windows (WinForms FormulaDialog.Show / CloseAllFor): one window per formula, unit and run.</summary>
public static class FormulaWindows
{
    static readonly List<FormulaWindow> Open = new();
    static readonly List<BatchGridWindow> Batches = new();

    /// <summary>The registry key of a run's batch grid.</summary>
    internal static string BatchKey(string runId) => "batch:" + runId;

    /// <summary>Shows the batch grid of a pending run (non-modal, owned by <paramref name="host"/>), or activates the one already open for it.
    /// <paramref name="openRow"/> is called with the row index when a Unit cell is double-clicked. Null (nothing opens) when the run is no longer
    /// pending.</summary>
    public static BatchGridWindow? ShowBatch(MainWindow host, ModuleSession s, IActionPort port, string runId, Action<int>? openRow)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(port);
        var key = BatchKey(runId);
        var existing = Batches.FirstOrDefault(w => ReferenceEquals(w.Session, s) && w.Key == key);
        if (existing != null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        if (s.Game?.State.Pending.Any(r => r != null && r.Id == runId) != true) return null;   // finished meanwhile: nothing to open
        var win = new BatchGridWindow(host, s, port, runId, openRow);
        Batches.Add(win);
        win.Show(host);
        return win;
    }

    /// <summary>
    /// Opens a menu batch grid (B4): one row per counter in <paramref name="pieceIds"/> (a counter may repeat; only those the viewer may act on
    /// and the formula applies to are taken), keyed <c>menu:{guid}</c>, so every call opens a new grid. <paramref name="firstRowTyped"/> is what
    /// was typed into the first row's Input and Piece cells (variable to text). Null, with the refusal in the host's status bar, when no row
    /// is left.
    /// </summary>
    public static BatchGridWindow? ShowMenuBatch(MainWindow host, ModuleSession s, IActionPort port, string formulaId,
        IReadOnlyList<string> pieceIds, IReadOnlyDictionary<string, string>? firstRowTyped)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(port);
        var model = new MenuBatchModel(s, port, formulaId, pieceIds ?? Array.Empty<string>());
        if (model.RowCount == 0)
        {
            host.SetStatus(FormulaWindowRules.Refusal(s, pieceIds?.FirstOrDefault()) ?? "Nothing to calculate.");
            return null;
        }
        if (firstRowTyped != null)
            foreach (var (variable, text) in firstRowTyped)
                if (!string.IsNullOrWhiteSpace(text)) model.SetCell(0, variable, text);
        var win = new BatchGridWindow(host, s, model, "menu:" + Guid.NewGuid().ToString("N"));
        Batches.Add(win);
        win.Show(host);
        return win;
    }

    /// <summary>Shows a non-modal formula window owned by <paramref name="host"/>, or activates the one already open for the same formula, unit
    /// and run. Null when the viewer may not act on the unit (the refusal goes to the host's status bar).</summary>
    public static FormulaWindow? Show(MainWindow host, ModuleSession s, string formulaId, string? pieceId, RunLink? link)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(s);
        if (FormulaWindowRules.Refusal(s, pieceId) is { } msg)
        {
            host.SetStatus(msg);
            return null;
        }
        var key = FormulaWindowRules.Key(formulaId, pieceId, link?.RunId);
        var existing = Open.FirstOrDefault(w => ReferenceEquals(w.Session, s) && w.Key == key);
        if (existing != null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var win = new FormulaWindow(host, s, formulaId, pieceId, link);
        Open.Add(win);
        win.Show(host);
        return win;
    }

    /// <summary>Closes every open formula window of the session (a step back, a difficulty switch, the module closing).</summary>
    public static void CloseAllFor(ModuleSession s)
    {
        foreach (var w in Open.Where(w => ReferenceEquals(w.Session, s)).ToList())
        {
            try { w.Close(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                s.Log.Add(Quartermaster.Vassal.DiagnosticSeverity.Warning, "formula", "Closing a formula window failed: " + ex.Message);
                Open.Remove(w);
            }
        }
        foreach (var w in Batches.Where(w => ReferenceEquals(w.Session, s)).ToList())
        {
            try { w.Close(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                s.Log.Add(Quartermaster.Vassal.DiagnosticSeverity.Warning, "formula", "Closing a batch grid failed: " + ex.Message);
                Batches.Remove(w);
            }
            // Close runs OnClosed synchronously, which already stopped them; this makes the order explicit even if closing failed or was
            // cancelled, because the caller may dispose the session right after.
            w.StopRendering();
        }
    }

    /// <summary>The open batch grids of the session.</summary>
    internal static IReadOnlyList<BatchGridWindow> OpenBatchFor(ModuleSession s) => Batches.Where(w => ReferenceEquals(w.Session, s)).ToList();

    internal static void ClosedBatch(BatchGridWindow w) => Batches.Remove(w);

    /// <summary>How many formula windows are open (all sessions).</summary>
    public static int OpenCount => Open.Count;

    /// <summary>The open formula windows of the session.</summary>
    internal static IReadOnlyList<FormulaWindow> OpenFor(ModuleSession s) => Open.Where(w => ReferenceEquals(w.Session, s)).ToList();

    internal static void Closed(FormulaWindow w) => Open.Remove(w);
}
