using Quartermaster.Core;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>Where a dropped group lands: the (snapped) position and the stack it joins, if any.</summary>
public sealed record DropTarget(PointD Pos, string? StackOnto);

/// <summary>Decides snap point and stack target for a drop on a map. UI-free.</summary>
public static class DropResolver
{
    public static DropTarget Resolve(GameState s, MapDef map, PointD drop, IReadOnlyCollection<string> draggedIds, ImageSource images)
    {
        if (!map.Stacking.Disabled && StackAt(s, map, drop, draggedIds, images) is { } st)
            return new DropTarget(st.Pos, st.Id);
        return new DropTarget(Snap(map, drop), null);
    }

    /// <summary>
    /// Drag point to target: the grabbed piece's centre (cursor + grab offset) is what gets snapped or stacked onto,
    /// and all dragged pieces land at that single point.
    /// </summary>
    public static DropTarget ResolveDrag(GameState s, MapDef map, PointD cursor, PointD grabOffset, IReadOnlyCollection<string> draggedIds, ImageSource images) =>
        Resolve(s, map, new PointD(cursor.X + grabOffset.X, cursor.Y + grabOffset.Y), draggedIds, images);

    /// <summary>Snaps with the grid of the board under the point, in board-local coordinates.</summary>
    public static PointD Snap(MapDef map, PointD p)
    {
        foreach (var b in map.Boards)
        {
            var lx = p.X - b.Offset.X;
            var ly = p.Y - b.Offset.Y;
            if (lx < 0 || ly < 0 || lx >= b.Width || ly >= b.Height) continue;
            if (b.Grid == null) return p;
            var q = b.Grid.Snap(new PointD(lx, ly));
            return new PointD(q.X + b.Offset.X, q.Y + b.Offset.Y);
        }
        return p;
    }

    private static StackInstance? StackAt(GameState s, MapDef map, PointD drop, IReadOnlyCollection<string> dragged, ImageSource images)
    {
        var types = PieceTypeCache.For(images);
        foreach (var st in s.StacksOn(map.Id).Reverse())
        {
            if (st.PieceIds.All(dragged.Contains)) continue;
            foreach (var (p, center) in PieceGeometry.Placed(s, st, map))
                if (HitTester.Contains(types.Get(p.TypeString), p, center, drop, images, types.Log)) return st;
        }
        return null;
    }
}

/// <summary>Rubber-band selection helpers.</summary>
public static class SelectionRect
{
    public static RectD FromCorners(PointD a, PointD b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>Pieces on the map whose centre lies in the rectangle (selection-locked pieces excluded), bottom to top.</summary>
    public static IReadOnlyList<string> PieceIdsIn(GameState s, MapDef map, RectD r, ImageSource images)
    {
        var types = PieceTypeCache.For(images);
        var ids = new List<string>();
        foreach (var st in s.StacksOn(map.Id))
            foreach (var (p, c) in PieceGeometry.Placed(s, st, map))
            {
                if (types.Get(p.TypeString).Traits.OfType<ImmobileDef>().Any(d => d.SelectionLocked)) continue;
                if (c.X >= r.X && c.X <= r.X + r.W && c.Y >= r.Y && c.Y <= r.Y + r.H) ids.Add(p.Id);
            }
        return ids;
    }
}

public static class ActionMenu
{
    /// <summary>Splits actions into runs by <see cref="PieceAction.Group"/>, groups in first-seen order.</summary>
    public static IReadOnlyList<IReadOnlyList<PieceAction>> Group(IEnumerable<PieceAction> actions) =>
        actions.GroupBy(a => a.Group).Select(g => (IReadOnlyList<PieceAction>)g.ToList()).ToList();
}

public static class FitView
{
    /// <summary>Extent of all boards in map coordinates.</summary>
    public static RectD Extent(MapDef map)
    {
        if (map.Boards.Count == 0) return new RectD(0, 0, 1, 1);
        var x0 = map.Boards.Min(b => b.Offset.X);
        var y0 = map.Boards.Min(b => b.Offset.Y);
        var x1 = map.Boards.Max(b => b.Offset.X + b.Width);
        var y1 = map.Boards.Max(b => b.Offset.Y + b.Height);
        return new RectD(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    public static void ZoomToFit(Viewport vp, MapDef map, SKSize client)
    {
        var e = Extent(map);
        if (client.Width < 1 || client.Height < 1) return;
        vp.Zoom = (float)Math.Min(client.Width / e.W, client.Height / e.H);
        Center(vp, new PointD(e.X + e.W / 2, e.Y + e.H / 2), client);
    }

    public static void Center(Viewport vp, PointD mapPoint, SKSize client) =>
        vp.Pan = new SKPoint((float)(mapPoint.X - client.Width / 2.0 / vp.Zoom), (float)(mapPoint.Y - client.Height / 2.0 / vp.Zoom));
}

/// <summary>What the hover detail viewer shows for one piece.</summary>
public sealed record PieceDetail(string PieceId, string Name, IReadOnlyList<(string Key, string Value)> Properties);

public static class PieceDetails
{
    /// <summary>The piece's name plus its mark (static), PROP (dynamic) and label properties.</summary>
    public static PieceDetail For(PieceInstance p, PieceType type)
    {
        var props = new List<(string, string)>();
        for (var i = 0; i < type.Traits.Count; i++)
        {
            switch (type.Traits[i])
            {
                case MarkerDef m:
                {
                    // mark;name1,name2;value1,value2 (names and values are parsed here; MarkerDef.Keys only holds the first part reliably)
                    var parts = m.RawType.Split(';');
                    var names = parts.Length > 1 ? parts[1].Split(',') : Array.Empty<string>();
                    var values = parts.Length > 2 ? parts[2].Split(',') : Array.Empty<string>();
                    for (var k = 0; k < names.Length; k++)
                        props.Add((names[k], k < values.Length ? values[k] : ""));
                    break;
                }
                case DynamicPropertyDef d:
                    props.Add((d.Key, TraitState.DynamicValue(d, PieceGeometry.StateAt(p, i))));
                    break;
                case LabelDef l:
                    props.Add((l.Name, TraitState.LabelText(l, PieceGeometry.StateAt(p, i))));
                    break;
            }
        }
        var name = type.Basic.Name;
        return new PieceDetail(p.Id, name.Length > 0 ? name : "piece", props);
    }
}
