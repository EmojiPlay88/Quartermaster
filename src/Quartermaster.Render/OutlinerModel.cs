using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

public enum OutlinerGrouping { OrgDisplays, BySide }

public enum OutlinerNodeKind { Folder, Group, Piece }

public sealed record OutlinerColumn(string PropertyId, string Name, OutlinerAggregate Aggregate, string? Notes);

public sealed record OutlinerNode(string Key, string Name, OutlinerNodeKind Kind, string? PieceId, string? MapId, int Units,
    IReadOnlyList<double?> Values, IReadOnlyList<AlertSeverity> Alerts, IReadOnlyList<OutlinerNode> Children);

public sealed record OutlinerTree(IReadOnlyList<OutlinerColumn> Columns, IReadOnlyList<OutlinerNode> Roots);

/// <summary>Pure model behind the outliner: folders and groups of pieces with aggregated property values and alert severities.</summary>
public static class OutlinerModel
{
    private const string OtherName = "Other";

    private sealed record Leaf(PieceInstance Piece, string Name, double?[] Values, AlertSeverity[] Alerts);

    public static OutlinerTree Build(GameState s, RulesPack? pack, ModuleModel model, OutlinerGrouping grouping, IReadOnlyList<AlertHit> alerts) =>
        Build(s, pack, model, grouping, alerts, PieceInfo.Name);

    /// <summary>As above with the piece-name lookup injectable (tests); a lookup that throws for a piece falls back to the piece id.</summary>
    internal static OutlinerTree Build(GameState s, RulesPack? pack, ModuleModel model, OutlinerGrouping grouping, IReadOnlyList<AlertHit> alerts,
        Func<PieceInstance, string> nameOf)
    {
        var props = (pack == null ? new List<PropertyDef>() : LedgerTable.Columns(pack).Where(p => p.Outliner != OutlinerAggregate.None).ToList());
        var cols = props.Select(p => new OutlinerColumn(p.Id, p.Name, p.Outliner, p.Notes)).ToList();

        var bySeverity = new Dictionary<string, HashSet<AlertSeverity>>(StringComparer.Ordinal);
        foreach (var h in alerts ?? Array.Empty<AlertHit>())
            foreach (var m in h.Matches)
                if (m.PieceId != null)
                {
                    if (!bySeverity.TryGetValue(m.PieceId, out var set)) bySeverity[m.PieceId] = set = new();
                    set.Add(h.Severity);
                }

        var leaves = new Dictionary<string, Leaf>(StringComparer.Ordinal);
        foreach (var p in s.Pieces.Values)
        {
            string name;
            try { name = nameOf(p) ?? ""; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { name = ""; } // one bad piece type must not blank the tree
            var vals = props.Select(c => Ledger.Get(s, LedgerKey.Piece(p.Id), c.Id)).ToArray();
            var sev = bySeverity.TryGetValue(p.Id, out var set) ? set.OrderByDescending(x => x).ToArray() : Array.Empty<AlertSeverity>();
            leaves[p.Id] = new Leaf(p, name.Length > 0 ? name : p.Id, vals, sev);
        }

        static IEnumerable<Leaf> Sorted(IEnumerable<Leaf> l) =>
            l.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Piece.Id, StringComparer.Ordinal);

        OutlinerNode Group(string key, string name, IEnumerable<Leaf> members)
        {
            var list = Sorted(members).ToList();
            var kids = list.Select(l => new OutlinerNode("piece:" + l.Piece.Id, l.Name, OutlinerNodeKind.Piece, l.Piece.Id, l.Piece.MapId, 1,
                l.Values, l.Alerts, Array.Empty<OutlinerNode>())).ToList();
            return Node(key, name, OutlinerNodeKind.Group, kids, list);
        }

        OutlinerNode Node(string key, string name, OutlinerNodeKind kind, IReadOnlyList<OutlinerNode> kids, List<Leaf> members)
        {
            var values = new double?[props.Count];
            for (var i = 0; i < props.Count; i++)
                values[i] = Aggregate(props[i].Outliner, members.Select(m => m.Values[i]).Where(v => v != null).Select(v => v!.Value).ToList());
            var alertSet = members.SelectMany(m => m.Alerts).Distinct().OrderByDescending(x => x).ToList();
            return new OutlinerNode(key, name, kind, null, null, members.Count, values, alertSet, kids);
        }

        var roots = new List<OutlinerNode>();
        if (grouping == OutlinerGrouping.BySide)
        {
            var rest = leaves.Values.ToList();
            if (pack != null)
                foreach (var side in pack.Sides)
                {
                    var match = PieceFilter.Compile(side.UnitFilter, s, pack);
                    var mine = rest.Where(l => match(l.Piece)).ToList();
                    rest = rest.Except(mine).ToList();
                    if (mine.Count > 0) roots.Add(Group("side:" + side.Id, side.Name, mine));
                }
            if (rest.Count > 0) roots.Add(Group("side:other", OtherName, rest));
        }
        else
        {
            var mapOf = new Dictionary<string, MapDef>(StringComparer.Ordinal);
            foreach (var m in model.Maps) mapOf.TryAdd(m.Id, m);
            foreach (var m in model.Maps) mapOf.TryAdd(m.Name, m);
            var byMap = new Dictionary<string, List<Leaf>>(StringComparer.Ordinal);
            var unknown = new List<string>();
            foreach (var l in leaves.Values)
            {
                var key = mapOf.TryGetValue(l.Piece.MapId, out var md) ? md.Id : l.Piece.MapId;
                if (!byMap.TryGetValue(key, out var list)) byMap[key] = list = new();
                list.Add(l);
                if (md == null && !unknown.Contains(key)) unknown.Add(key);
            }

            // Org displays, nested into folders by NavPath (first-seen order).
            var rootFolders = new List<FolderBuild>();
            foreach (var m in model.Maps.Where(x => x.IsOrgDisplay))
            {
                if (!byMap.TryGetValue(m.Id, out var members)) continue;
                var folders = rootFolders;
                FolderBuild? parent = null;
                var path = "";
                foreach (var seg in m.NavPath)
                {
                    path += "/" + seg;
                    var f = folders.FirstOrDefault(x => x.Name == seg);
                    if (f == null) folders.Add(f = new FolderBuild(path, seg));
                    parent = f;
                    folders = f.Folders;
                }
                var g = Group("map:" + m.Id, m.Name, members);
                if (parent == null) roots.Add(g); else parent.Groups.Add((g, members));
            }
            var folderNodes = rootFolders.Select(f => ToNode(f, Node)).ToList();
            roots.InsertRange(0, folderNodes);

            foreach (var m in model.Maps.Where(x => !x.IsOrgDisplay))
                if (byMap.TryGetValue(m.Id, out var members)) roots.Add(Group("map:" + m.Id, m.Name, members));
            foreach (var k in unknown) roots.Add(Group("map:" + k, k, byMap[k]));
        }
        return new OutlinerTree(cols, roots);
    }

    private sealed class FolderBuild(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public List<FolderBuild> Folders { get; } = new();
        public List<(OutlinerNode Node, List<Leaf> Members)> Groups { get; } = new();
    }

    private static OutlinerNode ToNode(FolderBuild f, Func<string, string, OutlinerNodeKind, IReadOnlyList<OutlinerNode>, List<Leaf>, OutlinerNode> node)
    {
        var kids = new List<OutlinerNode>();
        var members = new List<Leaf>();
        foreach (var sub in f.Folders)
        {
            var n = ToNode(sub, node);
            kids.Add(n);
            members.AddRange(Collect(sub));
        }
        foreach (var (g, m) in f.Groups) { kids.Add(g); members.AddRange(m); }
        return node("folder:" + f.Key, f.Name, OutlinerNodeKind.Folder, kids, members);
    }

    private static IEnumerable<Leaf> Collect(FolderBuild f) =>
        f.Folders.SelectMany(Collect).Concat(f.Groups.SelectMany(g => g.Members));

    private static double? Aggregate(OutlinerAggregate how, List<double> v)
    {
        if (v.Count == 0) return null;
        return how switch
        {
            OutlinerAggregate.Sum => v.Sum(),
            OutlinerAggregate.Min => v.Min(),
            OutlinerAggregate.Average => v.Average(),
            _ => null,
        };
    }
}
