using Quartermaster.Core;
using System.Drawing;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>What a drag carries. Pieces are identified by id; the grab offset is the map-space vector from the grab point to the primary piece.</summary>
public sealed record DragInfo(ModuleSession Session, string[] Ids, string PrimaryId, PointD GrabOffset);

/// <summary>
/// Piece dragging for one map window, in two flavours that share the formation logic of <see cref="DropResolver.ResolveFormation"/>:
/// <list type="bullet">
/// <item>Captured drag (this window is the source): a snapshot of the scene without the dragged pieces is taken once, the dragged
/// pieces are pre-rendered into sprites once, and each frame is snapshot + sprites at the unsnapped cursor offset + landing outlines.</item>
/// <item>Incoming OLE drag (another window, or this one after the cursor left and came back): no snapshot, same sprites and outlines drawn over the live scene.</item>
/// </list>
/// Zoom/pan/resize while a captured drag is active is not supported: the owner cancels the drag instead (<see cref="IsStale"/>).
/// </summary>
public sealed class MapDragController : IDisposable
{
    public const string Format = "Quartermaster.PieceIds";

    /// <summary>Optional sink for failure details beyond the session log (the WinForms app points it at app.log).</summary>
    public static Action<string>? DiagnosticLog { get; set; }

    /// <summary>The OLE drag in flight in this process (cross-window hand-off), if any.</summary>
    public static DragInfo? Current { get; set; }

    sealed record Item(string Id, PointD Center, PieceRenderer.PieceSprite Sprite);

    static readonly SKSamplingOptions Sampling = new(SKFilterMode.Nearest, SKMipmapMode.None);

    readonly ModuleSession _s;
    readonly MapDef _map;
    readonly PieceRenderer _pieces;
    readonly Action<Action> _guard;

    DragInfo? _info;
    SKImage? _base;
    SKSize _baseSize;
    float _baseZoom;
    SKPoint _basePan;
    float _spriteZoom;
    List<Item> _items = new();
    IReadOnlyList<FormationGroup> _groups = Array.Empty<FormationGroup>();
    PointD _delta;

    public MapDragController(ModuleSession s, MapDef map, PieceRenderer pieces, Action<Action> guard)
    {
        _s = s;
        _map = map;
        _pieces = pieces;
        _guard = guard;
    }

    /// <summary>True during a captured (in-window) drag.</summary>
    public bool Active => _base != null;

    /// <summary>True while a drag from elsewhere (OLE) is hovering over this window.</summary>
    public bool Incoming { get; private set; }

    /// <summary>The carried ids/grab offset of the active or incoming drag.</summary>
    public DragInfo? Info => _info;

    /// <summary>The movable ids among <paramref name="selected"/> (immobile pieces never drag), selection order.</summary>
    public static string[] MovableIds(GameState st, IEnumerable<string> selected) =>
        selected.Where(id => st.Pieces.TryGetValue(id, out var p) && PieceActions.CanMove(p)).ToArray();

    /// <summary>Raised when a failure cancelled a captured drag, so the owner can release the mouse.</summary>
    public event Action? Aborted;

    void Fail(Exception ex, bool wasActive)
    {
        _s.Log.AddOnce("drag:" + ex.GetType().Name, DiagnosticSeverity.Error, "render", "Dragging failed and was cancelled: " + ex.Message, _map.Name);
        DiagnosticLog?.Invoke("drag failed: " + ex);
        End();
        ClearIncoming();
        if (wasActive) Aborted?.Invoke();
    }

    /// <summary>Settings: snap dropped pieces to the grid.</summary>
    public bool Snap { get; set; } = true;

    /// <summary>
    /// Enters captured drag mode: snapshots the scene minus the dragged pieces and pre-renders them.
    /// <paramref name="renderBase"/> draws the scene with the given pieces hidden. Returns false when there is nothing to drag
    /// or something failed (logged); fields are only assigned once everything succeeded.
    /// </summary>
    public bool Begin(GameState st, string grabbed, IEnumerable<string> selected, PointD grabPoint, Viewport vp, SKSize size,
        Func<IReadOnlySet<string>, SKImage?> renderBase)
    {
        End();
        SKImage? snap = null;
        List<Item>? items = null;
        try
        {
            var ids = MovableIds(st, selected);
            if (ids.Length == 0) return false;
            var primary = ids.Contains(grabbed) ? grabbed : ids[0];
            var pp = st.Pieces[primary];
            var info = new DragInfo(_s, ids, primary, new PointD(pp.Pos.X - grabPoint.X, pp.Pos.Y - grabPoint.Y));
            snap = renderBase(new HashSet<string>(ids));
            if (snap == null) return false;
            items = MakeSprites(st, info, vp.Zoom);
            var delta = DropResolver.GhostDelta(st, info.PrimaryId, grabPoint, info.GrabOffset);
            var groups = DropResolver.ResolveFormation(st, _map, grabPoint, info.GrabOffset, info.PrimaryId, info.Ids, _s.Images, Snap);
            // Everything succeeded: only now touch the fields.
            _info = info;
            _base = snap;
            _baseSize = size;
            _baseZoom = vp.Zoom;
            _basePan = vp.Pan;
            _items = items;
            _spriteZoom = vp.Zoom;
            _delta = delta;
            _groups = groups;
            snap = null;
            items = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex, false);
            return false;
        }
        finally
        {
            snap?.Dispose();
            if (items != null) foreach (var it in items) it.Sprite.Dispose();
        }
    }

    /// <summary>Starts or refreshes the preview for a drag arriving from another window (or back into this one).</summary>
    public void UpdateIncoming(DragInfo d, GameState st, PointD cursor, float zoom)
    {
        try
        {
            if (_info != d || !Incoming || _spriteZoom != zoom)
            {
                var items = MakeSprites(st, d, zoom);
                DisposeSprites();
                _items = items;
                _spriteZoom = zoom;
                _info = d;
                Incoming = true;
            }
            UpdateCore(st, cursor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex, false);
        }
    }

    public void ClearIncoming()
    {
        if (!Incoming) return;
        Incoming = false;
        DisposeSprites();
        _info = null;
        _groups = Array.Empty<FormationGroup>();
    }

    /// <summary>True when the view changed since the snapshot (zoom, pan or size), so the snapshot no longer matches the screen.</summary>
    public bool IsStale(Viewport vp, SKSize size) =>
        Active && (vp.Zoom != _baseZoom || vp.Pan != _basePan || size != _baseSize);

    /// <summary>Recomputes the unsnapped ghost offset and the landing groups for a cursor position (map space).</summary>
    public void Update(GameState st, PointD cursor)
    {
        try { UpdateCore(st, cursor); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex, Active); }
    }

    void UpdateCore(GameState st, PointD cursor)
    {
        if (_info is not { } d) return;
        _delta = DropResolver.GhostDelta(st, d.PrimaryId, cursor, d.GrabOffset);
        _groups = DropResolver.ResolveFormation(st, _map, cursor, d.GrabOffset, d.PrimaryId, d.Ids, _s.Images, Snap);
    }

    /// <summary>Drops at the cursor: one MovePieces per group, each through the owner's error guard. Ends the drag.</summary>
    public void Commit(GameState st, PointD cursor)
    {
        var d = _info;
        var game = _s.Game;
        IReadOnlyList<FormationGroup> groups = Array.Empty<FormationGroup>();
        try
        {
            if (d != null && game != null)
                groups = DropResolver.ResolveFormation(st, _map, cursor, d.GrabOffset, d.PrimaryId, d.Ids, _s.Images, Snap);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(ex, false);
            return;
        }
        if (Incoming) ClearIncoming(); else End();
        if (groups.Count == 0) return;
        var moves = groups.Select(g => new MovePieces(g.Ids, _map.Id, g.Pos, g.StackOnto)).ToList();
        // Every group of one drop is one move gesture: the action port executes them (one undoable action) and fires the phase's OnMove
        // formulas. Without a port (no main window), plain logged moves in one scope.
        if (Actions?.Invoke() is { } port) { _guard(() => port.Move(moves)); return; }
        using var scope = game!.BeginAction();
        foreach (var move in moves)
            _guard(() => game.Execute(CommandLog.Prepare(move, game.State, _s)));
    }

    /// <summary>The main window's action port, when there is one; move gestures go through it.</summary>
    public Func<IActionPort?>? Actions { get; set; }

    /// <summary>Leaves captured drag mode and frees the snapshot and sprites.</summary>
    public void End()
    {
        _base?.Dispose();
        _base = null;
        if (!Incoming) _info = null;
        DisposeSprites();
        _groups = Array.Empty<FormationGroup>();
    }

    /// <summary>Draws the snapshot (captured drag only), the cursor-following sprites and the landing outlines.</summary>
    public void Draw(SKCanvas c, Viewport vp, GameState st)
    {
        if (_base != null) c.DrawImage(_base, 0, 0, Sampling);
        if (_info == null) return;

        foreach (var it in _items)
        {
            var sp = vp.ToScreen(new PointD(it.Center.X + _delta.X, it.Center.Y + _delta.Y));
            c.DrawImage(it.Sprite.Image, sp.X - it.Sprite.Half, sp.Y - it.Sprite.Half, Sampling);
        }

        var hl = SystemColors.Highlight;
        using var edge = new SKPaint { Color = new SKColor(hl.R, hl.G, hl.B, 170), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        var byId = _items.ToDictionary(i => i.Id);
        foreach (var g in _groups)
        {
            var below = g.StackOnto != null && st.Stacks.TryGetValue(g.StackOnto, out var onto) ? onto.PieceIds.Count : 0;
            var offsets = StackLayout.Offsets(below + g.Ids.Count, false, _map.Stacking);
            for (var i = 0; i < g.Ids.Count; i++)
            {
                if (!byId.TryGetValue(g.Ids[i], out var it)) continue;
                var o = offsets[Math.Min(below + i, offsets.Count - 1)];
                var sp = vp.ToScreen(new PointD(g.Pos.X + o.X, g.Pos.Y + o.Y));
                var save = c.Save();
                c.Translate(sp.X, sp.Y);
                if (it.Sprite.Rotation != 0) c.RotateDegrees((float)it.Sprite.Rotation);
                c.Scale(vp.Zoom);
                c.DrawRect(it.Sprite.Bounds, edge);
                c.RestoreToCount(save);
            }
        }
    }

    List<Item> MakeSprites(GameState st, DragInfo d, float zoom)
    {
        var items = new List<Item>();
        try
        {
            foreach (var id in d.Ids)
            {
                if (!st.Pieces.TryGetValue(id, out var p)) continue;
                items.Add(new Item(id, CurrentCenter(st, p), _pieces.RenderSprite(p, zoom, PieceDrawFlags.Ghost)));
            }
            return items;
        }
        catch
        {
            foreach (var it in items) it.Sprite.Dispose();
            throw;
        }
    }

    /// <summary>The piece's drawn centre: its stack position plus its slot in the stack layout.</summary>
    PointD CurrentCenter(GameState st, PieceInstance p)
    {
        if (p.StackId == null || !st.Stacks.TryGetValue(p.StackId, out var stack)) return p.Pos;
        var map = _s.Model.Maps.FirstOrDefault(m => m.Id == stack.MapId) ?? _map;
        var idx = stack.PieceIds.IndexOf(p.Id);
        if (idx < 0) return p.Pos;
        var offsets = StackLayout.Offsets(stack.PieceIds.Count, stack.Expanded, map.Stacking);
        return new PointD(stack.Pos.X + offsets[idx].X, stack.Pos.Y + offsets[idx].Y);
    }

    void DisposeSprites()
    {
        foreach (var it in _items) it.Sprite.Dispose();
        _items = new List<Item>();
    }

    public void Dispose()
    {
        ClearIncoming();
        End();
    }
}
