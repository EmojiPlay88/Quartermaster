using Avalonia.Controls;
using Quartermaster.Desktop.Ledgers;

namespace Quartermaster.Desktop;

/// <summary>
/// View &gt; Ledger (Ctrl/Cmd+L, WinForms MainForm.ShowLedger / OpenLedger): one Ledger window per module, owned by the main window and not
/// modal, its bounds remembered per module under the WinForms layout key "doc:ledger". Needs a rules pack (<c>CommandAvailability</c> "view.ledger").
/// With counters selected on the active map it is Show in Ledger (WinForms MainForm.ShowInLedger): their rows are selected too, and the
/// status bar says how many were shown or why some have no row. The counter menu's "Show in Ledger" comes here through the map's services.
/// </summary>
public partial class MainWindow
{
    LedgerWindow? _ledgerWin;

    /// <summary>The open Ledger window, or null.</summary>
    public LedgerWindow? Ledger => _ledgerWin;

    void InitLedger() => _router.SetHandler("view.ledger", () => Available(Avail("view.ledger"), ShowLedger));

    /// <summary>View &gt; Ledger / Ctrl+L: Show in Ledger for the counters selected on the active map, else just opens the Ledger.</summary>
    void ShowLedger()
    {
        if (_session?.Rules == null) return;
        var picked = Maps.ActiveView?.SelectedPieceIds.ToList() ?? new List<string>();
        if (picked.Count > 0) { ShowInLedger(picked); return; }
        OpenLedger();
    }

    /// <summary>Show in Ledger (map right-click, or Ctrl+L with counters selected): opens the Ledger and selects the counters' rows; the
    /// status bar says when some have none.</summary>
    public void ShowInLedger(IReadOnlyList<string> pieceIds)
    {
        Guard(() =>
        {
            if (OpenLedger() is not { } lw) return;
            var message = lw.ShowPieces(pieceIds);
            lw.Activate();
            var shown = lw.LastJump?.RowKeys.Count ?? 0;
            SetStatus(message ?? (shown == 1 ? "Shown in the Ledger." : $"{shown} counters shown in the Ledger."));
        });
    }

    /// <summary>True when the Ledger can be opened (a rules pack is loaded), as for View &gt; Ledger.</summary>
    public bool LedgerAvailable => _session?.Rules != null;

    /// <summary>Opens (or activates) the Ledger without touching its rows; null without a rules pack.</summary>
    public LedgerWindow? OpenLedger()
    {
        if (_session?.Rules == null) return null;
        var s = _session;
        var isNew = _ledgerWin == null;
        Open(ref _ledgerWin, "doc:ledger", () => new LedgerWindow(s, _dialogs, () => Actions, CentreOnPiece));
        if (isNew && _ledgerWin is { } w) w.Closed += (_, _) => { if (ReferenceEquals(_ledgerWin, w)) _ledgerWin = null; };
        return _ledgerWin;
    }

    /// <summary>Before the session goes: the Ledger closes (its picture workers stop) so the module can be disposed.</summary>
    void DestroyLedger()
    {
        try { _ledgerWin?.Close(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("closing the ledger: " + ex.Message); }
        _ledgerWin?.StopRendering();
        _ledgerWin = null;
    }
}
