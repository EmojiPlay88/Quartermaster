using System.Globalization;
using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>The outcome of checking a hand-typed Ledger value (see <see cref="LedgerViewRules.CheckManualEdit"/>).
/// <paramref name="Reload"/>: the window should redraw its rows (refused or unchanged edits).</summary>
public sealed record ManualEditCheck(bool Ok, double Value, bool Unchanged, string? Error, string? NotePrompt, bool Reload = false);

/// <summary>The Ledger window's UI-free rules (sorting, manual edits, tab titles, banner, CSV name, row height), shared by every front end.</summary>
public static class LedgerViewRules
{
    /// <summary>Columns before the property columns: Name, Kind.</summary>
    public const int FixedCols = 2;
    public const int CounterRowHeight = 52;
    /// <summary>The default compact row height of the WinForms grid at 96 dpi.</summary>
    public const int DefaultRowHeight = 22;
    public const string NoNameText = "(no name)";
    public const string HolderColumnSuffix = "  (depots & Global)";

    /// <summary>Sorts by the chosen column within each group: group header rows stay where they are and keep their rows under them;
    /// blank values always sort last and ties keep their natural order.</summary>
    public static IReadOnlyList<LedgerRowEx> Sort(IReadOnlyList<LedgerRowEx> rows, int columnIndex, bool ascending)
    {
        var output = new List<LedgerRowEx>(rows.Count);
        var segment = new List<LedgerRowEx>();
        void Flush() { output.AddRange(SortSegment(segment, columnIndex, ascending)); segment.Clear(); }
        foreach (var r in rows)
        {
            if (r.IsGroupHeader) { Flush(); output.Add(r); }
            else segment.Add(r);
        }
        Flush();
        return output;
    }

    static List<LedgerRowEx> SortSegment(List<LedgerRowEx> rows, int col, bool asc)
    {
        var sign = asc ? 1 : -1;
        if (col >= FixedCols)
        {
            var vi = col - FixedCols;
            double? V(LedgerRowEx r) => vi < r.Row.Values.Count ? r.Row.Values[vi] : null;
            // Blank values always sort last; a stable order keeps ties in their natural order.
            return rows.Select((r, i) => (r, i)).OrderBy(x => V(x.r) == null ? 1 : 0)
                .ThenBy(x => (V(x.r) ?? 0) * sign).ThenBy(x => x.i).Select(x => x.r).ToList();
        }
        Func<LedgerRowEx, string> sel = col == 0 ? r => r.Row.Name : r => r.Row.Kind;
        return (asc ? rows.OrderBy(sel, StringComparer.CurrentCultureIgnoreCase) : rows.OrderByDescending(sel, StringComparer.CurrentCultureIgnoreCase)).ToList();
    }

    /// <summary>Invariant first (no thousands separators), then the current culture; false when not a finite number.</summary>
    public static bool TryParseNumber(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value)) return true;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value)) return true;
        value = 0;
        return false;
    }

    /// <summary>Checks a typed value: a number, an editable side, a real change; then yields the note prompt.
    /// <paramref name="old"/> is the current stored value (null when none); <paramref name="propertyName"/> is shown in the prompt (default: the id).</summary>
    public static ManualEditCheck CheckManualEdit(ModuleSession s, string key, string propertyId, double? old, string text, string? propertyName = null)
    {
        if (!TryParseNumber(text, out var after))
            return new ManualEditCheck(false, 0, false, $"'{text}' is not a number.", null);
        if (s.Game == null) return new ManualEditCheck(false, after, false, null, null);
        if (!s.CanEditLedger(key))
            return new ManualEditCheck(false, after, false, "That value " + ModuleSession.OtherSideText + ".", null, Reload: true);
        if (old is { } cur && cur == after) return new ManualEditCheck(true, after, true, null, null, Reload: true);
        return new ManualEditCheck(true, after, false, null, $"Why is {propertyName ?? propertyId} being set to {LedgerTable.FormatNumber(after)}? (required)");
    }

    /// <summary>Default file name of a CSV export: <c>ledger-{tab}.csv</c> with invalid characters and spaces turned into dashes.</summary>
    public static string CsvFileName(string tabTitle)
    {
        var bad = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (var c in "<>:\"/\\|?*") bad.Add(c);
        var safe = new string(tabTitle.Select(c => bad.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).ToLowerInvariant();
        return "ledger-" + safe + ".csv";
    }

    /// <summary>The tab strip's label: the fixed tabs' labels carry their row count when known.</summary>
    public static string TabTitle(LedgerTabDef t, int? count) => t.Kind switch
    {
        LedgerTabKind.User => "★ " + t.Title,
        LedgerTabKind.Everything => t.Title,
        _ => count is { } n ? $"{t.Title} ({n:N0})" : t.Title,
    };

    public static string FilterBanner(string title, string error) =>
        $"The unit filter of \"{title}\" does not parse, so this tab shows no rows: {error}   (Edit tabs… to fix it)";

    public static bool SameTabs(IReadOnlyList<UserLedgerTab> a, IReadOnlyList<UserLedgerTab> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Name == p.Second.Name && p.First.Filter == p.Second.Filter && p.First.IncludeHolders == p.Second.IncludeHolders
            && p.First.Notes == p.Second.Notes && p.First.ColumnIds.SequenceEqual(p.Second.ColumnIds));

    /// <summary>The tab key to show after the tab list was edited: after "+" (<paramref name="openNew"/>) the first added tab, else the current one.</summary>
    public static string TabToOpenAfterEdit(IReadOnlyList<UserLedgerTab> before, IReadOnlyList<UserLedgerTab> cleaned, string currentKey, bool openNew)
    {
        var had = before.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (openNew && cleaned.FirstOrDefault(t => !had.Contains(t.Name)) is { } added) return "user:" + added.Name;
        return currentKey;
    }

    /// <summary>The Side and Group-by pickers apply to the unit tabs and the player's own tabs only.</summary>
    public static bool SideAndGroupApply(LedgerTabDef tab) => tab.Kind is LedgerTabKind.Units or LedgerTabKind.User;

    /// <summary>Row height: room for a counter picture when counters are shown, else the compact height.</summary>
    public static int RowHeight(bool showCounters, int compactHeight = DefaultRowHeight) => showCounters ? CounterRowHeight : compactHeight;

    /// <summary>"New tab", then "New tab 2", ... the first name no existing tab (case-insensitive, trimmed) uses.</summary>
    public static string NewTabName(IReadOnlyList<UserLedgerTab> existing)
    {
        var n = 1;
        string name;
        do name = n == 1 ? "New tab" : "New tab " + n; while (existing.Any(t => string.Equals(t.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) && n++ < 1000);
        return name;
    }

    /// <summary>The Edit-tabs list entry of a tab.</summary>
    public static string ListLabel(UserLedgerTab t) => string.IsNullOrWhiteSpace(t.Name) ? NoNameText : "★ " + t.Name.Trim();
}
