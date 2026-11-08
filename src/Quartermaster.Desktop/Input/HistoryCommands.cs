using Quartermaster.Render;

namespace Quartermaster.Desktop.Input;

/// <summary>
/// Undo / Redo commands (edit.undo, edit.redo, edit.redoAlt) wired to the shared <see cref="HistoryController"/>, as WinForms
/// MainForm.History: they run through the action port when there is one, are off for an online guest and while a turn is replayed,
/// and decline (letting the key through) when there is nothing to undo or redo — the WinForms menu item would be disabled then.
/// </summary>
public static class HistoryCommands
{
    /// <summary>True when Undo is available now (the Edit menu item's enabled state).</summary>
    public static bool CanUndo(ModuleSession s, HistoryController h) => !s.IsGuest && !s.IsReplaying && h.CanUndo;

    /// <summary>True when Redo is available now.</summary>
    public static bool CanRedo(ModuleSession s, HistoryController h) => !s.IsGuest && !s.IsReplaying && h.CanRedo;

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
}
