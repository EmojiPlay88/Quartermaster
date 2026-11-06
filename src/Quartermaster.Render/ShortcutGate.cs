namespace Quartermaster.Render;

/// <summary>Decides whether a bound shortcut may fire given what has focus, so typing and text editing keep working.</summary>
public static class ShortcutGate
{
    static readonly HashSet<string> EditKeys = new() { "Left", "Right", "Up", "Down", "Home", "End", "PageUp", "PageDown", "Backspace", "Delete" };
    static readonly HashSet<string> EditLetters = new() { "A", "C", "V", "X", "Z", "Y" };

    /// <param name="shortcut">Canonical shortcut string.</param>
    /// <param name="textFocused">A text-entry control (text box, combo edit, grid editor, numeric box) has focus.</param>
    /// <param name="mapFocused">A map canvas has focus.</param>
    public static bool ShouldDispatch(string shortcut, bool textFocused, bool mapFocused)
    {
        if (CommandRegistry.IsTextEntryKey(shortcut)) return mapFocused && !textFocused;
        if (!textFocused) return true;
        var key = shortcut[(shortcut.LastIndexOf('+') + 1)..];
        if (EditKeys.Contains(key)) return false;
        return !(shortcut.Contains("Ctrl+") && !shortcut.Contains("Alt+") && EditLetters.Contains(key));
    }
}
