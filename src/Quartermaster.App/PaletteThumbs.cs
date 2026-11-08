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
    readonly CounterImages _counters;

    public PaletteThumbs(ModuleSession s)
    {
        _s = s;
        _counters = s.Counters;
    }

    /// <summary>A bitmap for the slot, or null when it cannot be drawn. The caller owns it.</summary>
    public Bitmap? Render(PieceSlotDef slot)
    {
        try
        {
            var (type, state) = PieceFactory.FromSlot(_s.Model, slot, "", new PointD(0, 0));
            var count = PieceTypeParser.Parse(type, new DiagnosticLog(), null).Traits.Count;
            var piece = new PieceInstance { Id = "thumb", TypeString = type, TraitStates = PieceTypeParser.SplitState(state, count).ToList() };
            var img = _counters.Render(piece, new SKSizeI(Size.Width, Size.Height));
            return img?.ToBitmap();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
