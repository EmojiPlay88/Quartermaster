using Quartermaster.Core;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

[Flags]
public enum PieceDrawFlags { None = 0, Selected = 1, Ghost = 2 }

/// <summary>
/// Draws one piece centred on a canvas point. Order: layers set to draw underneath (selected pieces only, as in VASSAL),
/// the basic image, layers and labels inner to outer, then the moved marker. Rotation turns the whole piece.
/// </summary>
public sealed class PieceRenderer(ImageSource images)
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private readonly Dictionary<string, SKTypeface> _faces = new();

    public PieceTypeCache Types { get; } = PieceTypeCache.For(images);

    public void Draw(SKCanvas c, PieceInstance p, PieceType x, PointD center, float zoom, PieceDrawFlags flags)
    {
        var traits = x.Traits;
        var alpha = 1.0;
        var obscured = false;
        for (var i = 0; i < traits.Count; i++)
        {
            if (traits[i] is HideableDef h && TraitState.IsHidden(h, PieceGeometry.StateAt(p, i))) alpha = Math.Min(alpha, h.Transparency);
            if (traits[i] is ObscurableDef o && TraitState.IsObscured(o, PieceGeometry.StateAt(p, i))) obscured = true;
        }
        if (flags.HasFlag(PieceDrawFlags.Ghost)) alpha *= 0.5;

        var saved = c.Save();
        try
        {
            c.Translate((float)center.X, (float)center.Y);
            var rot = PieceGeometry.Rotation(x, p);
            if (rot != 0) c.RotateDegrees((float)rot);
            c.Scale(zoom);
            if (alpha < 1)
            {
                using var layer = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255)) };
                c.SaveLayer(layer);
            }
            if (obscured) DrawMask(c, p, x, zoom);
            else DrawBody(c, p, x, zoom, flags);
        }
        finally
        {
            c.RestoreToCount(saved);
        }
    }

    private void DrawBody(SKCanvas c, PieceInstance p, PieceType x, float zoom, PieceDrawFlags flags)
    {
        var traits = x.Traits;
        var selected = flags.HasFlag(PieceDrawFlags.Selected);
        for (var i = traits.Count - 1; i >= 0; i--)
            if (traits[i] is LayerDef { DrawUnderneath: true } l && selected) DrawLayer(c, p, l, i, zoom);

        if (x.Basic.Image.Length > 0) DrawImage(c, x.Basic.Image, 0, 0, zoom);

        for (var i = traits.Count - 1; i >= 0; i--)
        {
            switch (traits[i])
            {
                case LayerDef l when !(l.DrawUnderneath && selected): DrawLayer(c, p, l, i, zoom); break;
                case LabelDef lb: DrawLabel(c, p, x, lb, i); break;
            }
        }

        if (!p.Moved) return;
        foreach (var t in traits)
            if (t is MovementMarkableDef m && m.Image.Length > 0)
            {
                var size = images.SizeOf(m.Image);
                c.DrawImage(images.Get(m.Image, zoom), SKRect.Create(m.XOffset, m.YOffset, size.Width, size.Height), Sampling);
            }
    }

    private void DrawLayer(SKCanvas c, PieceInstance p, LayerDef l, int index, float zoom)
    {
        var (level, active) = TraitState.LayerLevel(l, PieceGeometry.StateAt(p, index));
        if (!active || level < 1 || level > l.Images.Count || l.Images[level - 1].Length == 0) return;
        DrawImage(c, l.Images[level - 1], l.OffsetX, l.OffsetY, zoom);
    }

    private void DrawImage(SKCanvas c, string name, float ox, float oy, float zoom)
    {
        var rect = PieceGeometry.Centered(images.SizeOf(name), ox, oy);
        c.DrawImage(images.Get(name, zoom), rect, Sampling);
    }

    private void DrawMask(SKCanvas c, PieceInstance p, PieceType x, float zoom)
    {
        ObscurableDef? def = x.Get<ObscurableDef>();
        if (def != null && def.MaskImage.Length > 0 && def.MaskImage != "?")
        {
            DrawImage(c, def.MaskImage, 0, 0, zoom);
            return;
        }
        var b = PieceGeometry.LocalBounds(x, p, images);
        if (b.Width < 1) b = SKRect.Create(-16, -16, 32, 32);
        using var fill = new SKPaint { Color = new SKColor(0x80, 0x80, 0x80), IsAntialias = true };
        c.DrawRect(b, fill);
        using var font = new SKFont(SKTypeface.Default, Math.Max(8, b.Height * 0.6f));
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
        c.DrawText("?", b.MidX, b.MidY + font.Size * 0.35f, SKTextAlign.Center, font, ink);
    }

    /// <summary>Label placement ported from Labeler.getLabelPosition; text comes from the trait state.</summary>
    private void DrawLabel(SKCanvas c, PieceInstance p, PieceType x, LabelDef lb, int index)
    {
        var text = TraitState.LabelText(lb, PieceGeometry.StateAt(p, index));
        if (text.Length == 0) return;
        using var font = new SKFont(Face(lb), Math.Max(1, lb.FontSize)) { Subpixel = true };
        var m = font.Metrics;
        var w = font.MeasureText(text);
        var h = m.Descent - m.Ascent;
        var sel = PieceGeometry.LocalBounds(x, p, images);

        float px = lb.HorizontalOffset, py = lb.VerticalOffset;
        if (lb.VerticalPos == 't') py += sel.Top; else if (lb.VerticalPos == 'b') py += sel.Bottom;
        if (lb.HorizontalPos == 'l') px += sel.Left; else if (lb.HorizontalPos == 'r') px += sel.Right;
        if (lb.VerticalJustification == 'b') py -= h; else if (lb.VerticalJustification == 'c') py -= h / 2;
        if (lb.HorizontalJustification == 'c') px -= w / 2; else if (lb.HorizontalJustification == 'r') px -= w;

        if (lb.Background is { } bg)
        {
            using var back = new SKPaint { Color = new SKColor(bg.R, bg.G, bg.B) };
            c.DrawRect(SKRect.Create(px, py, w, h), back);
        }
        var fg = lb.Foreground ?? new RgbColor(0, 0, 0);
        using var ink = new SKPaint { Color = new SKColor(fg.R, fg.G, fg.B), IsAntialias = true };
        c.DrawText(text, px, py - m.Ascent, SKTextAlign.Left, font, ink);
    }

    private SKTypeface Face(LabelDef lb)
    {
        var key = lb.FontFamily + "|" + lb.FontStyle;
        if (_faces.TryGetValue(key, out var f)) return f;
        var style = new SKFontStyle((lb.FontStyle & 1) != 0 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            (lb.FontStyle & 2) != 0 ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        return _faces[key] = SKTypeface.FromFamilyName(lb.FontFamily, style) ?? SKTypeface.Default;
    }
}
