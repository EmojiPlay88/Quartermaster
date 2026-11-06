using Quartermaster.Core;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>Shared geometry for drawing and hit-testing: piece bounds, rotation, and where stacked pieces sit.</summary>
internal static class PieceGeometry
{
    /// <summary>Trait state i of the piece; "" (default) when it has none.</summary>
    public static string StateAt(PieceInstance p, int i) => i >= 0 && i < p.TraitStates.Count ? p.TraitStates[i] ?? "" : "";

    /// <summary>Degrees clockwise on screen from the first rotator trait; 0 when none.</summary>
    public static double Rotation(PieceType t, PieceInstance p)
    {
        for (var i = 0; i < t.Traits.Count; i++)
            if (t.Traits[i] is RotatorDef r) return TraitState.RotationAngle(r, StateAt(p, i));
        return 0;
    }

    /// <summary>Centre-relative top-left of an image of the given size.</summary>
    public static SKRect Centered(SKSizeI size, float ox = 0, float oy = 0) =>
        SKRect.Create((float)Math.Floor(-size.Width / 2.0) + ox, (float)Math.Floor(-size.Height / 2.0) + oy, size.Width, size.Height);

    /// <summary>Centre-relative bounds of the basic image united with the active layer images (unrotated).</summary>
    public static SKRect LocalBounds(PieceType t, PieceInstance p, ImageSource images)
    {
        var r = SKRect.Empty;
        var first = true;
        void Add(SKRect x) { if (first) { r = x; first = false; } else r = SKRect.Union(r, x); }
        if (t.Basic.Image.Length > 0) Add(Centered(images.SizeOf(t.Basic.Image)));
        for (var i = 0; i < t.Traits.Count; i++)
        {
            if (t.Traits[i] is not LayerDef l) continue;
            var (level, active) = TraitState.LayerLevel(l, StateAt(p, i));
            if (!active || level < 1 || level > l.Images.Count || l.Images[level - 1].Length == 0) continue;
            Add(Centered(images.SizeOf(l.Images[level - 1]), l.OffsetX, l.OffsetY));
        }
        return r;
    }

    /// <summary>Pieces of a stack bottom to top with their centres in map coordinates.</summary>
    public static IEnumerable<(PieceInstance Piece, PointD Center)> Placed(GameState s, StackInstance st, MapDef map)
    {
        var offsets = StackLayout.Offsets(st.PieceIds.Count, st.Expanded, map.Stacking);
        for (var i = 0; i < st.PieceIds.Count; i++)
            if (s.Pieces.TryGetValue(st.PieceIds[i], out var p))
                yield return (p, new PointD(st.Pos.X + offsets[i].X, st.Pos.Y + offsets[i].Y));
    }
}
