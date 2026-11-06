using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Quartermaster.App;

/// <summary>Renders palette thumbnails (fit into <see cref="Size"/>) with the map piece renderer. Safe to call from a worker thread.</summary>
sealed class PaletteThumbs
{
    public static readonly Size Size = new(64, 48);
    readonly ModuleSession _s;
    readonly PieceRenderer _pieces;

    public PaletteThumbs(ModuleSession s)
    {
        _s = s;
        _pieces = new PieceRenderer(s.Images);
    }

    /// <summary>A bitmap for the slot, or null when it cannot be drawn. The caller owns it.</summary>
    public Bitmap? Render(PieceSlotDef slot)
    {
        try
        {
            lock (_pieces)
            {
                var (type, state) = PieceFactory.FromSlot(_s.Model, slot, "", new PointD(0, 0));
                var count = PieceTypeParser.Parse(type, new DiagnosticLog(), null).Traits.Count;
                var piece = new PieceInstance { Id = "thumb", TypeString = type, TraitStates = PieceTypeParser.SplitState(state, count).ToList() };
                using var sprite = _pieces.RenderSprite(piece, 1f, PieceDrawFlags.None);
                var b = sprite.Bounds;
                if (b.Width < 1 || b.Height < 1) b = SKRect.Create(-16, -16, 32, 32);
                var scale = (float)Math.Min(1.5, Math.Min((Size.Width - 4) / (double)b.Width, (Size.Height - 4) / (double)b.Height));
                var src = SKRect.Create(sprite.Half + b.Left, sprite.Half + b.Top, b.Width, b.Height);
                var dst = SKRect.Create((Size.Width - b.Width * scale) / 2f, (Size.Height - b.Height * scale) / 2f, b.Width * scale, b.Height * scale);
                using var surface = SKSurface.Create(new SKImageInfo(Size.Width, Size.Height));
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawImage(sprite.Image, src, dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                using var img = surface.Snapshot();
                return img.ToBitmap();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
