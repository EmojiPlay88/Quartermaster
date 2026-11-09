using Quartermaster.Render;

namespace Quartermaster.Desktop.Input;

/// <summary>
/// Undo / Redo commands (edit.undo, edit.redo, edit.redoAlt) wired to the shared <see cref="HistoryController"/>, and Step Back
/// (phase.stepBack, phase.stepBackTo) wired to the shared <see cref="PhaseRunDispatch"/>, as WinForms MainForm.History: they run through the
/// action port when there is one, are off for an online guest and while a turn is replayed, and decline (letting the key through) when there
/// is nothing to undo, redo or step back to — the WinForms menu item would be disabled then.
/// </summary>
public static class HistoryCommands
{
    /// <summary>True when Undo is available now (the Edit menu item's enabled state).</summary>
    public static bool CanUndo(ModuleSession s, HistoryController h) => !s.IsGuest && !s.IsReplaying && h.CanUndo;

    /// <summary>True when Redo is available now.</summary>
    public static bool CanRedo(ModuleSession s, HistoryController h) => !s.IsGuest && !s.IsReplaying && h.CanRedo;

    /// <summary>True when Step Back to Start of Phase is available now (something was done since the current phase began).</summary>
    public static bool CanStepBack(ModuleSession s, HistoryController h) => !s.IsGuest && !s.IsReplaying && h.StartOfPhase() != null;

    /// <summary>True when Step Back To... is available now (there is a checkpoint with something done after it).</summary>
    public static bool CanStepBackTo(ModuleSession s, HistoryController h) => !s.IsGuest && !s.IsReplaying && h.Checkpoints().Count > 0;

    /// <summary>Undoes the last action if possible. <paramref name="status"/> gets the WinForms status text ("Undone", or why not).</summary>
    public static bool Undo(ModuleSession s, HistoryController h, Func<IActionPort?>? actions, Action<string>? status = null)
    {
        if (s.IsReplaying) { status?.Invoke(ModuleSession.ReplayingText); return false; }
        if (s.IsGuest) { status?.Invoke(ModuleSession.GuestUndoText); return false; }
        if (h.UndoBlockedBySentTurn) { status?.Invoke(HistoryController.AlreadySentText); return false; }
        if (!h.CanUndo) return false;
        if (actions?.Invoke() is { } port) port.Undo(); else h.Undo();
        status?.Invoke("Undone");
        return true;
    }

    /// <summary>Redoes the last undone action if possible.</summary>
    public static bool Redo(ModuleSession s, HistoryController h, Func<IActionPort?>? actions, Action<string>? status = null)
    {
        if (s.IsReplaying) { status?.Invoke(ModuleSession.ReplayingText); return false; }
        if (s.IsGuest) { status?.Invoke(ModuleSession.GuestUndoText); return false; }
        if (!h.CanRedo) return false;
        if (actions?.Invoke() is { } port) port.Redo(); else h.Redo();
        status?.Invoke("Redone");
        return true;
    }

    /// <summary>Attaches edit.undo, edit.redo and edit.redoAlt to <paramref name="router"/>.</summary>
    public static void Register(CommandRouter router, ModuleSession s, HistoryController h, Func<IActionPort?>? actions, Action<string>? status = null)
    {
        router.SetHandler("edit.undo", () => Undo(s, h, actions, status));
        router.SetHandler("edit.redo", () => Redo(s, h, actions, status));
        router.SetHandler("edit.redoAlt", () => Redo(s, h, actions, status));
    }

    /// <summary>Attaches phase.stepBack (Ctrl+Left) and phase.stepBackTo to <paramref name="router"/>. The dispatch shows the dialog through
    /// <paramref name="run"/> (the window's async runner); for an online guest or during a replay it only says why.</summary>
    public static void RegisterStepBack(CommandRouter router, ModuleSession s, HistoryController h, Func<PhaseRunDispatch?> dispatch, Action<Func<Task>> run)
    {
        router.SetHandler("phase.stepBack", () => StepBack(s, h, dispatch, run, toStart: true));
        router.SetHandler("phase.stepBackTo", () => StepBack(s, h, dispatch, run, toStart: false));
    }

    static bool StepBack(ModuleSession s, HistoryController h, Func<PhaseRunDispatch?> dispatch, Action<Func<Task>> run, bool toStart)
    {
        if (dispatch() is not { } d) return false;
        var off = s.IsGuest || s.IsReplaying;   // the dispatch says why (WinForms GuestCannotUndo)
        if (!off && !(toStart ? CanStepBack(s, h) : CanStepBackTo(s, h))) return false;
        run(toStart ? d.StepBackToStart : d.StepBackTo);
        return true;
    }
}
