using Avalonia.Controls;
using Quartermaster.Desktop.Ledgers;

namespace Quartermaster.Desktop;

/// <summary>
/// View &gt; Ledger (Ctrl/Cmd+L, WinForms MainForm.ShowLedger / OpenLedger): one Ledger window per module, owned by the main window and not
/// modal, its bounds remembered per module under the WinForms layout key "doc:ledger". Needs a rules pack (<c>CommandAvailability</c> "view.ledger").
/// </summary>
public partial class MainWindow
{
    LedgerWindow? _ledgerWin;

    /// <summary>The open Ledger window, or null.</summary>
    public LedgerWindow? Ledger => _ledgerWin;

    void InitLedger() => _router.SetHandler("view.ledger", () => Available(Avail("view.ledger"), () => OpenLedger()));

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
