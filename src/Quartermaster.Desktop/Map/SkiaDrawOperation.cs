using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Quartermaster.Desktop.Map;

/// <summary>
/// Plays a recorded <see cref="SKPicture"/> on Avalonia's own Skia canvas. The picture is recorded on the UI thread (so the game state is only
/// read there) and replayed on the render thread; Avalonia's canvas already carries the control offset and the render scaling, so the
/// replay rasterises at device resolution. Owns the picture and frees it when Avalonia is done with the operation.
/// </summary>
sealed class SkiaDrawOperation(SKPicture picture, Rect bounds) : ICustomDrawOperation
{
    /// <summary>Last replay failure (null after a successful one); for diagnostics and tests.</summary>
    public static Exception? LastError { get; private set; }

    public Rect Bounds => bounds;
    public bool HitTest(Point p) => bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() => picture.Dispose();

    public void Render(ImmediateDrawingContext context)
    {
        try
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>() ?? throw new InvalidOperationException("Avalonia is not drawing with Skia");
            using var api = lease.Lease();
            var c = api.SkCanvas;
            var save = c.Save();
            c.ClipRect(new SKRect((float)bounds.X, (float)bounds.Y, (float)bounds.Right, (float)bounds.Bottom)); // the renderer clears its whole canvas
            c.DrawPicture(picture);
            c.RestoreToCount(save);
            LastError = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastError = ex;
        }
    }
}
