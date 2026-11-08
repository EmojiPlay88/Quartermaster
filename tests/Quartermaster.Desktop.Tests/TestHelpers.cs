using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>Tests that set the process-wide ShortcutGestures.IsMac (or read it through input handling) run here, one at a time, never
/// alongside other tests.</summary>
[CollectionDefinition("MacFlag", DisableParallelization = true)]
public sealed class MacFlagCollection;

public static class TestHelpers
{
    /// <summary>Renders the (headless) window and saves the frame as a PNG. No real window is ever shown.</summary>
    public static void CaptureToPng(Window window, string path)
    {
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        frame.Dispose();
    }

    /// <summary>
    /// A bitmap for the reference render, in the colour type Avalonia.Skia draws into: the platform's native 8888 type
    /// (<see cref="SKImageInfo.PlatformColorType"/>: Bgra8888 on Windows and Linux, Rgba8888 on macOS), premultiplied. Skia's output for
    /// filtered (scaled) image draws depends on the destination colour type, so the reference must match the surface, not the captured
    /// frame (which headless Avalonia always hands back as Rgba8888). SkiaInteropTests checks Avalonia's surface really is this type.
    /// </summary>
    public static SKBitmap ReferenceBitmap(int width, int height) =>
        new(new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul));

    /// <summary>
    /// Asserts every pixel of <paramref name="actual"/> is within <paramref name="tolerance"/> (per channel) of <paramref name="expected"/>.
    /// On failure the message says how many pixels differ, by how much at most, where, and a few samples.
    /// </summary>
    public static void AssertSamePixels((int Width, int Height, SKColor[] Px) actual, SKBitmap expected, int tolerance)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        int diff = 0, maxDelta = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var samples = new List<string>();
        for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++)
            {
                var a = actual.Px[y * actual.Width + x];
                var e = expected.GetPixel(x, y);
                var d = Math.Max(Math.Max(Math.Abs(a.Red - e.Red), Math.Abs(a.Green - e.Green)), Math.Max(Math.Abs(a.Blue - e.Blue), Math.Abs(a.Alpha - e.Alpha)));
                if (d <= tolerance) continue;
                diff++;
                maxDelta = Math.Max(maxDelta, d);
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                if (samples.Count < 5) samples.Add($"({x},{y}) {a} vs {e}");
            }
        Assert.True(diff == 0, $"{diff} of {actual.Width * actual.Height} pixels differ by more than {tolerance} (max {maxDelta}) " +
                               $"in [{minX},{minY}]-[{maxX},{maxY}]; actual vs expected: {string.Join("; ", samples)}");
    }

    /// <summary>The captured frame as SKColors (headless frames are Rgba8888).</summary>
    public static (int Width, int Height, SKColor[] Px) Pixels(Bitmap frame)
    {
        Assert.Equal(PixelFormat.Rgba8888, frame.Format);
        var w = frame.PixelSize.Width; var h = frame.PixelSize.Height;
        var bytes = new byte[w * h * 4];
        unsafe
        {
            fixed (byte* p = bytes)
                frame.CopyPixels(new PixelRect(0, 0, w, h), (nint)p, bytes.Length, w * 4);
        }
        var px = new SKColor[w * h];
        for (var i = 0; i < px.Length; i++) px[i] = new SKColor(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], bytes[i * 4 + 3]);
        return (w, h, px);
    }
}
