using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using Quartermaster.Vassal;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Core;

public sealed record TableCell(string TableId, int Row, int Col);

/// <param name="Skipped">The binding's "Only when" condition is false: it is not used in this run.</param>
/// <param name="Waiting">Neither filled, missing nor skipped: its "Only when" condition (or the precondition, for a roll) is not decided yet.</param>
public sealed record VariableState(string Variable, Value? Value, bool Auto, bool Overridden, string? MissingReason,
    string? Source = null, TableCell? Cell = null, bool Skipped = false, bool Waiting = false);

public sealed record OutputPreview(string Key, string PropertyId, string PropertyName, double Before, double After)
{
    /// <summary>The output's target (Unit, Global, Holder or Piece).</summary>
    public OutputTarget Target { get; init; }
    /// <summary>Target Piece: the target piece's side (side map of the run's state; null = a piece of no side). Null otherwise.</summary>
    public string? SideId { get; init; }
    /// <summary>This output's working part ("Strength 7 − 2 = 5"); a Piece output's log line is "{formula}: {part}".</summary>
    public string? Part { get; init; }
    /// <summary>Target Piece: the target's name as its binding names it for the requester ("Unknown piece" for a masked pick).</summary>
    public string? Subject { get; init; }
    /// <summary>Target Piece: the target's real name (the subject of its log line, which only the target's side reads in full).</summary>
    internal string? TargetName { get; init; }
}

public sealed record FormulaTrace(FormulaDef Formula, IReadOnlyList<VariableState> Variables, Value? Result, string Working,
    IReadOnlyList<OutputPreview> Outputs, string? Error, IReadOnlyList<DiceEntry> Rolls,
    IReadOnlyDictionary<string, IReadOnlyList<int>> RollsByKey, string? PieceId = null, bool Refused = false)
{
    /// <summary>The working before the output parts ("{text} = {result}"), so the acting side's lines can leave out Piece parts. Null = a
    /// trace not made by the runner (its <see cref="Working"/> is used as it is).</summary>
    internal string? Calc { get; init; }

    /// <summary>The run's effects (place, remove, layer and mask changes) in command order: Place, layer/mask changes in output order, then
    /// removals in output order. Empty for a run with none.</summary>
    public IReadOnlyList<PieceEffect> Effects { get; init; } = Array.Empty<PieceEffect>();
}

/// <param name="Dice">Rolls a key's dice when no matching pin is in <paramref name="FixedRolls"/> (key, count, sides); null = the runner's
/// roller. The online host uses it so every die of a guest's run is pinned per dice configuration.</param>
/// <param name="PickSides">The sides whose view a Piece binding's typed label is resolved in (<see cref="PieceChoices"/>): the requesting
/// guest's sides on the host; null = every piece (solo, the host's own full-state runs, a run on a player view state).</param>
public sealed record RunRequest(string FormulaId, string? PieceId, IReadOnlyDictionary<string, string> Typed,
    IReadOnlyDictionary<string, string> Overrides, IReadOnlyDictionary<string, IReadOnlyList<int>> FixedRolls, double? Moved = null, bool UseAutoValues = false,
    Func<string, int, int, IReadOnlyList<int>>? Dice = null, IReadOnlySet<string>? PickSides = null);

/// <summary>Binds a formula's variables to game data, evaluates it and previews/produces the ledger commands. Run never throws.</summary>
public sealed class FormulaRunner(RulesPack pack, ModuleModel model, Func<int, int, IReadOnlyList<int>> roller)
{
    private const string NeedsUnit = "This formula needs a unit";
    private const string HiddenText = "Hidden from this player";
    private readonly RulesPack _pack = pack;
    public RulesPack Pack => _pack;
    private readonly ModuleModel _model = model;
    public ModuleModel Model => _model;
    private readonly Func<int, int, IReadOnlyList<int>> _roller = roller;

    /// <summary>A variable that cannot be filled yet (not an error).</summary>
    private sealed class Missing(string reason) : Exception(reason);

    /// <summary>A roll attempted while evaluating the precondition.</summary>
    private sealed class RollsForbidden() : Exception("Rolls are not allowed here");

    /// <summary>A variable that waits on another (an "Only when" not yet known, or a roll held until the precondition is decided).</summary>
    private sealed class Waits() : Exception("Waiting");

    /// <summary>
    /// The parsed expressions of each formula record (text, precondition, output sources, binding expressions), keyed by source text: a value
    /// is an <see cref="AstExpr"/> or the <see cref="ParseError"/> it gave. Records are replaced on edit, so an edited formula parses again.
    /// </summary>
    private static readonly ConditionalWeakTable<FormulaDef, ConcurrentDictionary<string, object>> ParseCache = new();

    /// <summary>How many expressions the runner parsed on this thread (cache misses; tests read it).</summary>
    [ThreadStatic] internal static int Parses;

    /// <summary>The parsed <paramref name="text"/> of <paramref name="f"/>, parsed once per record; throws the <see cref="ParseError"/> it gives.</summary>
    private static AstExpr Parse(FormulaDef f, string text)
    {
        var map = ParseCache.GetValue(f, _ => new ConcurrentDictionary<string, object>(StringComparer.Ordinal));
        if (!map.TryGetValue(text, out var hit))
        {
            Parses++;
            try { hit = Parser.Parse(text); }
            catch (ParseError pe) { hit = pe; }
            hit = map.GetOrAdd(text, hit);
        }
        return hit is ParseError err ? throw new ParseError(err.Position, err.Detail) : (AstExpr)hit;
    }

    private Pass? _pass;

    /// <summary>Makes the id of a piece a Place output adds, when the trace is made (so previews and the placed piece's ledger keys agree).</summary>
    public Func<string> NewPieceId { get; set; } = () => "p:" + Guid.NewGuid().ToString("N");

    /// <summary>How many times an open pass dropped its side map and aggregate state after piece commands (tests read it).</summary>
    [ThreadStatic] internal static int PassRefreshes;


    /// <summary>The pass open on this thread for <paramref name="s"/> (<see cref="BeginPass"/>), else null.</summary>
    private Pass? PassFor(GameState s) =>
        _pass is { } p && ReferenceEquals(p.State, s) && p.Thread == Environment.CurrentManagedThreadId ? p : null;

    /// <summary>
    /// Opens a pass over <paramref name="s"/> (spec #31): until disposed, runs on that same state instance, on this thread, share one side map
    /// and one aggregate state (compiled filters, hex and map indexes) instead of building them per run. Values are always read live, so a
    /// unit sees the ledger writes of the units before it. Invariant: the commands of a run executed while a pass is open must be followed by
    /// <see cref="AfterApply(IReadOnlyList{GameCommand})"/>, which drops the side map and aggregate state when any of them adds, removes or
    /// moves a piece or changes a trait state (a Place, Remove, SetLayer or SetMask output), so later units see the change. Ledger writes,
    /// rolls and log lines keep them. A nested call on the same state joins the open pass; on another state, or another thread, it opens nothing.
    /// </summary>
    public IDisposable BeginPass(GameState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (_pass is { } open)
        {
            if (!ReferenceEquals(open.State, s) || open.Thread != Environment.CurrentManagedThreadId) return new PassScope(this, null);
            open.Depth++;
            return new PassScope(this, open);
        }
        var pass = new Pass(this, s);
        _pass = pass;
        return new PassScope(this, pass);
    }

    /// <summary>Called after a unit's commands were executed inside a pass: drops any value the pass memoised. No value is memoised today
    /// (filters, the side map and aggregates read the state live), so it keeps the contract for later caches.</summary>
    public void AfterApply()
    {
        if (_pass is { } p && p.Thread == Environment.CurrentManagedThreadId) p.AfterApply();
    }

    /// <summary>
    /// Called after a unit's commands were executed inside a pass, with those commands. When any adds, removes or moves a piece or changes a
    /// trait state (<see cref="AddPiece"/>, <see cref="RemovePiece"/>, <see cref="MovePieces"/>, <see cref="SetTraitState"/>), the open pass
    /// drops its side map and aggregate state (hex and map indexes, compiled filters); the next run rebuilds them. Ledger-only commands keep them.
    /// </summary>
    public void AfterApply(IReadOnlyList<GameCommand> executed)
    {
        if (_pass is not { } p || p.Thread != Environment.CurrentManagedThreadId) return;
        p.AfterApply();
        if (executed != null && executed.Any(c => c is AddPiece or RemovePiece or MovePieces or SetTraitState)) p.Refresh();
    }

    private sealed class Pass(FormulaRunner owner, GameState state)
    {
        public GameState State => state;
        public int Thread { get; } = Environment.CurrentManagedThreadId;
        public int Depth = 1;
        public Lazy<Func<PieceInstance, string?>> Sides { get; private set; } = PieceFilter.SideMap(state, owner._pack);
        private AggregateFilters? _filters;
        public AggregateFilters Filters => _filters ??= new(state, owner._pack, owner._model) { SharedSides = Sides };
        public void AfterApply() { }

        /// <summary>Drops the side map and the aggregate state (a piece was added, removed, moved or changed its trait states).</summary>
        public void Refresh()
        {
            Sides = PieceFilter.SideMap(state, owner._pack);
            _filters = null;
            PassRefreshes++;
        }
    }

    private sealed class PassScope(FormulaRunner owner, Pass? pass) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done || pass == null) return;
            _done = true;
            if (--pass.Depth == 0 && ReferenceEquals(owner._pass, pass)) owner._pass = null;
        }
    }

    public FormulaTrace Run(GameState s, RunRequest r)
    {
        var formula = _pack.ById<FormulaDef>(r.FormulaId);
        if (formula == null) return Fail(new FormulaDef { Id = r.FormulaId }, "Formula not found");
        var produced = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        try
        {
            var t = RunCore(s, r, formula, produced);
            return r.PieceId != null && s.Pieces.ContainsKey(r.PieceId) ? t with { PieceId = r.PieceId } : t;
        }
        catch (Exception ex)
        {
            return Fail(formula, ex is EvalError or ParseError ? ex.Message : "Could not run formula: " + ex.Message, produced);
        }
    }

    private static FormulaTrace Fail(FormulaDef f, string error, IReadOnlyDictionary<string, IReadOnlyList<int>>? produced = null) =>
        new(f, Array.Empty<VariableState>(), null, "", Array.Empty<OutputPreview>(), error, Array.Empty<DiceEntry>(),
            produced == null ? new Dictionary<string, IReadOnlyList<int>>() : new Dictionary<string, IReadOnlyList<int>>(produced));

    private FormulaTrace RunCore(GameState s, RunRequest r, FormulaDef formula, Dictionary<string, IReadOnlyList<int>> produced)
    {
        AstExpr expr;
        try { expr = Parse(formula, formula.Text ?? ""); }
        catch (ParseError pe) { return Fail(formula, pe.Message, produced); }

        var piece = r.PieceId != null && s.Pieces.TryGetValue(r.PieceId, out var pi) ? pi : null;
        var run = new RunScope(this, s, r, formula, piece, produced);

        // A Piece-target output names the Piece binding whose pick it acts on; without one it can never run.
        var pieceVars = new List<string>();
        foreach (var o in formula.Outputs)
        {
            if (o.Target != OutputTarget.Piece) continue;
            if (string.IsNullOrWhiteSpace(o.PieceVariable) || formula.Bindings.FirstOrDefault(x => x.Variable == o.PieceVariable) is not { Kind: BindingKind.Piece })
                throw new EvalError("An output refers to a deleted Piece variable");
            if (!pieceVars.Contains(o.PieceVariable)) pieceVars.Add(o.PieceVariable);
        }

        AstExpr? pre = string.IsNullOrWhiteSpace(formula.Precondition) ? null : Parse(formula, formula.Precondition);

        // Variables of the text, then of the precondition, then of the outputs' value and pick-by expressions, then any used only inside
        // Table binding row/column expressions, "Only when" conditions or Roll binding dice count expressions.
        var names = new List<string>(Parser.Variables(expr));
        if (pre != null) foreach (var v in Parser.Variables(pre)) if (!names.Contains(v)) names.Add(v);
        foreach (var src in FormulaVariables.OutputSources(formula))
            foreach (var v in Parser.Variables(Parse(formula, src))) if (!names.Contains(v)) names.Add(v);
        foreach (var v in pieceVars) if (!names.Contains(v)) names.Add(v);   // an output's target is asked even when nothing reads its value
        for (var i = 0; i < names.Count; i++)
        {
            var b = formula.Bindings.FirstOrDefault(x => x.Variable == names[i]);
            if (b == null) continue;
            var srcs = b.Kind switch
            {
                BindingKind.Table => new[] { b.RowExpr, b.ColExpr, b.When },
                BindingKind.Roll => new[] { b.When, b.DiceCountExpr },
                _ => new[] { b.When },
            };
            foreach (var src in srcs)
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                foreach (var v in Parser.Variables(Parse(formula, src)))
                    if (v != b.Variable && !names.Contains(v)) names.Add(v);
            }
        }

        // The precondition runs before the text's variables are resolved and never rolls; unknown (an input not filled) waits.
        var prePending = false;
        if (pre != null)
        {
            bool? ok;
            try { ok = run.Condition(pre, rolls: false); }
            catch (RollsForbidden) { throw new EvalError($"The precondition of {formula.Name} cannot roll dice"); }
            if (ok == false)
            {
                var seen = names.Select(run.Cached).OfType<VariableState>().ToList();
                return new(formula, seen, null, "", Array.Empty<OutputPreview>(), formula.RefusalMessage ?? $"{formula.Name} cannot be run now",
                    run.Rolls, run.RollsByKey, Refused: true);
            }
            prePending = ok == null;
            if (prePending) run.HoldRolls();   // nothing is rolled for the text until the precondition is decided
        }

        var states = names.Select(run.Resolve).ToList();
        var rolls = run.Rolls;

        var needsUnit = formula.Bindings.Any(b => b.Kind == BindingKind.Unit)
            || formula.Outputs.Any(o => o.Target == OutputTarget.Unit);
        if (needsUnit && piece == null)
            return new(formula, states, null, "", Array.Empty<OutputPreview>(), NeedsUnit, rolls, run.RollsByKey);

        if (prePending || run.Waiting || states.Any(x => x.MissingReason != null))
            return new(formula, states, null, "", Array.Empty<OutputPreview>(), null, rolls, run.RollsByKey);

        // The pieces the Piece outputs act on. A target that cannot be resolved, or (on a player view) is a masked placeholder, leaves its
        // binding missing: the run waits, as for the binding itself.
        FormulaTrace Unresolved(string variable, string reason) =>
            new(formula, states.Select(x => x.Variable == variable ? x with { Value = null, MissingReason = reason } : x).ToList(), null, "",
                Array.Empty<OutputPreview>(), null, rolls, run.RollsByKey);
        var targets = new Dictionary<string, (PieceInstance Piece, string Name)>(StringComparer.Ordinal);
        foreach (var v in pieceVars)
        {
            try { targets[v] = run.Pick(formula.Bindings.First(x => x.Variable == v)); }
            catch (Missing m) { return Unresolved(v, m.Message); }
            if (s.IsPlayerView && PlayerView.IsPlaceholder(targets[v].Piece)) return Unresolved(v, HiddenText);
        }

        var result = Evaluator.Evaluate(expr, new RunContext(run));
        var working = WorkingLine.Render(expr, formula.Text ?? "", n => run.Cached(n)?.Value, run.Label) + " = " + result.Display();

        var outputs = new List<OutputPreview>();
        var parts = new List<string>();
        var calc = working;
        string OutputError(int oi, string detail) => $"Formula \"{formula.Name}\": output {oi + 1}{detail}";

        // The placed piece first, so a Placed output listed before its Place still acts on it. At most one Place per formula.
        Placed? placed = null;
        PieceEffect? place = null;
        for (var oi = 0; oi < formula.Outputs.Count; oi++)
        {
            var o = formula.Outputs[oi];
            if (o.Action != OutputAction.Place) continue;
            if (placed != null) throw new EvalError(OutputError(oi, " places a second piece; only one output may place a piece"));
            var anchor = o.Target switch
            {
                OutputTarget.Unit => piece!,
                OutputTarget.Piece => targets[o.PieceVariable!].Piece,
                _ => throw new EvalError(OutputError(oi, $": {o.Action} cannot target {o.Target}")),
            };
            var slotText = (o.Slot ?? "").Trim();
            var slot = (slotText.Length == 0 ? null : _model.SlotByPath(slotText) ?? _model.SlotByGpid(slotText))
                ?? throw new EvalError($"Palette entry \"{slotText}\" was not found in the module");
            var (type, state) = PieceFactory.FromSlot(_model, slot, anchor.MapId, anchor.Pos);
            var id = NewPieceId();
            var inst = new PieceInstance { Id = id, TypeString = type, MapId = anchor.MapId, Pos = anchor.Pos, TraitStates = GameState.DecodeState(type, state) };
            var name = PieceInfo.Name(inst)?.Trim() is { Length: > 0 } n ? n : slot.EntryName;
            placed = new Placed(inst, name, run.Sides.Value(inst));
            place = new PieceEffect(PieceEffectKind.Place, id, $"placed {name}")
            {
                LogText = $"{formula.Name}: placed {name}", Name = name, TypeString = type, StateString = state, MapId = anchor.MapId,
                Pos = anchor.Pos, StackOnto = anchor.StackId, SideId = placed.Side,
            };
        }

        // The piece an effect acts on: its instance (the placed piece's is not in the state yet), its real name, the name the requester sees.
        (PieceInstance Piece, string Real, string Shown, string? Side) EffectTarget(FormulaOutput o, int oi)
        {
            switch (o.Target)
            {
                case OutputTarget.Unit: { var real = RealName(piece!); return (piece!, real, real, run.Sides.Value(piece!)); }
                case OutputTarget.Piece: { var tp = targets[o.PieceVariable!]; return (tp.Piece, RealName(tp.Piece), tp.Name, run.Sides.Value(tp.Piece)); }
                case OutputTarget.Placed when o.Action is OutputAction.SetLayer or OutputAction.SetMask:
                    return placed is { } pl ? (pl.Piece, pl.Name, pl.Name, pl.Side) : throw new EvalError(NothingPlaced);
                default: throw new EvalError(OutputError(oi, $": {o.Action} cannot target {o.Target}"));
            }
        }
        Value EffectValue(FormulaOutput o) => string.IsNullOrWhiteSpace(o.Expr) ? result : Evaluator.Evaluate(Parse(formula, o.Expr), new RunContext(run));

        var traitEffects = new List<PieceEffect>();
        var removals = new List<PieceEffect>();
        var pending = new Dictionary<(string, int), string>();   // trait states this run already set (a second change reads the first)
        // Every output is computed (value and property) before the trace is returned, so a failing output writes nothing.
        for (var oi = 0; oi < formula.Outputs.Count; oi++)
        {
            var o = formula.Outputs[oi];
            switch (o.Action)
            {
                case OutputAction.Write: break;
                case OutputAction.Place: continue;
                case OutputAction.Remove:
                {
                    if (o.Target == OutputTarget.Placed) throw new EvalError(OutputError(oi, $": {o.Action} cannot target {o.Target}"));
                    var tg = EffectTarget(o, oi);
                    if (string.IsNullOrWhiteSpace(o.ToMapId))
                    {
                        removals.Add(new PieceEffect(PieceEffectKind.Remove, tg.Piece.Id, $"removed {tg.Shown}")
                            { LogText = $"{formula.Name}: removed {tg.Real}", Name = tg.Real, SideId = tg.Side });
                        continue;
                    }
                    var mapId = o.ToMapId.Trim();
                    var map = _model.Maps.FirstOrDefault(m => m.Id == mapId) ?? _model.Maps.FirstOrDefault(m => m.Name == mapId)
                        ?? throw new EvalError($"Map \"{mapId}\" was not found in the module");
                    if (map.Boards.Count == 0) throw new EvalError($"Map \"{map.Name}\" has no board");
                    var board = map.Boards[0];
                    var centre = new PointD(board.Offset.X + board.Width / 2.0, board.Offset.Y + board.Height / 2.0);
                    removals.Add(new PieceEffect(PieceEffectKind.Remove, tg.Piece.Id, $"moved {tg.Shown} to {map.Name}")
                    {
                        LogText = $"{formula.Name}: moved {tg.Real} to {map.Name}", Name = tg.Real, ToMapId = map.Id, Pos = centre, SideId = tg.Side,
                    });
                    continue;
                }
                case OutputAction.SetLayer:
                case OutputAction.SetMask:
                {
                    var tg = EffectTarget(o, oi);
                    var traits = PieceInfo.TypeOf(tg.Piece).Traits;
                    string Current(int i) => pending.TryGetValue((tg.Piece.Id, i), out var st) ? st
                        : i < tg.Piece.TraitStates.Count ? tg.Piece.TraitStates[i] ?? "" : "";
                    var setTo = EffectValue(o);
                    var whole = setTo.Kind == ValueKind.Number && double.IsFinite(setTo.Number) && setTo.Number == Math.Floor(setTo.Number);
                    int index;
                    string newState, shown, logged;
                    if (o.Action == OutputAction.SetLayer)
                    {
                        var (li, def) = PieceInfo.FindLayer(PieceInfo.TypeOf(tg.Piece), o.Layer ?? "")
                            ?? throw new EvalError($"{tg.Shown} has no layer \"{(o.Layer ?? "").Trim()}\"");
                        var layer = def.Name.Trim();
                        if (def.FollowProperty) throw new EvalError($"Layer \"{layer}\" follows a property, so it cannot be set");
                        var levels = Math.Max(Math.Max(def.Images.Count, def.LevelNames.Count), 1);
                        if (!whole || setTo.Number < (def.AlwaysActive ? 1 : 0) || setTo.Number > levels)
                            throw new EvalError($"Layer \"{layer}\" has no level {setTo.Display()}");
                        var n = (int)setTo.Number;
                        var cur = Current(li);
                        newState = n > 0 ? TraitState.LayerSetLevel(def, cur, n, true)
                            : TraitState.LayerSetLevel(def, cur, TraitState.LayerLevel(def, cur).Level, false);   // 0: off, the level kept
                        var level = n >= 1 && n <= def.LevelNames.Count && def.LevelNames[n - 1]?.Trim() is { Length: > 0 } ln
                            ? ln : n.ToString(CultureInfo.InvariantCulture);
                        index = li;
                        shown = $"{layer} of {tg.Shown} = {level}";
                        logged = $"{layer} of {tg.Real} = {level}";
                    }
                    else
                    {
                        index = -1;
                        for (var i = 0; i < traits.Count && index < 0; i++) if (traits[i] is ObscurableDef) index = i;
                        if (index < 0) throw new EvalError($"{tg.Shown} has no mask");
                        if (!whole || setTo.Number is not (0 or 1)) throw new EvalError("The mask value must be 0 or 1");
                        var on = setTo.Number == 1;
                        newState = TraitState.ObscuredSet((ObscurableDef)traits[index], Current(index), on, MaskObscurer);
                        var verb = on ? "masked" : "unmasked";
                        shown = $"{tg.Shown} {verb}";
                        logged = $"{tg.Real} {verb}";
                    }
                    pending[(tg.Piece.Id, index)] = newState;
                    traitEffects.Add(new PieceEffect(o.Action == OutputAction.SetLayer ? PieceEffectKind.SetLayer : PieceEffectKind.SetMask, tg.Piece.Id, shown)
                        { LogText = $"{formula.Name}: {logged}", Name = tg.Real, TraitIndex = index, NewState = newState, SideId = tg.Side });
                    continue;
                }
                default:
                    throw new EvalError(OutputError(oi, $": {o.Action} cannot target {o.Target}"));
            }
            if (o.Target == OutputTarget.Placed && placed == null) throw new EvalError(NothingPlaced);
            var prop = OutputProperty(o, run);
            // an old pack edited by hand (the validator not run): a computed value is never written
            if (PropertyFacts.IsComputed(prop))
                throw new EvalError($"Formula \"{formula.Name}\": output {oi + 1} writes computed property \"{prop.Name}\"");
            string key;
            (string Real, string Shown, string? Side)? target = null;
            double? before = null;
            switch (o.Target)
            {
                case OutputTarget.Unit: key = LedgerKey.Piece(piece!.Id); break;
                case OutputTarget.Global: key = LedgerKey.Holder(HolderDef.GlobalId); EnsureKept(_pack, HolderDef.GlobalId, prop); break;
                case OutputTarget.Piece:
                case OutputTarget.Placed:
                {
                    // the scope rule of a Unit output (the validator's text): a holder-only property is never written to a piece
                    if (prop.Scope == PropertyScope.Holders)
                        throw new EvalError($"Formula \"{formula.Name}\": output property \"{prop.Name}\" cannot be written to a piece (its scope is {prop.Scope})");
                    if (o.Target == OutputTarget.Placed)
                    {
                        // the new piece has no ledger yet: Before is the property's Default (its seeds run after it is added, before this write)
                        target = (placed!.Name, placed.Name, placed.Side);
                        key = LedgerKey.Piece(placed.Piece.Id);
                        before = prop.Default;
                        break;
                    }
                    var tp = targets[o.PieceVariable!];
                    // on a player view, a non-Public value of a side the view was not made for is hidden (as the binding reads it)
                    if (s.IsPlayerView && !PropertyFacts.IsPublic(_pack, prop) && run.Sides.Value(tp.Piece) is { } tside && s.ViewSides?.Contains(tside) != true)
                        return Unresolved(o.PieceVariable!, HiddenText);
                    target = (RealName(tp.Piece), tp.Name, run.Sides.Value(tp.Piece));
                    key = LedgerKey.Piece(tp.Piece.Id);
                    break;
                }
                case OutputTarget.Holder:
                default:
                    if (o.HolderId == null || (o.HolderId != HolderDef.GlobalId && _pack.ById<HolderDef>(o.HolderId) == null))
                        throw new EvalError("An output refers to a deleted holder");
                    EnsureKept(_pack, o.HolderId, prop);
                    key = LedgerKey.Holder(o.HolderId);
                    break;
            }
            Value value;
            if (string.IsNullOrWhiteSpace(o.Expr))
            {
                if (result.Kind != ValueKind.Number) throw new EvalError("The result is not a number, so it cannot change " + prop.Name);
                value = result;
            }
            else
            {
                value = Evaluator.Evaluate(Parse(formula, o.Expr), new RunContext(run));
                if (value.Kind != ValueKind.Number) throw new EvalError($"The value for {prop.Name} is not a number");
            }
            var x = value.Number;
            var was = before ?? Ledger.GetOrDefault(s, key, prop);
            var after = o.Op switch { OutputOp.Subtract => was - x, OutputOp.Add => was + x, _ => x };
            if (double.IsNaN(after) || double.IsInfinity(after)) throw new EvalError("Result is not a finite number");
            var b = Value.Of(was).Display();
            var xs = value.Display();
            var a = Value.Of(after).Display();
            string Part(string what) => o.Op switch
            {
                OutputOp.Subtract => $"{what} {b} − {xs} = {a}",
                OutputOp.Add => $"{what} {b} + {xs} = {a}",
                _ => $"{what} = {PropertyLabels.Text(_pack, prop, after)}",
            };
            var part = Part(prop.Name);
            outputs.Add(new OutputPreview(key, prop.Id, prop.Name, was, after)
            {
                Target = o.Target,
                Part = part,
                SideId = target?.Side,
                Subject = target?.Shown,
                TargetName = target?.Real,
            });
            // the dialog's working names the target as its binding does ("Fuel of Blue Barge 4 + 2 = 6")
            parts.Add(target is { } named ? Part($"{prop.Name} of {named.Shown}") : part);
        }
        // Effects in command order: the Place, the layer and mask changes (output order), then the removals (output order).
        var effects = new List<PieceEffect>();
        if (place != null) effects.Add(place);
        effects.AddRange(traitEffects);
        effects.AddRange(removals);
        parts.AddRange(effects.Select(e => e.Text));
        if (parts.Count > 0) working += " → " + string.Join("; ", parts);
        return new(formula, states, result, working, outputs, null, rolls, run.RollsByKey) { Calc = calc, Effects = effects };
    }

    private const string NothingPlaced = "An output writes the placed piece, but nothing was placed";
    /// <summary>The obscurer a SetMask output writes (as the Mask menu does; <see cref="PieceAccess.Masking"/> ignores it).</summary>
    private const string MaskObscurer = "local";

    /// <summary>The piece a Place output adds (not in the state until its AddPiece executes), its name and its side.</summary>
    private sealed record Placed(PieceInstance Piece, string Name, string? Side);

    /// <summary>A piece's name on its own log lines: its real name, else "Unknown piece".</summary>
    private static string RealName(PieceInstance p) => PieceInfo.Name(p)?.Trim() is { Length: > 0 } real ? real : PieceChoices.UnknownName;

    /// <summary>The name a run failure gives a holder: "Global" for the global holder, else the holder's name (its id when deleted).</summary>
    private static string HolderText(RulesPack pack, string holderId) =>
        holderId == HolderDef.GlobalId ? "Global" : pack.ById<HolderDef>(holderId)?.Name ?? holderId;

    /// <summary>Fails the run with "{holder} does not keep {property}" when the property lists holders and this one is not among them.</summary>
    private static void EnsureKept(RulesPack pack, string holderId, PropertyDef prop)
    {
        if (!PropertyFacts.KeptBy(prop, holderId)) throw new EvalError($"{HolderText(pack, holderId)} does not keep {prop.Name}");
    }

    /// <summary>The output's fixed property, or with a property list the one its pick-by expression names: a whole number N is the Nth listed
    /// property, a text the listed property whose name equals it (trimmed, case-insensitive).</summary>
    private PropertyDef OutputProperty(FormulaOutput o, RunScope run)
    {
        PropertyDef Def(string id) => _pack.ById<PropertyDef>(id) ?? throw new EvalError("An output refers to a deleted property");
        if (o.PropertyChoice is not { Count: > 0 } list) return Def(o.Property);
        var pick = Evaluator.Evaluate(Parse(run.Formula, o.PropertySelect ?? ""), new RunContext(run));
        var props = list.Select(Def).ToList();
        PropertyDef? hit = pick.Kind switch
        {
            ValueKind.Number when pick.Number == Math.Floor(pick.Number) && pick.Number >= 1 && pick.Number <= props.Count => props[(int)pick.Number - 1],
            ValueKind.Text => props.FirstOrDefault(p => string.Equals(p.Name.Trim(), (pick.Text ?? "").Trim(), StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        return hit ?? throw new EvalError($"{pick.Display()} is not one of the output's properties");
    }

    /// <summary>
    /// The commands applying a trace. Each carries the unit the formula ran on (<see cref="FormulaTrace.PieceId"/>) and, when given, the side
    /// it was run for, so PlayerView can attribute rolls and working explicitly. Whatever the listed output order, the order is fixed: rolls;
    /// the Place (<see cref="AddPiece"/>); own writes (Unit, Global, Holder); Piece writes; Placed writes; each layer or mask change with its
    /// System line (output order); the working line when no own write carries it; then removals and moves (output order), so every line
    /// before a removal is logged against a piece that still exists. The caller seeds the placed piece right after its AddPiece executes.
    /// </summary>
    public IReadOnlyList<GameCommand> Commands(FormulaTrace t, string? note, string? tag = null, string? subject = null, string? sideId = null)
    {
        var list = new List<GameCommand>();
        if (t.Result == null || t.Error != null) return list;
        foreach (var d in t.Rolls) list.Add(new RollDice(d.Purpose, d.Count, d.Sides, d.Results, PieceId: t.PieceId, SideId: sideId));
        var own = t.Outputs.Where(o => o.Target is not (OutputTarget.Piece or OutputTarget.Placed)).ToList();
        var onPieces = t.Outputs.Where(o => o.Target == OutputTarget.Piece).ToList();
        var onPlaced = t.Outputs.Where(o => o.Target == OutputTarget.Placed).ToList();
        var effects = t.Effects ?? Array.Empty<PieceEffect>();
        // The acting side's working leaves out the Piece and Placed parts (those go on the targets' own lines, which the target's side reads)
        // and lists the effects by the names the acting player sees.
        var working = t.Calc == null ? t.Working
            : t.Calc + (own.Count + effects.Count == 0 ? "" : " → " + string.Join("; ", own.Select(o => o.Part).Concat(effects.Select(e => e.Text))));
        foreach (var e in effects.Where(e => e.Kind == PieceEffectKind.Place))
            list.Add(new AddPiece(e.PieceId, e.TypeString!, e.StateString ?? "", e.MapId!, e.Pos, e.StackOnto, e.LogText));
        var first = true;
        foreach (var o in own)
        {
            // The first own line records the applied run (runs(), spec #71): exactly one stamped line per run.
            list.Add(new LedgerDelta(o.Key, o.PropertyId, o.Before, o.After, "formula", t.Formula.Id, t.Formula.RuleRefId, note, working, Tag: tag,
                Subject: subject, PieceId: t.PieceId, SideId: sideId) { AppliedRun = first ? t.Formula.Id : null });
            first = false;
        }
        // A Piece (or Placed) write is the target's line: keyed on it, stamped with its side, subject its real name, working "{formula}: {part}".
        foreach (var o in onPieces.Concat(onPlaced))
        {
            LedgerKey.TryParse(o.Key, out _, out var targetId);
            list.Add(new LedgerDelta(o.Key, o.PropertyId, o.Before, o.After, "formula", t.Formula.Id, t.Formula.RuleRefId, note,
                $"{t.Formula.Name}: {o.Part}", Tag: tag, Subject: o.TargetName ?? o.Subject, PieceId: targetId, SideId: o.SideId));
        }
        // A layer or mask change: the trait state, then a System line by the piece's real name, stamped with its side.
        foreach (var e in effects.Where(e => e.Kind is PieceEffectKind.SetLayer or PieceEffectKind.SetMask))
        {
            list.Add(new SetTraitState(e.PieceId, e.TraitIndex, e.NewState ?? ""));
            list.Add(new AddLogLine(LogKind.System, e.LogText, e.PieceId, SideId: e.SideId));
        }
        // With no own line, the run's working (and its run stamp) goes on a line of its own, on the run piece, for the acting side.
        if (own.Count == 0 && (onPieces.Count + onPlaced.Count > 0 || effects.Count > 0))
            list.Add(new AddLogLine(LogKind.Calc, $"{subject ?? t.Formula.Name}: {working}" + (tag == null ? "" : $" ({tag})"), t.PieceId,
                SideId: sideId, FormulaId: t.Formula.Id) { AppliedRun = t.Formula.Id });
        // Removals last: a delete line is stamped with the removed piece's side (its owner keeps it, other sides drop it).
        foreach (var e in effects.Where(e => e.Kind == PieceEffectKind.Remove))
            list.Add(e.ToMapId == null
                ? new RemovePiece(e.PieceId, e.LogText) { SideId = e.SideId }
                : new MovePieces(new[] { e.PieceId }, e.ToMapId, e.Pos, null, e.LogText));
        return list;
    }

    /// <summary>Per-run resolution of bindings to values.</summary>
    private sealed class RunScope(FormulaRunner owner, GameState s, RunRequest r, FormulaDef formula, PieceInstance? piece, Dictionary<string, IReadOnlyList<int>> byKey)
    {
        private readonly Dictionary<string, VariableState> _done = new(StringComparer.Ordinal);
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private readonly List<DiceEntry> _rolls = new();
        public IReadOnlyList<DiceEntry> Rolls => _rolls;
        public PieceInstance? Piece => piece;
        public FormulaDef Formula => formula;
        private readonly Pass? _pass = owner.PassFor(s);
        public GameState State => s;
        public RulesPack Pack => owner._pack;
        /// <summary>The side map, compiled once per run (or per pass, <see cref="BeginPass"/>) on the first side() call.</summary>
        public Lazy<Func<PieceInstance, string?>> Sides => _sides ??= _pass?.Sides ?? PieceFilter.SideMap(s, owner._pack);
        private Lazy<Func<PieceInstance, string?>>? _sides;
        public ModuleModel Model => owner._model;
        /// <summary>The compiled filters of this run's aggregate calls, each filter text compiled once per run (or per pass), sharing <see cref="Sides"/>.</summary>
        public AggregateFilters Filters => _filters ??= _pass?.Filters ?? new(s, owner._pack, owner._model) { SharedSides = Sides };
        private AggregateFilters? _filters;
        private readonly Dictionary<string, IReadOnlyList<int>> _byKey = byKey;
        private int _inlineDice;
        public IReadOnlyDictionary<string, IReadOnlyList<int>> RollsByKey => _byKey;

        /// <summary>Rolls (or reuses the pinned roll for) a key: a Roll binding's variable name, or "dice#i" for the i-th inline dice() call of this run.
        /// A pin whose count or sides do not match is ignored. Inline call order follows evaluation order, so an `if` that changes branch can renumber keys.</summary>
        public IReadOnlyList<int> RollKey(string key, int n, int sides)
        {
            if (_noRolls) throw new RollsForbidden();
            IReadOnlyList<int> res;
            if (r.FixedRolls.TryGetValue(key, out var pin) && pin != null && pin.Count == n && pin.All(x => x >= 1 && x <= sides)) res = pin;
            else res = r.Dice?.Invoke(key, n, sides) ?? owner._roller(n, sides);
            Record(key, n, sides, res);
            return res;
        }

        /// <summary>Logs a key's dice (rolled, pinned or typed one by one) and keeps them for count().</summary>
        private void Record(string key, int n, int sides, IReadOnlyList<int> dice)
        {
            _rolls.Add(new DiceEntry(0, $"{formula.Name}: {key}", n, sides, dice));
            _byKey[key] = dice;
        }

        public IReadOnlyList<int> Roll(int n, int sides) => RollKey("dice#" + _inlineDice++, n, sides);

        public VariableState? Cached(string name) => _done.TryGetValue(name, out var v) ? v : null;

        /// <summary>
        /// The label of a variable's value in the working (<see cref="PropertyLabels"/>): only for a Unit, Global, Holder or Piece binding of a
        /// property with a labels table, and only from the value this run already holds (so it shows nothing the working does not).
        /// </summary>
        public string? Label(string name, Value value)
        {
            if (value.Kind != ValueKind.Number) return null;
            if (formula.Bindings.FirstOrDefault(x => x.Variable == name) is not { Kind: BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Piece } b)
                return null;
            if (b.Property == null || owner._pack.ById<PropertyDef>(b.Property) is not { Labels: { Length: > 0 } } prop) return null;
            return PropertyLabels.Label(owner._pack, prop, value.Number);
        }

        private bool _noRolls;
        private bool _holdRolls;

        /// <summary>From now on a variable that would roll waits instead (the precondition is not decided yet).</summary>
        public void HoldRolls() { _noRolls = true; _holdRolls = true; }

        /// <summary>True when a binding's "Only when" condition could not be evaluated yet (it reads a variable not filled).</summary>
        public bool Waiting { get; private set; }

        /// <summary>
        /// A condition's truth (numbers count as an <c>if</c> condition does; text is the evaluator's <c>if</c> error), or null when it reads a
        /// variable that cannot be filled yet. With <paramref name="rolls"/> false any roll throws <see cref="RollsForbidden"/>.
        /// </summary>
        public bool? Condition(AstExpr cond, bool rolls = true)
        {
            var test = new Call("if", new AstExpr[] { cond, new BoolLit(true, cond.Start, 0), new BoolLit(false, cond.Start, 0) }, cond.Start, cond.Length);
            var before = _noRolls;
            _noRolls = before || !rolls;
            try { return Evaluator.Evaluate(test, new RunContext(this)).Bool; }
            catch (Exception ex) when (ex is Missing or Waits or EvalError { HiddenData: true }) { return null; }   // hidden in this view: undecided
            finally { _noRolls = before; }
        }

        /// <summary>The value, or throws <see cref="Missing"/> when it cannot be filled (or waits on another variable), or
        /// <see cref="EvalError"/> when its "Only when" condition is false.</summary>
        public Value ValueOf(string name)
        {
            var st = Resolve(name);
            if (st.Skipped) throw new EvalError($"{name} is not used here: its \"Only when\" condition is false");
            if (st.Waiting) throw new Waits();
            return st.Value ?? throw new Missing(st.MissingReason ?? "Missing value");
        }

        /// <summary>The dice a Roll binding drew in this run. Reading it resolves the binding first, so a skipped, waiting or missing
        /// binding behaves as for any read.</summary>
        public IReadOnlyList<int> DiceOf(string name)
        {
            var value = ValueOf(name);
            if (formula.Bindings.FirstOrDefault(x => x.Variable == name) is not { Kind: BindingKind.Roll } rb)
                throw new EvalError($"count() needs a roll binding, not {name}");
            if (_byKey.TryGetValue(name, out var dice)) return dice;
            // A single typed number is not recorded; for a one-die roll it is that die.
            if (DiceCount(rb) == 1 && value.Kind == ValueKind.Number && value.Number == Math.Floor(value.Number) && value.Number >= 1 && value.Number <= (rb.DiceSides ?? 6))
                return new[] { (int)value.Number };
            throw new EvalError($"count() needs each die of {name}: type them separated by spaces");
        }

        public VariableState Resolve(string name)
        {
            if (_done.TryGetValue(name, out var done)) return done;
            if (!_active.Add(name)) throw new EvalError($"Variable {name} depends on itself");
            VariableState st;
            try
            {
                var b = formula.Bindings.FirstOrDefault(x => x.Variable == name);
                var auto = b is { Kind: not (BindingKind.Input or BindingKind.Piece) };
                try
                {
                    var use = b == null || string.IsNullOrWhiteSpace(b.When) ? true : Condition(Parse(formula, b.When));
                    st = use == false ? new(name, null, auto, false, null, Skipped: true)
                        : use == null ? throw new Waits()
                        : b == null ? new(name, null, false, false, "No binding for " + name) : Bind(b);
                }
                catch (Missing m) { st = new(name, null, auto, false, m.Message); }
                catch (Exception ex) when (ex is Waits || (ex is RollsForbidden && _holdRolls))
                {
                    st = new(name, null, auto, false, null, Waiting: true);   // not missing, not skipped
                    Waiting = true;
                }
            }
            finally { _active.Remove(name); }
            _done[name] = st;
            return st;
        }

        private static bool Numeric(VariableBinding b) =>
            b.Kind is BindingKind.Unit or BindingKind.Global or BindingKind.Holder or BindingKind.Roll or BindingKind.Moved or BindingKind.Piece
            || (b.Kind == BindingKind.Input && (b.Choices is { Count: > 0 } ? b.Choices.All(c => TryNum(c, out _)) : b.Min != null || b.Max != null));

        private static bool TryNum(string s, out double d) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);

        private VariableState Bind(VariableBinding b)
        {
            var name = b.Variable;
            var auto = b.Kind is not (BindingKind.Input or BindingKind.Piece);
            if (r.Overrides.TryGetValue(name, out var ov) && ov != null)
            {
                if (b.Kind == BindingKind.Roll) return TypedRoll(b, ov);
                if (Numeric(b))
                    return TryNum(ov, out var n)
                        ? new(name, Value.Of(n), auto, true, null)
                        : new(name, null, auto, true, "Not a number");
                return new(name, TryNum(ov, out var n2) && b.Kind != BindingKind.Location ? Value.Of(n2) : Value.Of(ov), auto, true, null);
            }
            switch (b.Kind)
            {
                case BindingKind.Unit:
                {
                    if (piece == null) throw new Missing("Needs a unit");
                    var prop = Prop(b);
                    return new(name, Value.Of(Read(LedgerKey.Piece(piece.Id), prop)), true, false, null,
                        $"{prop.Name} of {PieceInfo.Name(piece)}");
                }
                case BindingKind.Global:
                {
                    var prop = Prop(b);
                    EnsureKept(owner._pack, HolderDef.GlobalId, prop);
                    return new(name, Value.Of(Read(LedgerKey.Holder(HolderDef.GlobalId), prop)), true, false, null,
                        $"{prop.Name} (global)");
                }
                case BindingKind.Holder:
                {
                    var hid = b.HolderId;
                    if (hid == null || (hid != HolderDef.GlobalId && owner._pack.ById<HolderDef>(hid) == null)) throw new Missing("Unknown holder");
                    var prop = Prop(b);
                    EnsureKept(owner._pack, hid, prop);
                    var hname = hid == HolderDef.GlobalId ? "global" : owner._pack.ById<HolderDef>(hid)!.Name;
                    return new(name, Value.Of(Read(LedgerKey.Holder(hid), prop)), true, false, null,
                        $"{prop.Name} of {hname}");
                }
                case BindingKind.Table:
                {
                    var table = (b.TableId == null ? null : owner._pack.ById<TableDef>(b.TableId))
                        ?? throw new EvalError($"The table for {name} was deleted");
                    var ctx = new RunContext(this);
                    var row = Evaluator.Evaluate(Parse(formula, b.RowExpr ?? ""), ctx);
                    Value? col = string.IsNullOrWhiteSpace(b.ColExpr) ? null : Evaluator.Evaluate(Parse(formula, b.ColExpr), ctx);
                    var (ri, ci) = TableLookup.Locate(table, row, col);
                    var src = $"{table.Name}, row {TableLookup.Normalise(table.RowKeys[ri])}"
                        + (table.Kind == TableKind.TwoD ? $", column {TableLookup.Normalise(table.ColumnKeys[ci])}" : "");
                    (Value Value, PropertyDef? By, Value Printed) read;
                    try { read = CellOverrides.Read(table, ri, ci, piece, s, owner._pack, owner._model, Filters, Sides); }
                    catch (EvalError ex) when (ex.MissingValue || ex.HiddenData) { throw new Missing(ex.Message); }
                    if (read.By != null) src += $": {read.By.Name} of {PieceInfo.Name(piece!)} (printed {read.Printed.Display()})";
                    return new(name, read.Value, true, false, null, src, new TableCell(table.Id, ri, ci));
                }
                case BindingKind.Location:
                {
                    if (piece == null) throw new Missing("Needs a unit");
                    var loc = PieceInfo.Location(piece, owner._model) ?? throw new Missing("No grid location");
                    return new(name, Value.Of(loc), true, false, null, $"location of {PieceInfo.Name(piece)}");
                }
                case BindingKind.Roll:
                {
                    var res = RollKey(name, DiceCount(b), b.DiceSides ?? 6);
                    return new(name, Value.Of(res.Sum()), true, false, null);
                }
                case BindingKind.Moved:
                    if (r.Moved is not { } m || !double.IsFinite(m)) throw new Missing("Only known when a unit is moved");
                    return new(name, Value.Of(m), true, false, null, "distance moved");
                case BindingKind.Piece:
                    return PickedPiece(b);
                default:
                    return Input(b);
            }
        }

        /// <summary>
        /// A Roll binding typed in (physical dice). Several numbers (separated by spaces and/or commas) are the dice themselves: exactly the dice
        /// count of them, each 1 to the sides, else missing. A single number is the sum as before (a one-die roll's count() reads it as the die, see DiceOf).
        /// Typed dice are logged and seen by count() like rolled ones; a typed single number is neither logged nor pinned.
        /// </summary>
        private VariableState TypedRoll(VariableBinding b, string typed)
        {
            var name = b.Variable;
            var parts = typed.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 && !TryNum(typed, out _)) return new(name, null, true, true, "Not a number");
            int n = DiceCount(b), sides = b.DiceSides ?? 6;
            var dice = new List<int>();
            foreach (var p in parts)
            {
                if (!TryNum(p, out var d) || d != Math.Floor(d) || d < 1 || d > sides) break;
                dice.Add((int)d);
            }
            if (parts.Length < 2)
            {
                TryNum(typed, out var sum);
                return new(name, Value.Of(sum), true, true, null);
            }
            if (dice.Count != parts.Length || dice.Count != n) return new(name, null, true, true, $"{name} needs {n} dice from 1 to {sides}");
            Record(name, n, sides, dice);
            return new(name, Value.Of(dice.Sum()), true, true, null);
        }

        /// <summary>A Roll binding's number of dice: the fixed count, or its dice count expression evaluated in this run (a whole number
        /// 1-100, the limits and wording of dice()).</summary>
        private int DiceCount(VariableBinding b)
        {
            if (string.IsNullOrWhiteSpace(b.DiceCountExpr)) return b.DiceCount ?? 1;
            var v = Evaluator.Evaluate(Parse(formula, b.DiceCountExpr!), new RunContext(this));
            if (v.Kind != ValueKind.Number || v.Number != Math.Floor(v.Number) || v.Number < 1 || v.Number > 100)
                throw new EvalError($"'{b.Variable}' dice count must be a whole number from 1 to 100");
            return (int)v.Number;
        }

        /// <summary>
        /// A Unit, Global or Holder binding's value (<see cref="PropertyValues"/>); a computed value that is missing, or that reads data this
        /// player view cannot see (<see cref="EvalError.HiddenData"/>), leaves the binding missing (the host fills it in).
        /// </summary>
        private double Read(string key, PropertyDef prop)
        {
            try { return PropertyValues.GetOrDefault(s, owner._pack, owner._model, key, prop, PropertyFacts.IsComputed(prop) ? Filters : null); }
            catch (EvalError ex) when (ex.MissingValue || ex.HiddenData) { throw new Missing(ex.Message); }
        }

        /// <summary>
        /// A Piece binding: the typed label (trimmed, case-insensitive) resolved among the candidates of <see cref="RunRequest.PickSides"/>
        /// (<see cref="PieceChoices"/>), then that piece's property as a Unit binding reads it. Never filled automatically. On a player view
        /// state the placeholder of a masked piece, and a non-Public value of a side the view was not made for, are hidden (missing), not Default.
        /// </summary>
        private VariableState PickedPiece(VariableBinding b)
        {
            var name = b.Variable;
            var prop = Prop(b);
            var (picked, shown) = Pick(b);
            if (s.IsPlayerView && (PlayerView.IsPlaceholder(picked)
                    || (!PropertyFacts.IsPublic(owner._pack, prop) && Sides.Value(picked) is { } side && s.ViewSides?.Contains(side) != true)))
                throw new Missing(HiddenText);
            return new(name, Value.Of(Read(LedgerKey.Piece(picked.Id), prop)), false, false, null, $"{prop.Name} of {shown}");
        }

        /// <summary>
        /// The piece a Piece binding's typed label picks among the candidates of <see cref="RunRequest.PickSides"/> (the run piece's hex first;
        /// "same hex" only that hex), with its name as the requester sees it; throws <see cref="Missing"/> when it cannot be picked yet.
        /// </summary>
        public (PieceInstance Piece, string Name) Pick(VariableBinding b)
        {
            if (b.SameHex && piece == null) throw new Missing("Needs a unit");
            var list = PieceChoices.Named(s, owner._pack, owner._model, b, r.PickSides, piece?.Id);
            if (list.Count == 0)
                throw new Missing(b.SameHex ? PieceChoices.NoPieceInHex
                    : string.IsNullOrWhiteSpace(b.PieceFilter) ? PieceChoices.NoPiece : $"No piece matches \"{b.PieceFilter.Trim()}\"");
            if (!r.Typed.TryGetValue(b.Variable, out var raw) || string.IsNullOrWhiteSpace(raw)) throw new Missing("Waiting for input");
            var typed = raw.Trim();
            var hit = list.FirstOrDefault(c => string.Equals(c.Label, typed, StringComparison.OrdinalIgnoreCase));
            if (hit.PieceId == null || !s.Pieces.TryGetValue(hit.PieceId, out var picked) || picked == null) throw new Missing(PieceChoices.Unknown);
            return (picked, hit.Name.Length > 0 ? hit.Name : hit.Label);
        }

        private PropertyDef Prop(VariableBinding b) =>
            (b.Property == null ? null : owner._pack.ById<PropertyDef>(b.Property)) ?? throw new Missing("Unknown property");

        private VariableState Input(VariableBinding b)
        {
            var name = b.Variable;
            string? source = null;
            if (!r.Typed.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                if (!r.UseAutoValues || b.AlwaysAsk || string.IsNullOrWhiteSpace(b.AutoValue)) return new(name, null, false, false, "Waiting for input");
                try { raw = Evaluator.Evaluate(Parse(formula, b.AutoValue), new RunContext(this)).Display(); }
                catch (Exception ex) when (ex is EvalError or ParseError) { return new(name, null, false, false, ex.Message); }
                source = "default";
            }
            raw = raw.Trim();
            if (b.Choices is { Count: > 0 })
            {
                var hit = b.Choices.FirstOrDefault(c => string.Equals(c.Trim(), raw, StringComparison.OrdinalIgnoreCase));
                if (hit == null) return new(name, null, false, false, "Choose one of " + string.Join(", ", b.Choices));
                return b.Choices.All(c => TryNum(c, out _)) && TryNum(hit, out var cn)
                    ? new(name, Value.Of(cn), false, false, null, source)
                    : new(name, Value.Of(hit.Trim()), false, false, null, source);
            }
            if (b.Min == null && b.Max == null)
                return TryNum(raw, out var free) ? new(name, Value.Of(free), false, false, null, source)
                    : new(name, Value.Of(raw), false, false, null, source);   // unbounded, no choices: text allowed
            if (!TryNum(raw, out var v)) return new(name, null, false, false, "Not a number");
            if ((b.Min != null && v < b.Min) || (b.Max != null && v > b.Max))
            {
                var msg = b.Min != null && b.Max != null ? $"Must be {Value.Of(b.Min.Value).Display()}–{Value.Of(b.Max.Value).Display()}"
                    : b.Min != null ? $"Must be at least {Value.Of(b.Min.Value).Display()}"
                    : $"Must be at most {Value.Of(b.Max!.Value).Display()}";
                return new(name, null, false, false, msg);
            }
            return new(name, Value.Of(v), false, false, null, source);
        }
    }

    private sealed class RunContext(RunScope run) : IEvalContext
    {
        public Value GetVariable(string name) => run.ValueOf(name);
        public Value Lookup(string tableName, Value row, Value? col) =>
            PieceFilter.LookupTable(run.Pack, tableName, row, col, run.Piece, run.State, run.Model, run.Filters, run.Sides);
        public Value LookupRange(string table, Value row, Value value) => PieceFilter.LookupRangeTable(run.Pack, table, row, value);
        public IReadOnlyList<int> DiceOf(string variable) => run.DiceOf(variable);
        public IReadOnlyList<int> RollDice(int n, int sides) => run.Roll(n, sides);
        public bool Has(string property) => PieceFilter.HasProperty(run.State, run.Pack, run.Piece, property, run.Model);
        public Value PieceName() => PieceFilter.NameValue(run.Piece);
        public Value Marker(string key) => PieceFilter.MarkerValue(run.Piece, key);
        public double Layer(string name) => PieceFilter.LayerValue(run.Piece, run.State, name);
        public double Turn() => run.State.Position?.Turn ?? run.State.Turn.Turn;
        public double Stage() => run.State.Position?.Repeat ?? 1;
        public Value Side() => PieceFilter.SideValue(run.Piece, run.Pack, run.Sides);
        public Value PhaseSide() => PieceFilter.PhaseSideValue(run.Pack, Sequencer.Current(run.Pack, run.State));
        public Value MapName() => PieceFilter.MapValue(run.Piece);
        public Value HolderValue(string holder, string property)
        {
            // A holder that does not keep the property fails the run (filters and alerts read it as before).
            if (run.Pack.HolderByName(holder) is { } h && run.Pack.PropertyByName(property) is { Scope: not PropertyScope.Pieces } p)
                EnsureKept(run.Pack, h.Id, p);
            return PieceFilter.HolderRead(run.State, run.Pack, holder, property, run.Model, run.Filters);
        }
        public Value Aggregate(string fn, string? property, string? filter) =>
            PieceAggregates.Evaluate(fn, property, filter, run.Piece, run.Filters);
        /// <summary>The applied runs of a formula on this run's unit (none: its runs without a unit); the run itself is not counted yet.</summary>
        public double Runs(string formulaId, string period) => RunIndex.Count(run.State, run.Pack, formulaId, run.Piece?.Id, period, run.Sides);
    }
}
