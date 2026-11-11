using Avalonia.Controls;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

public sealed class MapPage : SettingsPage
{
    internal readonly NumericUpDown Zoom = new() { Minimum = 1.05m, Maximum = 2m, Increment = 0.05m, FormatString = "0.00", Width = 110, Value = 1.2m };
    internal readonly CheckBox Snap = Check("Snap pieces to the grid when dropped");
    internal readonly NumericUpDown Drag = Num(0, 50), Hover = Num(0, 5000);

    public MapPage()
    {
        Hover.Increment = 50;
        var t = Table();
        Row(t, "Mouse wheel zoom step per notch (x):", Zoom);
        Full(t, Snap);
        Row(t, "Drag starts after (pixels):", Drag);
        Row(t, "Stack popup delay (ms):", Hover);
        Content = Host(t);
    }

    public override string Title => "Map";

    public override void Load(Settings s)
    {
        Set(Zoom, (decimal)s.WheelZoomStep);
        Snap.IsChecked = s.SnapOnDrop;
        Set(Drag, s.DragThresholdPx);
        Set(Hover, s.HoverDelayMs);
    }

    public override Settings Collect(Settings s) => s with
    {
        WheelZoomStep = (double)(Zoom.Value ?? Zoom.Minimum), SnapOnDrop = Snap.IsChecked == true, DragThresholdPx = Int(Drag), HoverDelayMs = Int(Hover),
    };
}
