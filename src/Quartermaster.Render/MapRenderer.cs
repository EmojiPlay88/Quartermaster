using Quartermaster.Core;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

/// <param name="ShowGrid">True forces every board's grid overlay on (View > Show Grid); false draws only grids the module marks visible.</param>
/// <param name="HiddenPieceIds">Pieces left out of the drawing (being dragged); their stacks close up around the gap.</param>
/// <param name="Highlight">Selection outline colour; null keeps the default blue.</param>
/// <param name="ModuleGrids">False hides grids the module marks visible (Settings: grid never); <paramref name="ShowGrid"/> still forces them on.</param>
public sealed record MapDrawOptions(bool ShowGrid, IReadOnlySet<string> SelectedPieceIds, string? HoverStackId, IReadOnlySet<string>? HiddenPieceIds = null, SKColor? Highlight = null, bool ModuleGrids = true);

/// <summary>
/// Draws a map into a screen-space canvas: board tiles, grid overlay, movement trails, stacks (insertion order),
/// then selection and hover outlines.
/// </summary>
public sealed class MapRenderer(ImageSource images, PieceRenderer pieces)
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);
    private readonly LruImageCache _tiles = new(256L * 1024 * 1024);

    public void Draw(SKCanvas c, MapDef map, IReadOnlyDictionary<string, TileSet> tiles, GameState s, ModuleModel model,
        Viewport vp, SKSize client, MapDrawOptions opt)
    {
        c.Clear(new SKColor(map.Background.R, map.Background.G, map.Background.B));
        var visible = vp.VisibleMapRect(client);
        foreach (var board in map.Boards)
        {
            tiles.TryGetValue(BoardTiler.TileKey(board), out var ts);
            DrawBoard(c, board, ts, vp, visible);
            if (board.Grid != null && (opt.ShowGrid || (opt.ModuleGrids && board.Grid.Visible)))
            {
                var save = c.Save();
                c.ClipRect(ScreenRect(vp, board.Offset.X, board.Offset.Y, board.Offset.X + board.Width, board.Offset.Y + board.Height));
                DrawGrid(c, board, vp, visible);
                c.RestoreToCount(save);
            }
        }
        DrawTrails(c, map, s, vp);
        DrawStacks(c, map, s, vp, visible, opt);
    }

    private SKRect ScreenRect(Viewport vp, double x0, double y0, double x1, double y1)
    {
        var a = vp.ToScreen(new PointD(x0, y0));
        var b = vp.ToScreen(new PointD(x1, y1));
        return new SKRect((float)Math.Round(a.X), (float)Math.Round(a.Y), (float)Math.Round(b.X), (float)Math.Round(b.Y));
    }

    private void DrawBoard(SKCanvas c, BoardDef board, TileSet? ts, Viewport vp, RectD visible)
    {
        var ox = board.Offset.X;
        var oy = board.Offset.Y;
        if (ts == null || ts.IsPlaceholder)
        {
            using var blank = new SKPaint { Color = new SKColor(0xC0, 0xC0, 0xC0) };
            c.DrawRect(ScreenRect(vp, ox, oy, ox + board.Width, oy + board.Height), blank);
            return;
        }

        var level = ts.ChooseLevel(vp.Zoom);
        var lw = ts.LevelWidth(level);
        var lh = ts.LevelHeight(level);
        var sx = ts.Width / (double)lw;
        var sy = ts.Height / (double)lh;
        var tw = TileSet.TileSize * sx;
        var th = TileSet.TileSize * sy;
        var tx0 = Math.Max(0, (int)Math.Floor((visible.X - ox) / tw));
        var tx1 = Math.Min(ts.TilesX(level) - 1, (int)Math.Floor((visible.X + visible.W - ox) / tw));
        var ty0 = Math.Max(0, (int)Math.Floor((visible.Y - oy) / th));
        var ty1 = Math.Min(ts.TilesY(level) - 1, (int)Math.Floor((visible.Y + visible.H - oy) / th));
        for (var ty = ty0; ty <= ty1; ty++)
            for (var tx = tx0; tx <= tx1; tx++)
            {
                var img = Tile(ts.TilePath(level, tx, ty));
                if (img == null) continue;
                var dest = ScreenRect(vp, ox + tx * tw, oy + ty * th, ox + (tx * TileSet.TileSize + img.Width) * sx, oy + (ty * TileSet.TileSize + img.Height) * sy);
                c.DrawImage(img, dest, Sampling);
            }
    }

    private SKImage? Tile(string path)
    {
        var hit = _tiles.TryGet(path);
        if (hit != null) return hit;
        try
        {
            if (!File.Exists(path)) return null;
            var img = SKImage.FromEncodedData(path);
            if (img != null) _tiles.Put(path, img);
            return img;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void DrawGrid(SKCanvas c, BoardDef board, Viewport vp, RectD visible)
    {
        var grid = board.Grid!;
        var area = new RectD(visible.X - board.Offset.X, visible.Y - board.Offset.Y, visible.W, visible.H);
        var col = grid.Color;
        using var paint = new SKPaint { Color = new SKColor(col.R, col.G, col.B), StrokeWidth = 1, IsAntialias = true, Style = SKPaintStyle.Stroke };
        foreach (var (a, b) in grid.OverlayLines(area))
        {
            var p0 = vp.ToScreen(new PointD(a.X + board.Offset.X, a.Y + board.Offset.Y));
            var p1 = vp.ToScreen(new PointD(b.X + board.Offset.X, b.Y + board.Offset.Y));
            c.DrawLine(p0, p1, paint);
        }
    }

    private static void DrawTrails(SKCanvas c, MapDef map, GameState s, Viewport vp)
    {
        using var line = new SKPaint { Color = new SKColor(0xC0, 0x20, 0x20, 0xC0), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var dot = new SKPaint { Color = new SKColor(0xC0, 0x20, 0x20, 0xC0), IsAntialias = true };
        foreach (var p in s.Pieces.Values)
        {
            if (p.MapId != map.Id || !p.TrailVisible || p.Trail.Count == 0) continue;
            var pts = p.Trail.Append(p.Pos).Select(vp.ToScreen).ToArray();
            for (var i = 1; i < pts.Length; i++) c.DrawLine(pts[i - 1], pts[i], line);
            foreach (var pt in pts) c.DrawCircle(pt, 3, dot);
        }
    }

    private void DrawStacks(SKCanvas c, MapDef map, GameState s, Viewport vp, RectD visible, MapDrawOptions opt)
    {
        var margin = 400.0;
        var outlines = new List<(SKPoint Center, SKRect Bounds, double Rot, bool Selected)>();
        foreach (var st in s.StacksOn(map.Id))
        {
            var span = st.PieceIds.Count * Math.Max(Math.Abs(map.Stacking.ExSepX) + Math.Abs(map.Stacking.ExSepY), 1);
            if (st.Pos.X < visible.X - margin - span || st.Pos.X > visible.X + visible.W + margin + span ||
                st.Pos.Y < visible.Y - margin - span || st.Pos.Y > visible.Y + visible.H + margin + span) continue;

            var placed = opt.HiddenPieceIds is { Count: > 0 } hidden ? PieceGeometry.PlacedExcept(s, st, map, hidden) : PieceGeometry.Placed(s, st, map);
            foreach (var (p, center) in placed)
            {
                var type = pieces.Types.Get(p.TypeString);
                var sel = opt.SelectedPieceIds.Contains(p.Id);
                var sp = vp.ToScreen(center);
                pieces.Draw(c, p, type, new PointD(sp.X, sp.Y), vp.Zoom, sel ? PieceDrawFlags.Selected : PieceDrawFlags.None);
                if (sel || st.Id == opt.HoverStackId)
                {
                    var b = PieceGeometry.LocalBounds(type, p, images);
                    outlines.Add((sp, b, PieceGeometry.Rotation(type, p), sel));
                }
            }
        }
        using var selPaint = new SKPaint { Color = opt.Highlight ?? new SKColor(0x00, 0x78, 0xD7), StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        using var hoverPaint = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80, 0xC0), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        foreach (var (center, bounds, rot, selected) in outlines)
        {
            var save = c.Save();
            c.Translate(center.X, center.Y);
            if (rot != 0) c.RotateDegrees((float)rot);
            c.Scale(vp.Zoom);
            c.DrawRect(bounds, selected ? selPaint : hoverPaint);
            c.RestoreToCount(save);
        }
    }
}
