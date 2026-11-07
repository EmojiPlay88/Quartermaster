using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// Per-piece visibility decisions of <see cref="PlayerView"/> for one viewer, answered from the full state without building the view.
/// Side filters are compiled once per instance (build one per state version); map and piece answers are cached. A piece is in the view
/// unless it is on a map owned by a side not held, or belongs to a side not held and has an active Hideable (a masked piece stays, as a
/// placeholder). Never throws: on any failure it answers "not visible" (fail closed, as PlayerView does).
/// </summary>
public sealed class PieceAccess
{
    private readonly GameState _full;
    private readonly RulesPack? _pack;
    private readonly ModuleModel? _model;
    private readonly IReadOnlySet<string> _held;
    private readonly bool _everything;
    private Func<PieceInstance, string?>? _sides;
    private readonly Dictionary<string, bool> _mapHidden = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _visible = new(StringComparer.Ordinal);

    public PieceAccess(GameState full, RulesPack? pack, ModuleModel? model, IReadOnlySet<string> held)
    {
        _full = full;
        _pack = pack;
        _model = model;
        _held = held ?? new HashSet<string>();
        // Same shortcut as PlayerView.For: no pack, or every side held, shows everything.
        _everything = pack == null || (pack.Sides ?? new List<SideDef>()).All(x => x == null || _held.Contains(x.Id));
    }

    /// <summary>The piece's side (first matching unit filter); null when none matches or there is no pack.</summary>
    public string? SideOf(PieceInstance p)
    {
        if (_pack == null || p == null) return null;
        _sides ??= Ownership.PieceSides(_full, _pack);
        return _sides(p);
    }

    /// <summary>True for a side that exists and the viewer does not hold.</summary>
    public bool NotHeld(string? side) => side != null && !_held.Contains(side);

    /// <summary>True when the map (by id, or by the name of a module map) is owned by a side not held.</summary>
    public bool MapHidden(string? mapId)
    {
        if (mapId == null || _pack == null) return false;
        if (_mapHidden.TryGetValue(mapId, out var known)) return known;
        var side = Ownership.SideOfMap(mapId, _pack);
        if (side == null && _model != null)
            foreach (var m in _model.Maps)
                if (m.Name == mapId && m.Id != mapId && Ownership.SideOfMap(m.Id, _pack) is { } s) { side = s; break; }
        return _mapHidden[mapId] = NotHeld(side);
    }

    /// <summary>The piece's active Hideable (hide) and first active Obscurable (mask), from its own trait states.</summary>
    public static (bool Hidden, ObscurableDef? Mask) Masking(PieceInstance p)
    {
        var type = PieceInfo.TypeOf(p);
        ObscurableDef? mask = null;
        var hide = false;
        for (var i = 0; i < type.Traits.Count; i++)
        {
            var st = i < p.TraitStates.Count ? p.TraitStates[i] : null;
            if (type.Traits[i] is HideableDef h && TraitState.IsHidden(h, st)) hide = true;
            if (type.Traits[i] is ObscurableDef o && mask == null && TraitState.IsObscured(o, st)) mask = o;
        }
        return (hide, mask);
    }

    /// <summary>True when the piece is in the viewer's <see cref="PlayerView"/> (possibly as a masked placeholder).</summary>
    public bool CanSee(string? pieceId)
    {
        if (pieceId == null || !_full.Pieces.TryGetValue(pieceId, out var p) || p == null) return false;
        if (_everything) return true;
        if (_visible.TryGetValue(pieceId, out var known)) return known;
        bool visible;
        try
        {
            if (PlayerView.FailForTest) throw new InvalidOperationException("test fault");
            visible = !MapHidden(p.MapId) && !(NotHeld(SideOf(p)) && Masking(p).Hidden);
        }
        catch (Exception)
        {
            visible = false;   // fail closed
        }
        return _visible[pieceId] = visible;
    }

    /// <summary>True when the piece is visible and belongs to a held side or to no side.</summary>
    public bool CanAct(string? pieceId)
    {
        if (!CanSee(pieceId)) return false;
        if (_everything) return true;
        try { return !NotHeld(SideOf(_full.Pieces[pieceId!])); }
        catch (Exception) { return false; }
    }
}
