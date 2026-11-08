using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

public enum LedgerTabKind { Units, Holders, Everything, User }

public enum LedgerGrouping { None, Map, Side }

/// <summary>A personal ledger tab (stored per module on this PC, never in the rules pack).</summary>
public sealed record UserLedgerTab(string Name, string? Filter, IReadOnlyList<string> ColumnIds, bool IncludeHolders, string? Notes);

/// <summary>Key: "units", "holders", "all" or "user:&lt;name&gt;".</summary>
public sealed record LedgerTabDef(string Key, string Title, LedgerTabKind Kind, UserLedgerTab? User);

/// <summary>A ledger row plus what the UI needs to group, edit and picture it. Group headers carry <see cref="IsGroupHeader"/>.</summary>
public sealed record LedgerRowEx(LedgerRow Row, string? PieceId, string? SideId, string? MapName, bool IsGroupHeader);

public sealed record LedgerTabResult(IReadOnlyList<PropertyDef> Columns, IReadOnlyList<LedgerRowEx> Rows, int Count, string? FilterError);

/// <summary>UI-free model behind the Ledger window's tabs: fixed and user tabs, columns, side filter, grouping and CSV.</summary>
public static class LedgerTabs
{
    private const string NoSide = "No side";

    /// <summary>The three fixed tabs, then the user tabs in order.</summary>
    public static IReadOnlyList<LedgerTabDef> Tabs(IReadOnlyList<UserLedgerTab> user)
    {
        var list = new List<LedgerTabDef>
        {
            new("units", "Units", LedgerTabKind.Units, null),
            new("holders", "Depots & Global", LedgerTabKind.Holders, null),
            new("all", "Everything", LedgerTabKind.Everything, null),
        };
        foreach (var u in user ?? Array.Empty<UserLedgerTab>())
            list.Add(new LedgerTabDef("user:" + u.Name, u.Name, LedgerTabKind.User, u));
        return list;
    }

    public static IReadOnlyList<PropertyDef> ColumnsFor(LedgerTabDef tab, RulesPack p)
    {
        var all = LedgerTable.Columns(p);
        switch (tab.Kind)
        {
            case LedgerTabKind.Units: return all.Where(c => c.Scope != PropertyScope.Holders).ToList();
            case LedgerTabKind.Holders: return all.Where(c => c.Scope != PropertyScope.Pieces).ToList();
            case LedgerTabKind.Everything: return all;
            default:
                var ids = tab.User?.ColumnIds ?? Array.Empty<string>();
                var picked = new List<PropertyDef>();
                foreach (var id in ids)
                {
                    var def = all.FirstOrDefault(c => c.Id == id);
                    if (def != null && !picked.Contains(def)) picked.Add(def);
                }
                return picked.Count > 0 ? picked : all.Where(c => c.Scope != PropertyScope.Holders).ToList();
        }
    }

    public static LedgerTabResult Rows(LedgerTabDef tab, GameState view, RulesPack p, ModuleModel m,
        string filter, string? sideId, LedgerGrouping group, bool includeEmpty)
    {
        var cols = ColumnsFor(tab, p);
        var allCols = LedgerTable.Columns(p);
        var map = cols.Select(c => allCols.ToList().FindIndex(a => a.Id == c.Id)).ToArray();

        string? error = null;
        if (tab.Kind == LedgerTabKind.User) error = FilterError(tab.User?.Filter);
        if (error != null) return new LedgerTabResult(cols, Array.Empty<LedgerRowEx>(), 0, error);

        var baseRows = LedgerTable.Rows(view, p, m, "", includeEmpty);
        var sideOf = Ownership.PieceSides(view, p);
        Func<PieceInstance, bool> userFilter = tab.Kind == LedgerTabKind.User ? PieceFilter.Compile(tab.User?.Filter, view, p) : _ => true;

        var holders = new List<LedgerRowEx>();
        var units = new List<LedgerRowEx>();
        foreach (var r in baseRows)
        {
            if (!LedgerKey.TryParse(r.Key, out var isPiece, out var id)) continue;
            var projected = new LedgerRow(r.Key, r.Name, r.Kind, map.Select(i => i >= 0 && i < r.Values.Count ? r.Values[i] : null).ToList());
            if (!isPiece) { holders.Add(new LedgerRowEx(projected, null, null, null, false)); continue; }
            if (!view.Pieces.TryGetValue(id, out var piece)) continue;
            var side = sideOf(piece);
            var mapObj = m.Maps.FirstOrDefault(x => x.Id == piece.MapId) ?? m.Maps.FirstOrDefault(x => x.Name == piece.MapId);
            units.Add(new LedgerRowEx(projected, id, side, mapObj?.Name ?? piece.MapId, false));
            if (tab.Kind == LedgerTabKind.User && !userFilter(piece)) units.RemoveAt(units.Count - 1);
        }

        var unitTab = tab.Kind is LedgerTabKind.Units or LedgerTabKind.User;
        if (unitTab && !string.IsNullOrEmpty(sideId)) units = units.Where(u => u.SideId == sideId).ToList();

        IEnumerable<LedgerRowEx> chosen = tab.Kind switch
        {
            LedgerTabKind.Units => units,
            LedgerTabKind.Holders => holders,
            LedgerTabKind.Everything => holders.Concat(units),
            _ => (tab.User?.IncludeHolders ?? false) ? holders.Concat(units) : units,
        };

        var f = filter?.Trim() ?? "";
        var rows = f.Length == 0
            ? chosen.ToList()
            : chosen.Where(r => r.Row.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Row.Kind.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        var count = rows.Count;

        if (unitTab && group != LedgerGrouping.None)
        {
            var lead = rows.Where(r => r.PieceId == null).ToList();
            var body = rows.Where(r => r.PieceId != null).ToList();
            var groups = new List<(string Title, List<LedgerRowEx> Items)>();
            if (group == LedgerGrouping.Map)
            {
                foreach (var r in body)
                {
                    var title = r.MapName ?? "";
                    var at = groups.FindIndex(x => x.Title == title);
                    if (at < 0) { groups.Add((title, new List<LedgerRowEx>())); at = groups.Count - 1; }
                    groups[at].Items.Add(r);
                }
            }
            else
            {
                foreach (var s in p.Sides ?? new List<SideDef>())
                {
                    var items = body.Where(r => r.SideId == s.Id).ToList();
                    if (items.Count > 0) groups.Add((s.Name, items));
                }
                var none = body.Where(r => r.SideId == null || !(p.Sides ?? new()).Any(s => s.Id == r.SideId)).ToList();
                if (none.Count > 0) groups.Add((NoSide, none));
            }
            var outRows = new List<LedgerRowEx>(lead);
            foreach (var (title, items) in groups)
            {
                outRows.Add(new LedgerRowEx(new LedgerRow("group:" + title, title, "", new double?[cols.Count]), null, null, null, true));
                outRows.AddRange(items);
            }
            rows = outRows;
        }
        return new LedgerTabResult(cols, rows, count, null);
    }

    /// <summary>Null when the expression is empty or parses; otherwise the parse error text.</summary>
    public static string? FilterError(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;
        try { Parser.Parse(expr); return null; }
        catch (Exception e) { return string.IsNullOrWhiteSpace(e.Message) ? "Invalid filter" : e.Message; }
    }

    /// <summary>CSV of the tab's columns and rows; group headers are skipped.</summary>
    public static string ToCsv(LedgerTabResult r) =>
        LedgerTable.ToCsv(r.Columns, r.Rows.Where(x => !x.IsGroupHeader).Select(x => x.Row).ToList());
}
