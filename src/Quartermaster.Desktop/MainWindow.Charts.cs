using Avalonia.Controls;
using Quartermaster.Desktop.Documents;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop;

/// <summary>
/// Chart windows (WinForms MainForm.OpenChild with ChartWindow): one per chart (key "name|file"), owned by the main window and not modal,
/// their bounds remembered per module under "chart:{key}". All of them close with the session.
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<string, ChartWindow> _chartWins = new();

    /// <summary>The open chart windows by key (tests).</summary>
    internal IReadOnlyDictionary<string, ChartWindow> ChartWindows => _chartWins;

    /// <summary>Opens the chart's window, or brings the one already open to the front.</summary>
    internal void OpenChart(ChartDef chart)
    {
        if (_session is not { } s) return;
        var key = chart.Name + "|" + chart.FileName;
        if (_chartWins.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var w = new ChartWindow(s, chart);
        _chartWins[key] = w;
        var module = s.Archive.Sha256;
        var layoutKey = "chart:" + key;
        if (!Restore(w, module, layoutKey)) w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        w.Closing += (_, _) => SaveBounds(w, module, layoutKey);
        w.Closed += (_, _) => { if (_chartWins.TryGetValue(key, out var cur) && ReferenceEquals(cur, w)) _chartWins.Remove(key); };
        w.Show(this);
    }

    /// <summary>Before the session goes: every chart window closes.</summary>
    void DestroyCharts()
    {
        var all = _chartWins.Values.ToList();
        _chartWins.Clear();
        foreach (var w in all)
            try { w.Close(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("closing a chart window: " + ex.Message); }
    }
}
