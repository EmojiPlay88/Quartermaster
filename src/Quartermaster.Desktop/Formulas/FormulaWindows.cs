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
    /// <paramref name="openRow"/> is called with the row index when a Unit cell is double-clicked.</summary>
    public static BatchGridWindow ShowBatch(MainWindow host, ModuleSession s, IActionPort port, string runId, Action<int>? openRow)
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
        var win = new BatchGridWindow(host, s, port, runId, openRow);
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
