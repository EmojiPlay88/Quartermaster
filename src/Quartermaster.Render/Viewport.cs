using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>Map-to-screen transform. <see cref="Pan"/> is the map coordinate at the screen's top-left.</summary>
public sealed class Viewport
{
    public const float MinZoom = 0.25f, MaxZoom = 4f;
    private float _zoom = 1f;

    public float Zoom
    {
        get => _zoom;
        set => _zoom = float.IsFinite(value) ? Math.Clamp(value, MinZoom, MaxZoom) : _zoom;
    }

    public SKPoint Pan { get; set; }

    public SKPoint ToScreen(PointD map) => new((float)((map.X - Pan.X) * _zoom), (float)((map.Y - Pan.Y) * _zoom));

    public PointD ToMap(SKPoint screen) => new(screen.X / (double)_zoom + Pan.X, screen.Y / (double)_zoom + Pan.Y);

    /// <summary>Multiplies the zoom (clamped) so that the map point under <paramref name="screenAnchor"/> stays put.</summary>
    public void ZoomAt(SKPoint screenAnchor, float factor)
    {
        var m = ToMap(screenAnchor);
        Zoom = _zoom * factor;
        Pan = new SKPoint((float)(m.X - screenAnchor.X / (double)_zoom), (float)(m.Y - screenAnchor.Y / (double)_zoom));
    }

    public RectD VisibleMapRect(SKSize client) => new(Pan.X, Pan.Y, client.Width / (double)_zoom, client.Height / (double)_zoom);
}
