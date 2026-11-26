using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>The outcome of checking a hand-typed Ledger value (see <see cref="LedgerViewRules.CheckManualEdit"/>).
/// <paramref name="Reload"/>: the window should redraw its rows (refused or unchanged edits).</summary>
public sealed record ManualEditCheck(bool Ok, double Value, bool Unchanged, string? Error, string? NotePrompt, bool Reload = false);

/// <summary>One row of the keyed values panel (B6) over the viewer's state: <paramref name="Value"/> is null when <paramref name="Hidden"/> (the
/// view may lack it, so neither a value nor the Default is shown).</summary>
public sealed record KeyedCell(PropertyDef Property, string Key, double? Value, bool Stored, bool Hidden);

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

    /// <summary>Checks a typed value: not a computed property, a number, an editable side, a real change; then yields the note prompt.
    /// <paramref name="old"/> is the current stored value (null when none); <paramref name="propertyName"/> is shown in the prompt (default: the id).</summary>
    public static ManualEditCheck CheckManualEdit(ModuleSession s, string key, string propertyId, double? old, string text, string? propertyName = null)
    {
        if (ComputedRefusal(s.Rules, propertyId) is { } refused) return new ManualEditCheck(false, 0, false, refused, null, Reload: true);
        if (NotKeptRefusal(s.Rules, key, propertyId) is { } notKept) return new ManualEditCheck(false, 0, false, notKept, null, Reload: true);
        if (!TryParseNumber(text, out var after))
            return new ManualEditCheck(false, 0, false, $"'{text}' is not a number.", null);
        if (s.Game == null) return new ManualEditCheck(false, after, false, null, null);
        if (!s.CanEditLedger(key))
            return new ManualEditCheck(false, after, false, "That value " + ModuleSession.OtherSideText + ".", null, Reload: true);
        if (old is { } cur && cur == after) return new ManualEditCheck(true, after, true, null, null, Reload: true);
        return new ManualEditCheck(true, after, false, null, $"Why is {propertyName ?? propertyId} being set to {LedgerTable.FormatNumber(after)}? (required)");
    }

    /// <summary>"{name} is computed (= {expr}) and cannot be edited" when the property is computed (its value is never stored), else null.</summary>
    public static string? ComputedRefusal(RulesPack? pack, string? propertyId) =>
        pack?.Properties?.FirstOrDefault(p => p != null && p.Id == propertyId) is { } def && PropertyFacts.IsComputed(def)
            ? $"{def.Name} is computed (= {def.Expr!.Trim()}) and cannot be edited"
            : null;

    /// <summary>"{holder} does not keep {property}" when <paramref name="key"/> is a holder that does not keep the property (its
    /// <see cref="PropertyDef.Holders"/> list leaves the holder out), else null. The holder is "Global" for the global holder.</summary>
    public static string? NotKeptRefusal(RulesPack? pack, string key, string? propertyId)
    {
        if (pack == null || !LedgerKey.TryParse(key, out var isPiece, out var holderId) || isPiece) return null;
        if (pack.Properties?.FirstOrDefault(p => p != null && p.Id == propertyId) is not { } def || def.Scope == PropertyScope.Pieces) return null;
        if (PropertyFacts.KeptBy(def, holderId)) return null;
        var holder = holderId == HolderDef.GlobalId ? "Global" : pack.ById<HolderDef>(holderId)?.Name ?? holderId;
        return $"{holder} does not keep {def.Name}";
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

    // ---------- keyed values (B6) ----------

    /// <summary>
    /// The stored form of <paramref name="valueKey"/> for a manual edit of <paramref name="prop"/> at <paramref name="ledgerKey"/>
    /// (<see cref="KeyedValues.NormaliseKey"/>); null when the property is not keyed, a place is edited off the Global holder (places are kept
    /// by Global only), the key is missing or longer than <see cref="KeyedValues.MaxKeyLength"/>, or it does not normalise.
    /// </summary>
    public static string? EditKey(RulesPack pack, Quartermaster.Vassal.ModuleModel? model, PropertyDef prop,
        string ledgerKey, string? valueKey)
    {
        if (pack == null || !KeyedFacts.IsKeyed(prop) || valueKey == null || valueKey.Length > KeyedValues.MaxKeyLength) return null;
        if (KeyedFacts.IsPlace(prop) && ledgerKey != LedgerKey.Holder(HolderDef.GlobalId)) return null;
        try { return KeyedValues.NormaliseKey(pack, model, prop, Quartermaster.Rules.Expr.Value.Of(valueKey)); }
        catch (Quartermaster.Rules.Expr.EvalError) { return null; }
    }

    /// <summary>The working of a manual keyed edit: <c>{Prop}[{key}] {before} → {after}</c>.</summary>
    public static string KeyedWorking(PropertyDef prop, string valueKey, double before, double after) =>
        $"{(string.IsNullOrWhiteSpace(prop.Name) ? prop.Id : prop.Name)}[{valueKey}] {LedgerTable.FormatNumber(before)} → {LedgerTable.FormatNumber(after)}";

    /// <summary>The manual delta writing <paramref name="after"/> at the normalised <paramref name="valueKey"/> (Before: the stored value or the
    /// Default); null when the value is already stored as <paramref name="after"/> (an unchanged value records nothing).</summary>
    public static LedgerDelta? KeyedDelta(GameState state, PropertyDef prop, string ledgerKey, string valueKey, double after, string note)
    {
        if (KeyedValues.Get(state, ledgerKey, prop.Id, valueKey) is { } cur && cur == after) return null;
        var before = KeyedValues.GetOrDefault(state, ledgerKey, prop, valueKey);
        return new LedgerDelta(ledgerKey, prop.Id, before, after, "manual", null, null, note, KeyedWorking(prop, valueKey, before, after)) { ValueKey = valueKey };
    }

    /// <summary>A local manual keyed edit (<see cref="LocalActions.EditKeyed"/>): null when the property is unknown, computed, not kept by the
    /// holder, not keyed, the key is bad (<see cref="EditKey"/>) or the value unchanged.</summary>
    public static LedgerDelta? KeyedEdit(RulesPack pack, Quartermaster.Vassal.ModuleModel? model, GameState state, string ledgerKey,
        string propertyId, string? valueKey, double after, string note)
    {
        if (pack?.Properties?.FirstOrDefault(p => p != null && p.Id == propertyId) is not { } prop || state == null || ledgerKey == null) return null;
        if (ComputedRefusal(pack, prop.Id) != null || NotKeptRefusal(pack, ledgerKey, prop.Id) != null) return null;
        return EditKey(pack, model, prop, ledgerKey, valueKey) is { } vk ? KeyedDelta(state, prop, ledgerKey, vk, after, note) : null;
    }

    /// <summary>
    /// The keyed values panel of a ledger row over the viewer's state (<see cref="KeyedTable.For"/>): a row the view may lack (another side's
    /// non-Public value, a holder of a side not held) is <see cref="KeyedCell.Hidden"/> with no value, never the property's Default.
    /// </summary>
    public static IReadOnlyList<KeyedCell> KeyedRows(GameState view, RulesPack pack, string ledgerKey) =>
        KeyedTable.For(view, pack, ledgerKey).Select(r => !r.Stored && KeyedValues.HiddenIn(view, pack, ledgerKey, r.Property)
            ? new KeyedCell(r.Property, r.Key, null, false, true)
            : new KeyedCell(r.Property, r.Key, r.Value, r.Stored, false)).ToList();

    /// <summary>The Value cell of the keyed panel: the number, or <see cref="ModuleSession.HiddenText"/> for a hidden row.</summary>
    public static string KeyedValueText(KeyedCell c) => c.Value is { } v ? LedgerTable.FormatNumber(v) : ModuleSession.HiddenText;

    /// <summary>The Places tab over the viewer's state (<see cref="KeyedTable.Places"/>); in a player view the place properties it may not see
    /// (Private places) are left out as columns.</summary>
    public static PlacesResult Places(GameState view, RulesPack pack, string filter)
    {
        var all = KeyedTable.Places(view, pack, filter);
        if (view == null || !view.IsPlayerView) return all;
        var global = LedgerKey.Holder(HolderDef.GlobalId);
        var keep = Enumerable.Range(0, all.Columns.Count).Where(i => !KeyedValues.HiddenIn(view, pack, global, all.Columns[i])).ToList();
        if (keep.Count == all.Columns.Count) return all;
        return new PlacesResult(keep.Select(i => all.Columns[i]).ToList(),
            all.Rows.Select(r => new PlaceRow(r.Hex, keep.Select(i => r.Values[i]).ToList())).Where(r => r.Values.Any(v => v != null)).ToList());
    }
}
