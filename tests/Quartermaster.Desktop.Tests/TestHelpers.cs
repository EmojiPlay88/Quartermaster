using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

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
}
