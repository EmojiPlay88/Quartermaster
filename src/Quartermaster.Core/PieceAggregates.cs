using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// Piece sums: sum, countof, maxof and minof over every piece, and hexsum, hexcount, hexmax and hexmin over the pieces in the hex of the piece
/// being evaluated, and attachedsum, attachedcount, attachedmax and attachedmin over the pieces on its organisation chart. Shared by every evaluation context (<see cref="IEvalContext.Aggregate"/>).
/// </summary>
public static class PieceAggregates
{
    /// <summary>How many filter texts the <see cref="AggregateFilters"/> caches compiled on this thread (tests read it).</summary>
    [ThreadStatic] internal static int FilterCompiles;

    /// <summary>Error text of an aggregate a player view cannot answer (after the function name and "() ").</summary>
    public const string HiddenText = "reads data this player cannot see";

    /// <summary>
    /// The value of an aggregate call over the state of <paramref name="ctx"/>. <paramref name="self"/> is the piece being evaluated (null where there
    /// is none: the hex ones then throw "{fn}() is not available here", as they do without a module model). "" matches every piece; an error the
    /// filter raises on a piece fails the whole aggregate (no piece is skipped silently). A piece's value is its ledger value of the property,
    /// else the property's Default (as a Unit binding reads it); a holder-only property is an error. In a player view, a call that may read
    /// data the viewer cannot see (<see cref="FormulaVariables.AggregateCallHidden"/>) is the error "{fn}() reads data this player cannot see":
    /// the view lacks pieces or values, so any answer would be wrong.
    /// </summary>
    internal static Value Evaluate(string fn, string? property, string? filter, PieceInstance? self, AggregateFilters ctx)
    {
        var (s, pack, model) = (ctx.State, ctx.Pack, ctx.Model);
        if (s.IsPlayerView && ctx.HiddenInView(fn, property, filter)) throw new EvalError($"{fn}() {HiddenText}") { HiddenData = true };
        IEnumerable<PieceInstance> pieces;
        if (ExprFacts.IsHexAggregate(fn))
        {
            if (self == null || model == null) throw new EvalError($"{fn}() is not available here");
            var here = PieceInfo.Location(self, model) ?? throw new EvalError($"{Label(self)} is not in a hex");
            pieces = ctx.InHex(self.MapId, here);
        }
        else if (ExprFacts.IsAttachedAggregate(fn))
        {
            if (self == null || model == null) throw new EvalError($"{fn}() is not available here");
            var display = FormationDisplay(self, model) ?? throw new EvalError($"{Label(self)} has no organisation chart");
            pieces = ctx.OnMap(display.Id);
        }
        else pieces = s.Pieces.Values;
        var text = filter ?? "";
        var match = ctx.Compile(text);
        pieces = pieces.Where(match);
        if (fn is "countof" or "hexcount" or "attachedcount") return Value.Of(pieces.Count());

        var def = (property == null ? null : pack.PropertyByName(property)) ?? throw new EvalError($"Unknown property '{property}'");
        if (def.Scope == PropertyScope.Holders) throw new EvalError($"{def.Name} is not a piece property");
        var values = pieces.Select(p => Ledger.GetOrDefault(s, LedgerKey.Piece(p.Id), def));
        switch (fn)
        {
            case "sum" or "hexsum" or "attachedsum":
                return Value.Of(values.Sum());
            case "maxof" or "hexmax" or "attachedmax":
            case "minof" or "hexmin" or "attachedmin":
                var max = fn is "maxof" or "hexmax" or "attachedmax";
                double? best = null;
                foreach (var v in values)
                    if (best == null || (max ? v > best : v < best)) best = v;
                return best is { } b ? Value.Of(b) : throw new EvalError($"No piece matches \"{text}\"");
            default:
                throw new EvalError($"{fn}() is not available here");
        }
    }

    /// <summary>
    /// The organisation display of a piece's formation: the map named by its first SendTo trait (prototypes expanded, as
    /// <see cref="PieceActions"/> sees them) that is an organisation display; null when it has none.
    /// </summary>
    public static MapDef? FormationDisplay(PieceInstance p, ModuleModel model)
    {
        foreach (var d in PieceInfo.TypeOf(p).Traits.OfType<SendToDef>())
        {
            if (d.MapName.Length == 0) continue;
            var map = model.Maps.FirstOrDefault(m => m.Name == d.MapName || m.Id == d.MapName);
            if (map is { IsOrgDisplay: true }) return map;
        }
        return null;
    }

    /// <summary>A piece as the log names it: its name, else "piece".</summary>
    private static string Label(PieceInstance p)
    {
        var n = PieceInfo.Name(p);
        return string.IsNullOrWhiteSpace(n) ? "piece" : n;
    }
}

/// <summary>
/// The per-context state of aggregate calls (one per evaluation context, or per compiled unit filter, so an aggregate in a unit filter does not
/// redo work per piece): each filter text compiled once, the pieces of each hex (built on the first hex call), and in a player view each
/// call's hidden verdict. Nothing is built until an aggregate is evaluated.
/// </summary>
internal sealed class AggregateFilters(GameState s, RulesPack pack, ModuleModel? model)
{
    private Dictionary<string, Func<PieceInstance, bool>>? _cache;
    private Func<string, Func<PieceInstance, bool>>? _compile;
    private Dictionary<(string?, string), List<PieceInstance>>? _hexes;
    private Dictionary<string, List<PieceInstance>>? _maps;
    private bool? _piecesMayHide;
    private Dictionary<(string, string?, string?), bool>? _hidden;

    public GameState State => s;
    public RulesPack Pack => pack;
    public ModuleModel? Model => model;

    public Func<string, Func<PieceInstance, bool>> Compile => _compile ??= CompileOnce;

    private Func<PieceInstance, bool> CompileOnce(string text)
    {
        _cache ??= new(StringComparer.Ordinal);
        if (_cache.TryGetValue(text, out var hit)) return hit;
        PieceAggregates.FilterCompiles++;
        return _cache[text] = PieceFilter.CompileStrict(text, s, pack, model);
    }

    /// <summary>The pieces on this map at this grid location name, in state order (needs the model; the index is built once).</summary>
    public IReadOnlyList<PieceInstance> InHex(string? mapId, string location)
    {
        if (_hexes == null)
        {
            _hexes = new();
            foreach (var p in s.Pieces.Values)
            {
                if (PieceInfo.Location(p, model!) is not { } at) continue;
                if (!_hexes.TryGetValue((p.MapId, at), out var list)) _hexes[(p.MapId, at)] = list = new();
                list.Add(p);
            }
        }
        return _hexes.TryGetValue((mapId, location), out var found) ? found : Array.Empty<PieceInstance>();
    }

    /// <summary>The pieces on this map (<see cref="PieceInstance.MapId"/> is its id or its name), in state order; the index is built once.</summary>
    public IReadOnlyList<PieceInstance> OnMap(string mapId)
    {
        if (_maps == null)
        {
            _maps = new(StringComparer.Ordinal);
            foreach (var p in s.Pieces.Values)
            {
                if (p.MapId == null) continue;
                if (!_maps.TryGetValue(p.MapId, out var list)) _maps[p.MapId] = list = new();
                list.Add(p);
            }
        }
        var name = model?.Maps.FirstOrDefault(m => m.Id == mapId)?.Name;
        var byId = _maps.TryGetValue(mapId, out var a) ? a : null;
        var byName = name != null && name != mapId && _maps.TryGetValue(name, out var b) ? b : null;
        if (byName == null) return byId ?? (IReadOnlyList<PieceInstance>)Array.Empty<PieceInstance>();
        if (byId == null) return byName;
        var ids = new HashSet<PieceInstance>(byId.Concat(byName));
        return s.Pieces.Values.Where(ids.Contains).ToList();   // keep state order
    }

    /// <summary>
    /// For a player view: true when the call may read data its viewer (<see cref="GameState.ViewSides"/>) cannot see, as
    /// <see cref="PlayerView"/> and the formula masking decide it.
    /// </summary>
    public bool HiddenInView(string fn, string? property, string? filter)
    {
        _piecesMayHide ??= PieceAccess.PiecesMayHide(pack, model, s.ViewSides?.ToHashSet(StringComparer.Ordinal));
        _hidden ??= new();
        if (_hidden.TryGetValue((fn, property, filter), out var known)) return known;
        return _hidden[(fn, property, filter)] = FormulaVariables.AggregateCallHidden(pack, fn, property, filter, _piecesMayHide.Value);
    }
}
