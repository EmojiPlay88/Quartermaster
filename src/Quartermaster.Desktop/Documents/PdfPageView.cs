using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Documents;

/// <summary>
/// One PDF page on screen: a white sheet with the rendered page picture stretched over it (the picture may be at an older zoom while a
/// sharper one renders) and translucent boxes over search hits. The owner (<see cref="DocumentViewer"/>) owns the bitmaps.
/// </summary>
public sealed class PdfPageView : Control
{
    static readonly IBrush Sheet = Brushes.White;
    static readonly IPen Edge = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 1);

    readonly IBrush _highlight;

    public PdfPageView(Color highlight)
    {
        _highlight = new ImmutableSolidColorBrush(highlight);
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    /// <summary>The 1-based page this view stands for (0 = none).</summary>
    public int ShownPage { get; private set; }

    /// <summary>The picture drawn (null = a blank sheet while the page renders).</summary>
    public Bitmap? Bitmap { get; private set; }

    /// <summary>Search-hit boxes as fractions of the page (origin top-left).</summary>
    public IReadOnlyList<RectD> Highlights { get; private set; } = Array.Empty<RectD>();

    public void Show(int page, Bitmap? bitmap)
    {
        ShownPage = page;
        Bitmap = bitmap;
        InvalidateVisual();
    }

    public void SetHighlights(IReadOnlyList<RectD> boxes)
    {
        Highlights = boxes;
        InvalidateVisual();
    }

    /// <summary>A hit box in this control's coordinates.</summary>
    public Rect BoxRect(RectD box) =>
        new(box.X * Bounds.Width, box.Y * Bounds.Height, box.W * Bounds.Width, box.H * Bounds.Height);

    public override void Render(DrawingContext context)
    {
        var area = new Rect(Bounds.Size);
        if (area.Width < 1 || area.Height < 1) return;
        context.FillRectangle(Sheet, area);
        if (Bitmap is { } b)
        {
            var src = new Rect(0, 0, b.PixelSize.Width, b.PixelSize.Height);
            context.DrawImage(b, src, area);
        }
        foreach (var box in Highlights) context.FillRectangle(_highlight, BoxRect(box));
        context.DrawRectangle(null, Edge, area.Deflate(0.5));
    }
}
