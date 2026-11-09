using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>What double-clicking / pressing Enter on an outliner row does.</summary>
public enum OutlinerActivation { CentrePiece, OpenMap, Toggle }

/// <summary>UI-free outliner behaviour shared by the WinForms and Desktop panels: which rows start open, the flattened row list, activation, aggregate explanations, severity colours and the "Now" list texts.</summary>
public static class OutlinerViewRules
{
    public const string NowText = "Now";
    public const string NothingWaitingText = "(nothing waiting)";

    public static string NowCountText(int count) => $"Now — {count}";

    /// <summary>The "Now" label: plain when nothing waits, else with the count.</summary>
    public static string NowLabel(int count) => count == 0 ? NowText : NowCountText(count);

    /// <summary>New folders start open, groups (and pieces) start closed.</summary>
    public static bool StartsExpanded(OutlinerNode n) => n.Kind == OutlinerNodeKind.Folder;

    /// <summary>Every node of the given subtrees, depth first, parents before children.</summary>
    public static IEnumerable<OutlinerNode> AllNodes(IEnumerable<OutlinerNode> ns)
    {
        foreach (var n in ns)
        {
            yield return n;
            foreach (var c in AllNodes(n.Children)) yield return c;
        }
    }

    /// <summary>The visible rows: a node's children follow it only while its key is in <paramref name="expandedKeys"/>.</summary>
    public static IReadOnlyList<(OutlinerNode Node, int Depth)> Flatten(OutlinerTree t, ISet<string> expandedKeys)
    {
        var rows = new List<(OutlinerNode, int)>();
        void Add(OutlinerNode n, int depth)
        {
            rows.Add((n, depth));
            if (n.Children.Count > 0 && expandedKeys.Contains(n.Key)) foreach (var c in n.Children) Add(c, depth + 1);
        }
        foreach (var r in t.Roots) Add(r, 0);
        return rows;
    }

    /// <summary>The org-display map a "map:" group stands for (by id, else by name), or null.</summary>
    public static MapDef? OrgDisplayFor(OutlinerNode n, ModuleModel m)
    {
        if (n.Kind != OutlinerNodeKind.Group || !n.Key.StartsWith("map:", StringComparison.Ordinal)) return null;
        var id = n.Key["map:".Length..];
        var map = m.Maps.FirstOrDefault(x => x.Id == id) ?? m.Maps.FirstOrDefault(x => x.Name == id);
        return map != null && map.IsOrgDisplay ? map : null;
    }

    /// <summary>Pieces centre on the map, org-display groups open their window, anything else toggles (a no-op without children).</summary>
    public static OutlinerActivation Activate(OutlinerNode n, ModuleModel m)
    {
        if (n.Kind == OutlinerNodeKind.Piece && n.PieceId != null) return OutlinerActivation.CentrePiece;
        if (OrgDisplayFor(n, m) != null) return OutlinerActivation.OpenMap;
        return OutlinerActivation.Toggle;
    }

    static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The heading of an aggregate number's explanation, e.g. "Fuel total 7  (Main Map)".</summary>
    public static string AggregateTitle(OutlinerColumn col, OutlinerNode node, double value, bool visibleOnly)
    {
        var how = col.Aggregate switch { OutlinerAggregate.Min => "lowest", OutlinerAggregate.Average => "average", _ => "total" };
        var text = $"{col.Name} {how} {Num(value)}  ({node.Name})";
        return visibleOnly ? text + " — " + ModuleSession.VisibleOnlyText : text;
    }

    /// <summary>The pieces under <paramref name="node"/> that add up to its value in column <paramref name="columnIndex"/>; null when the node has no value there.</summary>
    public static Breakdown? AggregateBreakdown(OutlinerColumn col, OutlinerNode node, int columnIndex, bool visibleOnly)
    {
        if (columnIndex < 0 || columnIndex >= node.Values.Count || node.Values[columnIndex] is not { } value) return null;
        var parts = new List<(string, double)>();
        foreach (var leaf in AllNodes(new[] { node }).Where(x => x.Kind == OutlinerNodeKind.Piece))
            if (columnIndex < leaf.Values.Count && leaf.Values[columnIndex] is { } v) parts.Add((leaf.Name, v));
        return LedgerBreakdown.ForTotal(AggregateTitle(col, node, value, visibleOnly), parts);
    }

    /// <summary>Alert dot colour: critical firebrick, warning dark orange, otherwise steel blue.</summary>
    public static (byte R, byte G, byte B) SeverityColour(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => (178, 34, 34),
        AlertSeverity.Warning => (255, 140, 0),
        _ => (70, 130, 180),
    };

    /// <summary>The open decisions of the "Now" list.</summary>
    public static IReadOnlyList<Decision> NowList(PhaseView v) => v.Decisions;
}
