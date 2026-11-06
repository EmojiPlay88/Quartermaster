namespace Quartermaster.Render;

/// <summary>A user-visible command with a default shortcut ("" = none by default).</summary>
public sealed record CommandDef(string Id, string Label, string DefaultShortcut);

/// <summary>
/// Command ids, default shortcuts and user overrides. Shortcuts are canonical strings: modifiers in the order Ctrl+Alt+Shift, then the key
/// ("Ctrl+Shift+W", "F5", "L", "Ctrl+,"). Input is case-insensitive. No WinForms here; the App maps these strings to <c>Keys</c>.
/// </summary>
public sealed class CommandRegistry
{
    static readonly HashSet<string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tab", "Enter", "Space", "Esc", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown", "Left", "Right", "Up", "Down",
    };

    static readonly string Punctuation = ",.;'/" + '\\' + "[]-=`";

    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Control"] = "Ctrl", ["Escape"] = "Esc", ["Return"] = "Enter", ["Del"] = "Delete", ["Ins"] = "Insert", ["PgUp"] = "PageUp", ["PgDn"] = "PageDown", ["Back"] = "Backspace",
    };

    /// <summary>Keys the window itself uses (tab switching and closing, focus, dialogs); they cannot be bound to commands. Alt+letter (menu mnemonics) is covered by <see cref="IsReserved"/>.</summary>
    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string> { "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+W", "Alt+F4", "Esc", "Tab", "Shift+Tab", "Enter" };

    /// <summary>True for a canonical shortcut the window keeps for itself: <see cref="Reserved"/> or Alt plus a letter (menu mnemonics).</summary>
    public static bool IsReserved(string canonical) =>
        Reserved.Contains(canonical) || (canonical.StartsWith("Alt+", StringComparison.Ordinal) && canonical.Length == 5 && char.IsAsciiLetter(canonical[4]));

    readonly List<CommandDef> _all = new();

    public IReadOnlyList<CommandDef> All => _all;

    /// <summary>Adds a command; a repeated id replaces the earlier definition.</summary>
    public void Register(CommandDef d)
    {
        var i = _all.FindIndex(x => x.Id == d.Id);
        if (i >= 0) _all[i] = d; else _all.Add(d);
    }

    public CommandDef? Find(string id) => _all.FirstOrDefault(d => d.Id == id);

    /// <summary>The key bound to the command now: its override when valid ("" = unbound), otherwise the default. "" when unbound or unknown.</summary>
    public string EffectiveShortcut(string id, IReadOnlyDictionary<string, string> overrides)
    {
        var d = Find(id);
        if (d == null) return "";
        if (overrides.TryGetValue(id, out var o))
        {
            if (o.Trim().Length == 0) return "";
            if (TryNormalize(o, out var n)) return n;
        }
        return TryNormalize(d.DefaultShortcut, out var def) ? def : "";
    }

    /// <summary>Keys bound to two or more commands, plus reserved keys bound to any command.</summary>
    public IReadOnlyList<(string Key, IReadOnlyList<string> Ids)> Conflicts(IReadOnlyDictionary<string, string> overrides) =>
        _all.Select(d => (d.Id, Key: EffectiveShortcut(d.Id, overrides)))
            .Where(x => x.Key.Length > 0)
            .GroupBy(x => x.Key)
            .Where(g => g.Count() > 1 || IsReserved(g.Key))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(x => x.Id).ToList()))
            .ToList();

    /// <summary>Parses "ctrl+shift+w" style text into the canonical form. Exactly one key, optional distinct modifiers.</summary>
    public static bool TryNormalize(string? text, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        // The key is the last token; a trailing '+' would be the plus key, which is not supported.
        var parts = t.Split('+');
        if (parts.Any(p => p.Length == 0)) return false;
        bool ctrl = false, alt = false, shift = false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var m = parts[i].Trim();
            if (Aliases.TryGetValue(m, out var a)) m = a;
            if (m.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) { if (ctrl) return false; ctrl = true; }
            else if (m.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { if (alt) return false; alt = true; }
            else if (m.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { if (shift) return false; shift = true; }
            else return false;
        }
        var key = CanonicalKey(parts[^1].Trim());
        if (key == null) return false;
        canonical = (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + key;
        return true;
    }

    static string? CanonicalKey(string k)
    {
        if (Aliases.TryGetValue(k, out var a)) k = a;
        if (k.Length == 1)
        {
            var c = k[0];
            if (char.IsAsciiLetter(c)) return char.ToUpperInvariant(c).ToString();
            if (char.IsAsciiDigit(c) || Punctuation.Contains(c)) return k;
            return null;
        }
        if ((k[0] is 'F' or 'f') && int.TryParse(k.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
            return "F" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return NamedKeys.TryGetValue(k, out var named) ? named : null;
    }

    /// <summary>True for a key that types text (no Ctrl or Alt, single character key); such shortcuts must not fire while typing.</summary>
    public static bool IsTextEntryKey(string canonical)
    {
        if (canonical.Contains("Ctrl+") || canonical.Contains("Alt+")) return false;
        var key = canonical[(canonical.LastIndexOf('+') is var i and > 0 && i < canonical.Length - 1 ? i + 1 : 0)..];
        return key.Length == 1;
    }
}
