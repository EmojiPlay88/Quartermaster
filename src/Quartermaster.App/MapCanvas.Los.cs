using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Line-of-sight toggle glue and the viewport-changed notification (used by the overview window).</summary>
sealed partial class MapCanvas
{
    LosTool? _los;
    bool _swallowContext;

    /// <summary>Raised after a pan, zoom, resize or recentre, so views of this map (the overview) can follow.</summary>
    public event Action? ViewportChanged;

    /// <summary>Raised when the line-of-sight mode is switched on or off.</summary>
    public event Action? LosChanged;

    /// <summary>Tile sets prepared so far, by <see cref="Quartermaster.Render.BoardTiler.TileKey"/>.</summary>
    internal IReadOnlyDictionary<string, Quartermaster.Render.TileSet> Tiles => _tiles;

    void NotifyViewport() => ViewportChanged?.Invoke();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool LosEnabled
    {
        get => _los != null;
        set
        {
            if (value == LosEnabled) return;
            _los = value ? new LosTool(Map) : null;
            Cursor = value ? Cursors.Cross : Cursors.Default;
            if (!value) DropMessage?.Invoke("");
            Invalidate();
            LosChanged?.Invoke();
        }
    }

    /// <summary>Left button with the tool on starts a line; right button cancels one. Returns true when the press was consumed.</summary>
    bool LosMouseDown(MouseEventArgs e)
    {
        if (_los is not { } los) return false;
        if (e.Button == MouseButtons.Right && (los.HasLine || los.Dragging))
        {
            los.Cancel();
            _swallowContext = true;
            Capture = false;
            DropMessage?.Invoke("");
            Invalidate();
            return true;
        }
        if (e.Button != MouseButtons.Left) return false;
        los.Begin(MapPoint(e.Location));
        Capture = true;
        DropMessage?.Invoke(los.Status() ?? "");
        Invalidate();
        return true;
    }

    bool LosMouseMove(MouseEventArgs e)
    {
        if (_los is not { Dragging: true } los) return false;
        los.Update(MapPoint(e.Location));
        DropMessage?.Invoke(los.Status() ?? "");
        Request();
        return true;
    }

    bool LosMouseUp(MouseEventArgs e)
    {
        if (_swallowContext && e.Button == MouseButtons.Right) { _swallowContext = false; return true; }
        if (_los is not { Dragging: true } los || e.Button != MouseButtons.Left) return false;
        los.End();
        Capture = false;
        return true;
    }

    bool LosEscape()
    {
        if (_los is not { } los || !(los.HasLine || los.Dragging)) return false;
        los.Cancel();
        Capture = false;
        DropMessage?.Invoke("");
        Invalidate();
        return true;
    }

    /// <summary>Smoke access: draws a line between two map points and returns its status text.</summary>
    internal string? SmokeLine(PointD a, PointD b)
    {
        LosEnabled = true;
        _los!.Begin(a);
        _los.Update(b);
        _los.End();
        Invalidate();
        return _los.Status();
    }
}
