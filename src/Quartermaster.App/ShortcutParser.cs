using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>Maps the registry's canonical shortcut strings ("Ctrl+Shift+W") to WinForms <see cref="Keys"/> and back.</summary>
static class ShortcutParser
{
    static readonly (string Name, Keys Key)[] Table = BuildTable();

    static (string, Keys)[] BuildTable()
    {
        var t = new List<(string, Keys)>();
        for (var c = 'A'; c <= 'Z'; c++) t.Add((c.ToString(), Keys.A + (c - 'A')));
        for (var d = 0; d <= 9; d++) t.Add((d.ToString(), Keys.D0 + d));
        for (var f = 1; f <= 24; f++) t.Add(("F" + f, Keys.F1 + (f - 1)));
        t.AddRange(new (string, Keys)[]
        {
            ("Tab", Keys.Tab), ("Enter", Keys.Enter), ("Space", Keys.Space), ("Esc", Keys.Escape), ("Backspace", Keys.Back), ("Delete", Keys.Delete),
            ("Insert", Keys.Insert), ("Home", Keys.Home), ("End", Keys.End), ("PageUp", Keys.PageUp), ("PageDown", Keys.PageDown),
            ("Left", Keys.Left), ("Right", Keys.Right), ("Up", Keys.Up), ("Down", Keys.Down),
            (",", Keys.Oemcomma), (".", Keys.OemPeriod), (";", Keys.OemSemicolon), ("'", Keys.OemQuotes), ("/", Keys.OemQuestion),
            ("\\", Keys.OemPipe), ("[", Keys.OemOpenBrackets), ("]", Keys.OemCloseBrackets), ("-", Keys.OemMinus), ("=", Keys.Oemplus), ("`", Keys.Oemtilde),
        });
        return t.ToArray();
    }

    /// <summary>Parses a shortcut string; false for anything the registry would not accept.</summary>
    public static bool TryParse(string? text, out Keys keys)
    {
        keys = Keys.None;
        if (!CommandRegistry.TryNormalize(text, out var canon)) return false;
        var parts = canon.Split('+');
        var k = Keys.None;
        foreach (var p in parts[..^1])
            k |= p switch { "Ctrl" => Keys.Control, "Alt" => Keys.Alt, _ => Keys.Shift };
        var name = parts[^1];
        foreach (var (n, key) in Table)
            if (n == name) { keys = k | key; return true; }
        return false;
    }

    /// <summary>Canonical string for a pressed key combination, or null when the key is not one the registry supports (or only a modifier).</summary>
    public static string? Format(Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        string? name = null;
        foreach (var (n, key) in Table)
            if (key == code) { name = n; break; }
        if (name == null) return null;
        return (keyData.HasFlag(Keys.Control) ? "Ctrl+" : "") + (keyData.HasFlag(Keys.Alt) ? "Alt+" : "") + (keyData.HasFlag(Keys.Shift) ? "Shift+" : "") + name;
    }

    /// <summary>Smoke check: every default parses and round-trips through Format. Returns a problem text, or null.</summary>
    public static string? SelfCheck(CommandRegistry registry)
    {
        foreach (var d in registry.All)
        {
            if (d.DefaultShortcut.Length == 0) continue;
            if (!TryParse(d.DefaultShortcut, out var k)) return $"default '{d.DefaultShortcut}' of {d.Id} does not parse";
            if (Format(k) is not { } f || !TryParse(f, out var k2) || k2 != k) return $"default '{d.DefaultShortcut}' of {d.Id} does not round-trip";
        }
        return null;
    }
}
