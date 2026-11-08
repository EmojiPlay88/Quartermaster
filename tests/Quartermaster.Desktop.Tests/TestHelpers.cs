using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

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
