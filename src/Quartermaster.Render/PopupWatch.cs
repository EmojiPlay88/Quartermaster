using System.Drawing;

namespace Quartermaster.Render;

/// <summary>
/// Decides when an open hover popup must close although the map saw no mouse event: the popup is click-through away from its ledger
/// lines, so the pointer can leave it straight onto something other than the map (a docked panel, the toolbar, another window).
/// </summary>
public static class PopupWatch
{
    /// <param name="popup">The popup's screen bounds.</param>
    /// <param name="cursor">The cursor in screen coordinates.</param>
    /// <param name="cursorOverCanvas">True when the window under the cursor is the map itself (its own hover logic then decides).</param>
    public static bool ShouldHide(Rectangle popup, Point cursor, bool cursorOverCanvas) =>
        !popup.Contains(cursor) && !cursorOverCanvas;
}
