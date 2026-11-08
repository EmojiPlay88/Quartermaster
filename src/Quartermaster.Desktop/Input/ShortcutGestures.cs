using Avalonia.Input;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Input;

/// <summary>Turns the registry's canonical shortcut strings ("Ctrl+Shift+W") into Avalonia gestures; on macOS Ctrl becomes Cmd (Meta).</summary>
public static class ShortcutGestures
{
    /// <summary>True on macOS. Settable so headless tests can simulate it.</summary>
    public static bool IsMac { get; set; } = OperatingSystem.IsMacOS();

    static readonly Dictionary<string, Key> Named = new(StringComparer.Ordinal)
    {
        ["Tab"] = Key.Tab, ["Enter"] = Key.Enter, ["Space"] = Key.Space, ["Esc"] = Key.Escape, ["Backspace"] = Key.Back, ["Delete"] = Key.Delete,
        ["Insert"] = Key.Insert, ["Home"] = Key.Home, ["End"] = Key.End, ["PageUp"] = Key.PageUp, ["PageDown"] = Key.PageDown,
        ["Left"] = Key.Left, ["Right"] = Key.Right, ["Up"] = Key.Up, ["Down"] = Key.Down,
        [","] = Key.OemComma, ["."] = Key.OemPeriod, [";"] = Key.OemSemicolon, ["'"] = Key.OemQuotes, ["/"] = Key.OemQuestion,
        ["\\"] = Key.OemPipe, ["["] = Key.OemOpenBrackets, ["]"] = Key.OemCloseBrackets, ["-"] = Key.OemMinus, ["="] = Key.OemPlus, ["`"] = Key.OemTilde,
    };

    static readonly Dictionary<string, string> MacKeyText = new(StringComparer.Ordinal)
    {
        ["Left"] = "←", ["Right"] = "→", ["Up"] = "↑", ["Down"] = "↓", ["Enter"] = "↩", ["Esc"] = "⎋", ["Backspace"] = "⌫", ["Delete"] = "⌦", ["Tab"] = "⇥",
    };

    static bool TryKey(string name, out Key key)
    {
        if (Named.TryGetValue(name, out key)) return true;
        if (name.Length == 1 && char.IsAsciiLetter(name[0])) { key = Key.A + (name[0] - 'A'); return true; }
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) { key = Key.D0 + (name[0] - '0'); return true; }
        if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24) { key = Key.F1 + (n - 1); return true; }
        key = Key.None;
        return false;
    }

    /// <summary>Parses a shortcut string using the runtime <see cref="IsMac"/>.</summary>
    public static KeyGesture? Parse(string spec) => Parse(spec, IsMac);

    /// <summary>Parses a shortcut string; null when the registry would not accept it. On mac, Ctrl maps to Meta (Cmd) and Alt stays Alt (Option).</summary>
    public static KeyGesture? Parse(string spec, bool mac)
    {
        if (!CommandRegistry.TryNormalize(spec, out var canon)) return null;
        var parts = canon.Split('+');
        if (!TryKey(parts[^1], out var key)) return null;
        var mods = KeyModifiers.None;
        foreach (var p in parts[..^1])
            mods |= p switch { "Ctrl" => mac ? KeyModifiers.Meta : KeyModifiers.Control, "Alt" => KeyModifiers.Alt, _ => KeyModifiers.Shift };
        return new KeyGesture(key, mods);
    }

    /// <summary>Menu text for a shortcut using the runtime <see cref="IsMac"/>.</summary>
    public static string Display(string spec) => Display(spec, IsMac);

    /// <summary>Menu text: "Ctrl+Shift+W" on Windows/Linux, "⌘⇧W" on mac (Apple order ⌃⌥⇧⌘). Empty for an unparseable spec.</summary>
    public static string Display(string spec, bool mac)
    {
        if (!CommandRegistry.TryNormalize(spec, out var canon)) return "";
        if (!mac) return canon;
        var parts = canon.Split('+');
        var key = parts[^1];
        var mods = parts[..^1];
        return (mods.Contains("Alt") ? "⌥" : "") + (mods.Contains("Shift") ? "⇧" : "") + (mods.Contains("Ctrl") ? "⌘" : "")
               + (MacKeyText.TryGetValue(key, out var t) ? t : key);
    }
}
