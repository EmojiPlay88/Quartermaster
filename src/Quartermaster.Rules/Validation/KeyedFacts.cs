namespace Quartermaster.Rules;

/// <summary>How a keyed property is keyed (see <see cref="PropertyDef.KeyedBy"/>).</summary>
public enum KeyKind { None, Table, Turn, Hex }

/// <summary>Facts about <see cref="PropertyDef.KeyedBy"/>: the three forms, and how the Workbench shows them.</summary>
public static class KeyedFacts
{
    private const string TablePrefix = "table:";

    /// <summary>
    /// The form of a keyed-by text (trimmed, case-insensitive): <c>table:{id}</c>, <c>turn</c> or <c>hex</c>. Blank gives (None, null);
    /// any other text, and <c>table:</c> without an id, gives (None, null) too (see <see cref="IsMalformed"/>).
    /// </summary>
    public static (KeyKind Kind, string? TableId) Parse(string? keyedBy)
    {
        var s = keyedBy?.Trim();
        if (string.IsNullOrEmpty(s)) return (KeyKind.None, null);
        if (s.Equals("turn", StringComparison.OrdinalIgnoreCase)) return (KeyKind.Turn, null);
        if (s.Equals("hex", StringComparison.OrdinalIgnoreCase)) return (KeyKind.Hex, null);
        if (s.StartsWith(TablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = s.Substring(TablePrefix.Length).Trim();
            if (id.Length > 0) return (KeyKind.Table, id);
        }
        return (KeyKind.None, null);
    }

    /// <summary>True when the text is not blank but is none of the three forms.</summary>
    public static bool IsMalformed(string? keyedBy) => !string.IsNullOrWhiteSpace(keyedBy) && Parse(keyedBy).Kind == KeyKind.None;

    /// <summary>True when the property keeps one value per key. The null check comes first, so a pack without keyed properties pays nothing.</summary>
    public static bool IsKeyed(PropertyDef? p) => p?.KeyedBy is not null && Parse(p.KeyedBy).Kind != KeyKind.None;

    /// <summary>True for a place property: keyed by hex.</summary>
    public static bool IsPlace(PropertyDef? p) => p?.KeyedBy is not null && Parse(p.KeyedBy).Kind == KeyKind.Hex;

    /// <summary>The Workbench's text for a keyed-by value: <c>Turn</c>, <c>Hex (place)</c>, <c>Table: {name}</c>, blank for none; a malformed value is shown as written.</summary>
    public static string Display(RulesPack pack, string? keyedBy)
    {
        var (kind, tableId) = Parse(keyedBy);
        return kind switch
        {
            KeyKind.Turn => "Turn",
            KeyKind.Hex => "Hex (place)",
            KeyKind.Table => "Table: " + (pack.ById<TableDef>(tableId!)?.Name ?? tableId),
            _ => keyedBy?.Trim() ?? "",
        };
    }

    /// <summary>The keyed-by value for a Workbench text (the reverse of <see cref="Display"/>): null for blank; text that is none of the displayed forms is kept (trimmed) so the validator reports it.</summary>
    public static string? FromDisplay(RulesPack pack, string text)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Equals("Turn", StringComparison.OrdinalIgnoreCase)) return "turn";
        if (s.Equals("Hex (place)", StringComparison.OrdinalIgnoreCase)) return "hex";
        const string shown = "Table:";
        if (s.StartsWith(shown, StringComparison.OrdinalIgnoreCase))
        {
            var name = s.Substring(shown.Length).Trim();
            if (pack.TableByName(name) is { } t) return TablePrefix + t.Id;
        }
        return s;
    }
}
