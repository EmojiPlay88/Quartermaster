using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>Undo, Redo and Step Back wiring: the Edit menu, toolbar buttons, shortcuts and the confirmation / list dialogs.</summary>
sealed partial class MainForm
{
    const string FloorTip = "Undo stops here: this game was loaded from a save without undo history.";

    HistoryController? _history;
    ToolStripMenuItem? _editUndo, _editRedo, _editStepBack, _editStepBackTo;
    ToolStripButton? _tbUndo, _tbRedo, _tbStepBack;
    bool _historyRefreshQueued;
    /// <summary>Status text for the rewind about to run; consumed by <see cref="OnHistoryRewound"/>, so nothing closes when the controller declined to rewind.</summary>
    string? _rewindStatus;

    /// <summary>The Edit menu, between File and View. Items stay disabled until a game with history exists.</summary>
    ToolStripMenuItem BuildEditMenu()
    {
        var edit = new ToolStripMenuItem("&Edit");
        _editUndo = Item(edit.DropDownItems, "&Undo", DoUndo, "edit.undo");
        _editRedo = Item(edit.DropDownItems, "&Redo", DoRedo, "edit.redo");
        edit.DropDownItems.Add(new ToolStripSeparator());
        _editStepBack = Item(edit.DropDownItems, "Step &Back to Start of Phase", DoStepBackToStart, "phase.stepBack");
        _editStepBackTo = Item(edit.DropDownItems, "Step Back &To...", DoStepBackTo, "phase.stepBackTo");
        edit.DropDownOpening += (_, _) => RefreshHistoryUi();
        SetCommandHandler("edit.redoAlt", () => { if (_history?.CanRedo != true) return false; DoRedo(); return true; });
        RefreshHistoryUi();
        return edit;
    }

    /// <summary>The toolbar buttons (captions are fixed; tooltips name the action).</summary>
    void BuildHistoryToolbar()
    {
        _tbUndo = Tool("edit.undo", "Undo", () => _editUndo?.PerformClick());
        _tbRedo = Tool("edit.redo", "Redo", () => _editRedo?.PerformClick());
        _tbStepBack = Tool("phase.stepBack", "◀ Step Back", () => _editStepBack?.PerformClick());
        RefreshHistoryUi();
    }

    /// <summary>Coalesces refreshes asked for by game changes (many commands can arrive in one burst).</summary>
    void RequestHistoryRefresh()
    {
        if (_historyRefreshQueued || IsDisposed || !IsHandleCreated) return;
        _historyRefreshQueued = true;
        BeginInvoke(() => { _historyRefreshQueued = false; if (!IsDisposed) RefreshHistoryUi(); });
    }

    /// <summary>Captions, enabled state and tooltips of the Edit items and toolbar buttons.</summary>
    void RefreshHistoryUi()
    {
        try
        {
            var h = _history;
            var undo = h?.CanUndo == true;
            var redo = h?.CanRedo == true;
            var start = h?.StartOfPhase();
            var any = h != null && h.Checkpoints().Count > 0;
            var floor = h?.AtFloorFromOldSave == true;

            var undoText = h?.UndoText ?? "Undo";
            var redoText = h?.RedoText ?? "Redo";
            Set(_editUndo, "&" + Mnemonic(undoText), undo, "edit.undo", !undo && floor ? FloorTip : null);
            Set(_editRedo, "&" + Mnemonic(redoText), redo, "edit.redo", null);
            Set(_editStepBack, "Step &Back to Start of Phase", start != null, "phase.stepBack", start == null && floor ? FloorTip : null);
            Set(_editStepBackTo, "Step Back &To...", any, "phase.stepBackTo", !any && floor ? FloorTip : null);

            SetButton(_tbUndo, undo, Tip(undoText, "edit.undo"), !undo && floor);
            SetButton(_tbRedo, redo, Tip(redoText, "edit.redo"), false);
            SetButton(_tbStepBack, start != null, Tip("Step back to start of phase", "phase.stepBack"), start == null && floor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("refresh history ui: " + ex.Message);
        }

        void Set(ToolStripMenuItem? mi, string text, bool enabled, string cmd, string? tip)
        {
            if (mi == null) return;
            mi.Text = text;
            mi.Enabled = enabled;
            mi.ShortcutKeyDisplayString = ShortcutText(cmd);
            mi.ToolTipText = tip ?? "";
        }
        void SetButton(ToolStripButton? b, bool enabled, string tip, bool floor)
        {
            if (b == null) return;
            b.Enabled = enabled;
            b.ToolTipText = floor ? FloorTip : tip;
        }
        string Tip(string text, string cmd) => ShortcutText(cmd) is { Length: > 0 } k ? $"{text} ({k})" : text;
    }

    /// <summary>Escapes "&" so an action label cannot turn into a menu mnemonic.</summary>
    static string Mnemonic(string text) => text.Replace("&", "&&", StringComparison.Ordinal);

    void OnHistoryRewound()
    {
        var status = _rewindStatus ?? "Rewound";
        _rewindStatus = null;
        CloseFormulaWindows(status);
        RefreshPhaseViews();
        RefreshStrip();
        RefreshHistoryUi();
    }

    void DoUndo()
    {
        if (_history is not { CanUndo: true }) return;
        _rewindStatus = "Undone";
        try { _actions!.Undo(); } finally { _rewindStatus = null; }
    }

    void DoRedo()
    {
        if (_history is not { CanRedo: true }) return;
        _rewindStatus = "Redone";
        try { _actions!.Redo(); } finally { _rewindStatus = null; }
    }

    void DoStepBackToStart()
    {
        if (_history is not { } h || h.StartOfPhase() is not { } row) return;
        using var dlg = new StepBackDialog(new[] { row }, r => HistoryController.Describe(h.Preview(r)), confirmOnly: true);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        StepBackTo(row);
    }

    void DoStepBackTo()
    {
        if (_history is not { } h) return;
        var rows = h.Checkpoints();
        if (rows.Count == 0) return;
        using var dlg = new StepBackDialog(rows, r => HistoryController.Describe(h.Preview(r)), confirmOnly: false);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Selected is not { } row) return;
        StepBackTo(row);
    }

    /// <summary>Autosaves first (a failure is logged and does not block), then steps back and closes the dialogs that belonged to the old state.</summary>
    void StepBackTo(CheckpointRow row)
    {
        if (_history == null) return;
        try
        {
            if (_autosaver?.SaveForced(DateTimeOffset.UtcNow) != null) _autosavedSeq = _changeSeq;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("step back autosave: " + ex.Message);
        }
        _rewindStatus = "Stepped back — reopen from the phase panel";
        try { _actions!.StepBack(row); } finally { _rewindStatus = null; }
    }

    /// <summary>Open formula dialogs, batch grids and cheat sheets belong to the state before a rewind or a difficulty change, so they close.
    /// Pending runs stay pending. (The missing-input dialog is modal, so none can be open while a menu command runs.)</summary>
    void CloseFormulaWindows(string status)
    {
        if (IsDisposed || _session == null) return;
        FormulaDialog.CloseAllFor(_session);
        foreach (var kv in _docs.Where(kv => kv.Key.StartsWith("batch:", StringComparison.Ordinal)).ToList())
            if (kv.Value is { IsDisposed: false } f) { try { f.Close(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("closing batch grid: " + ex.Message); } }
        SetStatus(status);
    }
}
