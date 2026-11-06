using Quartermaster.Core;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

public static class HitTester
{
    /// <summary>
    /// The topmost piece (stacks later in insertion order and higher pieces win) whose bounds contain the point.
    /// Uses the piece's non-rectangular shape when it has one, otherwise its image bounds; selection-locked pieces are skipped.
    /// </summary>
    public static string? PieceAt(GameState s, MapDef map, ModuleModel model, ImageSource img, PointD mapPoint, PieceTypeCache? types = null)
    {
        types ??= PieceTypeCache.For(img);
        foreach (var st in s.StacksOn(map.Id).Reverse())
        {
            foreach (var (p, center) in PieceGeometry.Placed(s, st, map).Reverse())
            {
                var type = types.Get(p.TypeString);
                if (type.Traits.OfType<ImmobileDef>().Any(d => d.SelectionLocked)) continue;
                if (Contains(type, p, center, mapPoint, img, types.Log)) return p.Id;
            }
        }
        return null;
    }

    /// <summary>Whether the map point lies inside the piece drawn at <paramref name="center"/>.</summary>
    internal static bool Contains(PieceType type, PieceInstance p, PointD center, PointD pt, ImageSource img, DiagnosticLog log)
    {
        var dx = pt.X - center.X;
        var dy = pt.Y - center.Y;
        var rot = PieceGeometry.Rotation(type, p) * Math.PI / 180;
        var lx = (float)(dx * Math.Cos(rot) + dy * Math.Sin(rot));
        var ly = (float)(-dx * Math.Sin(rot) + dy * Math.Cos(rot));

        var nr = type.Get<NonRectangularDef>();
        if (nr != null)
        {
            var shape = ShapeParser.TryParse(nr.ShapeSpec);
            if (shape != null)
            {
                var sc = nr.Scale > 0 ? nr.Scale : 1.0;
                return shape.Contains((float)(lx / sc), (float)(ly / sc));
            }
            log.AddOnce("shape:" + nr.ShapeSpec, DiagnosticSeverity.Warning, "shape",
                "Unparsable non-rectangular piece shape; using the image rectangle instead.", nr.ShapeSpec);
        }
        var b = PieceGeometry.LocalBounds(type, p, img);
        return lx >= b.Left && lx < b.Right && ly >= b.Top && ly < b.Bottom;
    }
}
