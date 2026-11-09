using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Map;

/// <summary>
/// Hover and stack expand, copied from the WinForms MapCanvas: resting on a stack highlights it and, after the hover delay, opens its popup
/// (<see cref="CounterPopup"/>, never under the pointer) in the window's overlay layer. The popup closes on a press, wheel, drag, game or view
/// change, when the pointer leaves the map or reaches another stack, and stays while the pointer crosses it to a ledger line (a short grace
/// lets the pointer travel from the counter to the popup). Double-clicking a stack expands or collapses it.
/// </summary>
public sealed partial class MapView
{
    /// <summary>Lets the pointer travel from a counter to a hoverable popup (WinForms 450 ms).</summary>
    static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(450);

    DispatcherTimer? _hoverTimer;
    DispatcherTimer? _graceTimer;
    CounterPopup? _popup;
    /// <summary>Where the open popup is, in window (top level) coordinates (known before the overlay has laid it out).</summary>
    Rect _popupRect;
    string? _hoverStack;
    /// <summary>The last pointer position over the window, in window (top level) coordinates; null when unknown.</summary>
    Point? _pointerInWindow;

    /// <summary>Delay before the hover popup opens (WinForms Settings HoverDelayMs, default 600; at least 1).</summary>
    public int HoverDelayMs { get; set => field = Math.Max(1, value); } = 600;

    /// <summary>The stack under the resting pointer (highlighted); null for none.</summary>
    internal string? HoverStackId => _hoverStack;

    /// <summary>The open hover popup (tests).</summary>
    internal CounterPopup? Popup => _popup;

    /// <summary>The shared ledger breakdown tip (tests).</summary>
    internal BreakdownTip Tip { get; } = new();

    void UpdateHover(Point p)
    {
        string? stackId = null;
        if (PieceAt(p) is { } id && State is { } st && st.Pieces.TryGetValue(id, out var pc)) stackId = pc.StackId;
        if (stackId == _hoverStack) return;
        // The popup is click-through away from its ledger lines, so the map sees the pointer crossing it on the way to a line: keep it up.
        if (_popup is { Interactive: true } && PointerOnPopup()) return;
        _hoverStack = stackId;
        _hoverTimer?.Stop();
        if (stackId == null && _popup is { Interactive: true }) StartGrace(); // keep it up briefly so the pointer can reach it
        else HidePopup();
        if (stackId != null) StartHoverTimer();
        InvalidateVisual();
    }

    void StartHoverTimer()
    {
        _hoverTimer ??= NewTimer(ShowPopup);
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(HoverDelayMs);
        _hoverTimer.Start();
    }

    void StartGrace()
    {
        _graceTimer ??= NewTimer(HidePopup);
        _graceTimer.Interval = Grace;
        _graceTimer.Start();
    }

    static DispatcherTimer NewTimer(Action tick)
    {
        var t = new DispatcherTimer();
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    void ShowPopup()
    {
        _hoverTimer?.Stop();
        if (_hoverStack == null || _panning || _bandActive || _drag.Active || State is not { } st || !st.Stacks.TryGetValue(_hoverStack, out var stack)) return;
        HidePopup();
        if (_topLevel == null || OverlayLayer.GetOverlayLayer(this) is not { } layer || _pointerInWindow is not { } pw
            || _topLevel.TranslatePoint(pw, layer) is not { } cursor) return;
        CounterPopup popup;
        try
        {
            popup = new CounterPopup(CounterPopupLayout.Build(Session, st, stack, _pieces), Tip, PlaybackFailed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Session.Log.AddOnce("popup:" + ex.GetType().Name, DiagnosticSeverity.Error, "render", "The counter popup failed: " + ex.Message, Map.Name);
            return;
        }
        popup.PointerEnteredPopup += () => _graceTimer?.Stop();
        popup.PointerLeftPopup += () => Dispatcher.UIThread.Post(() =>
        {
            // Leaving a ledger line for the popup's click-through area hands the pointer back to the map's hover logic.
            if (!ReferenceEquals(_popup, popup) || PointerOnPopup()) return;
            _hoverStack = null;
            HidePopup();
            InvalidateVisual();
        });
        // Never under the cursor: a click or drag right after the hover must land on the map, not on the popup. The window is the area
        // (the overlay cannot leave it).
        var window = BreakdownTip.WindowArea(layer);
        var at = PopupPlacement.Place(new System.Drawing.Point((int)Math.Round(cursor.X), (int)Math.Round(cursor.Y)),
            new System.Drawing.Size(popup.Layout.Width, popup.Layout.Height),
            new System.Drawing.Rectangle((int)Math.Ceiling(window.X), (int)Math.Ceiling(window.Y), (int)Math.Floor(window.Width), (int)Math.Floor(window.Height)));
        Canvas.SetLeft(popup, at.X);
        Canvas.SetTop(popup, at.Y);
        layer.Children.Add(popup);
        _popup = popup;
        var layerOrigin = layer.TranslatePoint(default, _topLevel) ?? default;
        _popupRect = new Rect(layerOrigin + new Point(at.X, at.Y), new Size(popup.Layout.Width, popup.Layout.Height));
    }

    /// <summary>The open popup's bounds in window coordinates; null when there is none.</summary>
    Rect? PopupBoundsInWindow() => _popup == null ? null : _popupRect;

    bool PointerOnPopup() => _pointerInWindow is { } p && PopupBoundsInWindow() is { } r && r.Contains(p);

    /// <summary>Window-wide pointer watch (handled events too): remembers where the pointer is, and closes the popup once the pointer is on
    /// neither the popup nor the map (it can leave the click-through popup onto a docked panel without the map seeing it; WinForms polls).</summary>
    void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_topLevel == null) return;
        var p = e.GetPosition(_topLevel);
        _pointerInWindow = p;
        if (_popup == null || PopupBoundsInWindow() is not { } bounds) return;
        var overMap = _topLevel.TranslatePoint(p, this) is { } local && new Rect(Bounds.Size).Contains(local);
        if (!PopupWatch.ShouldHide(ToDrawing(bounds), ToDrawing(p), overMap)) return;
        _hoverStack = null;
        HidePopup();
        InvalidateVisual();
    }

    static System.Drawing.Rectangle ToDrawing(Rect r) =>
        System.Drawing.Rectangle.FromLTRB((int)Math.Floor(r.X), (int)Math.Floor(r.Y), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));

    static System.Drawing.Point ToDrawing(Point p) => new((int)Math.Floor(p.X), (int)Math.Floor(p.Y));

    void HookPointerWatch(TopLevel? top, bool on)
    {
        if (top == null) return;
        if (on) top.AddHandler(PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        else top.RemoveHandler(PointerMovedEvent, OnWindowPointerMoved);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_topLevel != null) _pointerInWindow = e.GetPosition(_topLevel);
        if (_popup != null && PointerOnPopup()) return;
        HidePopup();
        if (_hoverStack != null) { _hoverStack = null; InvalidateVisual(); }
    }

    /// <summary>Closes the hover popup and its breakdown tip and stops the hover timers (the hovered stack stays highlighted).</summary>
    public void HidePopup()
    {
        _hoverTimer?.Stop();
        _graceTimer?.Stop();
        Tip.Hide();
        if (_popup is { } p)
        {
            _popup = null;
            (p.GetVisualParent() as Panel)?.Children.Remove(p);
        }
    }

    /// <summary>Double-click on a counter: expands or collapses its stack (WinForms MapCanvas.OnMouseDoubleClick).</summary>
    void ToggleStack(Point at)
    {
        if (State is not { } st || Session.IsReplaying) return;   // a replayed turn stays exactly the sender's
        var hit = PieceAt(at);
        if (hit == null || !st.Pieces.TryGetValue(hit, out var p) || p.StackId == null || !st.Stacks.TryGetValue(p.StackId, out var stack)) return;
        var game = Session.Game;
        var toggle = new SetStackExpanded(stack.Id, !stack.Expanded);
        // An online guest's stack expansion is its own view: the remote port keeps it across the host's snapshots.
        if (Actions?.Invoke() is { IsRemote: true } port) Guard(() => port.PieceCommand(toggle));
        else Guard(() => game?.Execute(toggle));
    }
}
