using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>Map view tools: last-moved highlight, hide counters, centre on selection, zoom presets.</summary>
sealed partial class MapCanvas
{
    bool _hideCounters;
    bool _highlightLast;
    IReadOnlySet<string> _lastMoved = new HashSet<string>();

    public event Action<bool>? HideCountersChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool HideCounters
    {
        get => _hideCounters;
        set
        {
            if (_hideCounters == value) return;
            _hideCounters = value;
            if (value)
            {
                if (_drag.Active) CancelDrag(); else _drag.ClearIncoming();
                _selected.Clear();
                _hoverStack = null;
                HidePopup();
                DropMessage?.Invoke("Counters hidden — press H to show");
            }
            else DropMessage?.Invoke("Counters shown");
            HideCountersChanged?.Invoke(value);
            Invalidate();
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool HighlightLastMoved
    {
        get => _highlightLast;
        set
        {
            _highlightLast = value;
            RefreshLastMoved();
        }
    }

    /// <summary>Recomputes the last-moved set from the command list (call after the game changed).</summary>
    public void RefreshLastMoved()
    {
        _lastMoved = _highlightLast && _s.Game is { } g ? MapViewTools.LastMovedIds(g.Log, Map.Id) : new HashSet<string>();
        Invalidate();
    }

    MapDrawOptions Options(IReadOnlySet<string>? hidden, string? hover) =>
        new(ShowGrid, _selected, hover, hidden, _highlight, _moduleGrids, _highlightLast ? _lastMoved : null, _hideCounters, _highlight);

    public void CentreOnSelection()
    {
        if (State is { } st && MapViewTools.SelectionCentre(st, Map.Id, _selected) is { } c) CentreOn(c);
        else DropMessage?.Invoke("Select a counter first");
    }

    /// <summary>Sets the zoom to a fixed factor around the middle of the view.</summary>
    public void ZoomPreset(float zoom) => ZoomCentered(zoom);
}
