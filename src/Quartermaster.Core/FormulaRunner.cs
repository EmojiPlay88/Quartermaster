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
    /// <summary>The value key a keyed property's write goes to (B6: the output's key, normalised); null for an unkeyed property.</summary>
    public string? ValueKey { get; init; }
}

/// <summary>
/// B7 #47: what a Reveal output shows once its run is applied. Never shown before Apply: no box, verdict or working carries it; the commands
/// log it (<see cref="FormulaRunner.Commands"/>), drawing the window then.
/// </summary>
/// <param name="Subject">The run piece's name (Unit), the picked piece's name as the requester sees it (Piece; a masked pick reads
/// "Unknown piece"), the holder's name (Holder) or "Global" (Global).</param>
/// <param name="PropertyId">The revealed property.</param>
/// <param name="Value">The value read at the run, after the run's earlier writes to the same ledger key and property; null when unknown (a
/// player view lacking it, or a computed value that cannot be worked out), so no line is logged.</param>
/// <param name="Margin">The width of the revealed range, 0 to 1000; 0 shows the exact value.</param>
public sealed record RevealPreview(string Subject, string PropertyId, double? Value, int Margin);

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

    /// <summary>The keys this run took from <see cref="RunRequest.SharedRolls"/> (B4), in the order read: in <see cref="RollsByKey"/> but not in
    /// <see cref="Rolls"/>, so the run logs no dice for them (the group's first run did). Empty when none.</summary>
    public IReadOnlyList<string> SharedRollKeys { get; init; } = Array.Empty<string>();

    /// <summary>The sides of every key in <see cref="RollsByKey"/> (rolled, pinned, typed or shared), so a group's dice can be matched on
    /// sides as well as count (<see cref="RollGroups.GroupKey"/>). Empty for a trace not made by the runner.</summary>
    public IReadOnlyDictionary<string, int> RollSides { get; init; } = new Dictionary<string, int>();

    /// <summary>B7 #47: the run's Reveal outputs in output order. Their values are logged only by <see cref="FormulaRunner.Commands"/>, never
    /// previewed (a Reveal adds no <see cref="OutputPreview"/>; its working part is "reveal {Prop}"). Empty for a run with none.</summary>
    public IReadOnlyList<RevealPreview> Reveals { get; init; } = Array.Empty<RevealPreview>();
}

/// <param name="Dice">Rolls a key's dice when no matching pin is in <paramref name="FixedRolls"/> (key, count, sides); null = the runner's
/// roller. The online host uses it so every die of a guest's run is pinned per dice configuration.</param>
/// <param name="PickSides">The chooser's sides: a Piece binding's typed label is resolved among the candidates of their view
/// (<see cref="PieceChoices"/>) and an Input's choices filter is judged on that view (B7), so the accepted values are exactly the chooser's
/// listed ones. A holder picker's typed label (B9 #13b, <see cref="HolderChoices"/>) is resolved among the listed holders whose current
/// owner is no side or one of these sides (no view is built for it). The requesting guest's held set on the host, the viewer's in hot-seat;
/// null = the run's own state (solo, hook runs, the host's own full-state runs, a run on a player view state, whose own sides choose a
/// holder). The view is built at most once per run, and only when it is needed.</param>
public sealed record RunRequest(string FormulaId, string? PieceId, IReadOnlyDictionary<string, string> Typed,
    IReadOnlyDictionary<string, string> Overrides, IReadOnlyDictionary<string, IReadOnlyList<int>> FixedRolls, double? Moved = null, bool UseAutoValues = false,
    Func<string, int, int, IReadOnlyList<int>>? Dice = null, IReadOnlySet<string>? PickSides = null)
{
    /// <summary>A group's dice already drawn (group key → dice, <see cref="RollGroups.GroupKey"/>: an inline key carries its sides; B4
    /// <see cref="FormulaDef.GroupRoll"/>): a key whose sides and dice count match and whose dice are within the sides is used before pins and the roller, and is not logged by this run (<see cref="FormulaTrace.SharedRollKeys"/>).
    /// A typed roll still wins. Null = none.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>>? SharedRolls { get; init; }

    /// <summary>B7: Input variables that take their auto value when nothing is typed, even without <see cref="UseAutoValues"/> (an Input
    /// whose value is hidden from the viewer: the viewer cannot type it, so the engine reads it). Null = none.</summary>
    public IReadOnlySet<string>? AutoInputs { get; init; }

    /// <summary>B7: a run for what a masked viewer is shown, never applied: a false precondition does not refuse it (a precondition over
    /// data hidden from the viewer is the host's to decide at Apply).</summary>
    public bool IgnorePrecondition { get; init; }

    /// <summary>B7: variables resolved as if their "Only when" held (host guards over hidden data: their typed value is validated the same
    /// way whatever the guard says). For a shown run only, never applied. Null = none.</summary>
    public IReadOnlySet<string>? ForceWhen { get; init; }

    /// <summary>B7: the side answering this run (an ask run's or a sealed run's <see cref="PendingRun.AskSideId"/>): <c>askside()</c> reads
    /// its name (the id when the name is blank). Null = no side answers (<c>askside()</c> is <c>""</c>).</summary>
    public string? AskSideId { get; init; }
}

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
        var shared = new List<string>();
        var sides = new Dictionary<string, int>(StringComparer.Ordinal);
        FormulaTrace t;
        try
        {
            t = RunCore(s, r, formula, produced, shared, sides);
            if (r.PieceId != null && s.Pieces.ContainsKey(r.PieceId)) t = t with { PieceId = r.PieceId };
        }
        catch (Exception ex)
        {
            t = Fail(formula, ex is EvalError or ParseError ? ex.Message : "Could not run formula: " + ex.Message, produced);
        }
        if (sides.Count > 0) t = t with { RollSides = new Dictionary<string, int>(sides, StringComparer.Ordinal) };
        return shared.Count == 0 ? t : t with { SharedRollKeys = shared.ToArray() };
    }

    private static FormulaTrace Fail(FormulaDef f, string error, IReadOnlyDictionary<string, IReadOnlyList<int>>? produced = null) =>
        new(f, Array.Empty<VariableState>(), null, "", Array.Empty<OutputPreview>(), error, Array.Empty<DiceEntry>(),
            produced == null ? new Dictionary<string, IReadOnlyList<int>>() : new Dictionary<string, IReadOnlyList<int>>(produced));

    private FormulaTrace RunCore(GameState s, RunRequest r, FormulaDef formula, Dictionary<string, IReadOnlyList<int>> produced, List<string> shared,
        Dictionary<string, int> sides)
    {
        AstExpr expr;
        try { expr = Parse(formula, formula.Text ?? ""); }
        catch (ParseError pe) { return Fail(formula, pe.Message, produced); }

        var piece = r.PieceId != null && s.Pieces.TryGetValue(r.PieceId, out var pi) ? pi : null;
        var run = new RunScope(this, s, r, formula, piece, produced, shared, sides);

        // A Piece-target output names the Piece binding whose pick it acts on; without one it can never run.
        var pieceVars = new List<string>();
        foreach (var o in formula.Outputs)
        {
            if (o.Target != OutputTarget.Piece) continue;
            if (string.IsNullOrWhiteSpace(o.PieceVariable) || formula.Bindings.FirstOrDefault(x => x.Variable == o.PieceVariable) is not { Kind: BindingKind.Piece })
                throw new EvalError("An output refers to a deleted Piece variable");
            if (!pieceVars.Contains(o.PieceVariable)) pieceVars.Add(o.PieceVariable);
        }
        // B9 #13b: a Holder output may name the holder picker whose chosen holder it writes (or reveals), in place of a fixed holder.
        var holderVars = new List<string>();
        foreach (var o in formula.Outputs)
        {
            if (string.IsNullOrWhiteSpace(o.HolderVariable)) continue;
            if (o.Target != OutputTarget.Holder || !HolderChoices.IsPicker(formula.Bindings.FirstOrDefault(x => x.Variable == o.HolderVariable)))
                throw new EvalError("An output refers to a deleted Holder variable");
            if (!holderVars.Contains(o.HolderVariable)) holderVars.Add(o.HolderVariable);
        }

        AstExpr? pre = string.IsNullOrWhiteSpace(formula.Precondition) ? null : Parse(formula, formula.Precondition);

        // Variables of the text, then of the precondition, then of the outputs' value and pick-by expressions, then any used only inside
        // Table binding row/column expressions, "Only when" conditions or Roll binding dice count expressions.
        var names = new List<string>(Parser.Variables(expr));
        if (pre != null) foreach (var v in Parser.Variables(pre)) if (!names.Contains(v)) names.Add(v);
        foreach (var src in FormulaVariables.OutputSources(formula))
            foreach (var v in Parser.Variables(Parse(formula, src))) if (!names.Contains(v)) names.Add(v);
        foreach (var v in pieceVars) if (!names.Contains(v)) names.Add(v);   // an output's target is asked even when nothing reads its value
        foreach (var v in holderVars) if (!names.Contains(v)) names.Add(v);
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
            if (ok == false && !r.IgnorePrecondition)
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
            if (states.First(x => x.Variable == v).Skipped) continue;   // "Only when" false: not asked; the outputs naming it do nothing
            try { targets[v] = run.Pick(formula.Bindings.First(x => x.Variable == v)); }
            catch (Missing m) { return Unresolved(v, m.Message); }
            if (s.IsPlayerView && PlayerView.IsPlaceholder(targets[v].Piece)) return Unresolved(v, HiddenText);
        }
        // The holders the Holder-variable outputs act on (the picker's own pick, shared with its binding: one list per run).
        var holderTargets = new Dictionary<string, HolderChoice>(StringComparer.Ordinal);
        foreach (var v in holderVars)
        {
            if (states.First(x => x.Variable == v).Skipped) continue;   // "Only when" false: the outputs naming it do nothing
            try { holderTargets[v] = run.PickHolder(formula.Bindings.First(x => x.Variable == v)); }
            catch (Missing m) { return Unresolved(v, m.Message); }
        }

        var result = Evaluator.Evaluate(expr, new RunContext(run));
        var working = WorkingLine.Render(expr, formula.Text ?? "", n => run.Cached(n)?.Value, run.Label) + " = " + result.Display();

        var outputs = new List<OutputPreview>();
        var parts = new List<string>();
        var calc = working;
        string OutputError(int oi, string detail) => $"Formula \"{formula.Name}\": output {oi + 1}{detail}";

        // An output acting on a Piece binding skipped by its "Only when" condition does nothing (no write, no effect, no part); so do the
        // Placed outputs of a Place skipped that way.
        var placeSkipped = false;
        bool SkippedTarget(FormulaOutput o) => (o.Target == OutputTarget.Piece && o.PieceVariable != null && !targets.ContainsKey(o.PieceVariable))
            || (o.Target == OutputTarget.Holder && !string.IsNullOrWhiteSpace(o.HolderVariable) && !holderTargets.ContainsKey(o.HolderVariable))
            || (o.Target == OutputTarget.Placed && placeSkipped);

        // The placed piece first, so a Placed output listed before its Place still acts on it. At most one Place per formula.
        Placed? placed = null;
        PieceEffect? place = null;
        for (var oi = 0; oi < formula.Outputs.Count; oi++)
        {
            var o = formula.Outputs[oi];
            if (o.Action != OutputAction.Place) continue;
            if (SkippedTarget(o)) { placeSkipped = true; continue; }
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
        var reveals = new List<RevealPreview>();
        var pending = new Dictionary<(string, int), string>();   // trait states this run already set (a second change reads the first)
        // Every output is computed (value and property) before the trace is returned, so a failing output writes nothing.
        for (var oi = 0; oi < formula.Outputs.Count; oi++)
        {
            var o = formula.Outputs[oi];
            if (SkippedTarget(o)) continue;
            switch (o.Action)
            {
                case OutputAction.Write: break;
                case OutputAction.Place: continue;
                case OutputAction.Reveal:
                {
                    // B7 #47: read now, in output order (after this run's earlier writes); shown only when the commands are built.
                    var (rv, rp) = RevealOf(s, o, oi, run, piece, targets, holderTargets, outputs, OutputError);
                    reveals.Add(rv);
                    parts.Add($"reveal {rp.Name}");
                    continue;
                }
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
                    // the one resolver of a To map, as the validator's (B6): trimmed, id then name
                    var map = MapRefs.Find(_model.Maps, o.ToMapId) ?? throw new EvalError($"Map \"{o.ToMapId.Trim()}\" was not found in the module");
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
                        // A piece the requester sees masked (its name differs from the real one) shows the level as its number: a level's
                        // name would tell what the piece is.
                        var maskedToViewer = !string.Equals(tg.Shown, tg.Real, StringComparison.Ordinal);
                        shown = $"{layer} of {tg.Shown} = {(maskedToViewer ? n.ToString(CultureInfo.InvariantCulture) : level)}";
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
            string? chosen = null;   // a Holder-variable output: the chosen holder's name
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
                    if (s.IsPlayerView && HiddenValues.PieceValueHidden(_pack, prop, tp.Piece, run.Sides, HiddenValues.HeldBy(s)))
                        return Unresolved(o.PieceVariable!, HiddenText);
                    target = (RealName(tp.Piece), tp.Name, run.Sides.Value(tp.Piece));
                    key = LedgerKey.Piece(tp.Piece.Id);
                    break;
                }
                case OutputTarget.Holder when !string.IsNullOrWhiteSpace(o.HolderVariable):
                {
                    // B9 #13b: the holder the picker chose (always the chooser's or no side's, so never hidden from it)
                    var hid = holderTargets[o.HolderVariable!].HolderId;
                    EnsureKept(_pack, hid, prop);
                    key = LedgerKey.Holder(hid);
                    chosen = HolderText(_pack, hid);
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
            // A keyed property (B6) writes one key: the output's key, evaluated after its value and normalised. Unkeyed and keyed never mix.
            string? valueKey = null;
            if (KeyedFacts.IsKeyed(prop))
            {
                if (string.IsNullOrWhiteSpace(o.Key)) throw new EvalError(OutputError(oi, $" writes keyed property \"{prop.Name}\" but has no key"));
                if (KeyedFacts.IsPlace(prop) && key != LedgerKey.Holder(HolderDef.GlobalId))
                    throw new EvalError(OutputError(oi, $" writes place property \"{prop.Name}\" to a holder other than Global"));
                var keyValue = Evaluator.Evaluate(Parse(formula, o.Key), new RunContext(run));
                try { valueKey = KeyedValues.NormaliseKey(_pack, _model, prop, keyValue); }
                catch (EvalError ex) { throw new EvalError($"The key for {prop.Name}: {ex.Message}"); }
                // a player view lacks a Private place's values: a change of one is not previewed (the host decides), as a hidden binding waits
                if (s.IsPlayerView && o.Op != OutputOp.Set && KeyedFacts.IsPlace(prop) && !PropertyFacts.IsPublic(_pack, prop))
                    return new(formula, states, null, "", Array.Empty<OutputPreview>(), null, rolls, run.RollsByKey);
            }
            else if (!string.IsNullOrWhiteSpace(o.Key))
                throw new EvalError(OutputError(oi, $" has a key but writes property \"{prop.Name}\", which is not keyed"));
            var x = value.Number;
            var was = before ?? (valueKey != null ? KeyedValues.GetOrDefault(s, key, prop, valueKey) : Ledger.GetOrDefault(s, key, prop));
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
            var shownName = valueKey == null ? prop.Name : $"{prop.Name}[{valueKey}]";   // a keyed write names its key: "Ford depth[0412] = 3"
            // a chosen holder is named in its own part, the acting side's line ("Stores of Ford Dump 10 - 3 = 7")
            var part = chosen == null ? Part(shownName) : Part($"{shownName} of {chosen}");
            outputs.Add(new OutputPreview(key, prop.Id, prop.Name, was, after)
            {
                Target = o.Target,
                Part = part,
                SideId = target?.Side,
                Subject = target?.Shown ?? chosen,
                TargetName = target?.Real,
                ValueKey = valueKey,
            });
            // the dialog's working names the target as its binding does ("Fuel of Blue Barge 4 + 2 = 6")
            parts.Add(target is { } named ? Part($"{shownName} of {named.Shown}") : part);
        }
        // Effects in command order: the Place, the layer and mask changes (output order), then the removals (output order).
        var effects = new List<PieceEffect>();
        if (place != null) effects.Add(place);
        effects.AddRange(traitEffects);
        effects.AddRange(removals);
        parts.AddRange(effects.Select(e => e.Text));
        if (parts.Count > 0) working += " → " + string.Join("; ", parts);
        return new(formula, states, result, working, outputs, null, rolls, run.RollsByKey) { Calc = calc, Effects = effects, Reveals = reveals };
    }

    /// <summary>
    /// A Reveal output's subject, value and margin (B7 #47), with its property. The value is the property's as a binding of the target reads
    /// it (stored, else Default, a computed property evaluated), or the After of this run's last earlier write to the same ledger key and
    /// property. On a player view a value the view may not see (<see cref="HiddenValues"/>) is unknown (null), never an error: the host reads
    /// the full state. The margin (blank = 0) must be a whole number from 0 to 1000, else the run fails.
    /// </summary>
    private (RevealPreview Reveal, PropertyDef Property) RevealOf(GameState s, FormulaOutput o, int oi, RunScope run, PieceInstance? piece,
        Dictionary<string, (PieceInstance Piece, string Name)> targets, Dictionary<string, HolderChoice> holderTargets, List<OutputPreview> earlier,
        Func<int, string, string> outputError)
    {
        var prop = OutputProperty(o, run);
        if (KeyedFacts.IsKeyed(prop)) throw new EvalError(outputError(oi, $" reveals keyed property \"{prop.Name}\"; a reveal shows one value"));
        var margin = 0;
        if (!string.IsNullOrWhiteSpace(o.Margin))
        {
            var m = Evaluator.Evaluate(Parse(run.Formula, o.Margin), new RunContext(run));
            if (m.Kind != ValueKind.Number || m.Number != Math.Floor(m.Number) || m.Number < 0 || m.Number > 1000)
                throw new EvalError($"The margin for {prop.Name} must be a whole number from 0 to 1000");
            margin = (int)m.Number;
        }
        var view = s.IsPlayerView;
        var held = view ? HiddenValues.HeldBy(s) : null;
        string key, subject;
        bool hidden;
        switch (o.Target)
        {
            case OutputTarget.Unit:
                key = LedgerKey.Piece(piece!.Id);
                subject = RealName(piece);
                hidden = view && (PlayerView.IsPlaceholder(piece) || HiddenValues.PieceValueHidden(_pack, prop, piece, run.Sides, held!));
                break;
            case OutputTarget.Piece:
            {
                var tp = targets[o.PieceVariable!];
                key = LedgerKey.Piece(tp.Piece.Id);
                subject = tp.Name;
                hidden = view && (PlayerView.IsPlaceholder(tp.Piece) || HiddenValues.PieceValueHidden(_pack, prop, tp.Piece, run.Sides, held!));
                break;
            }
            case OutputTarget.Global:
                EnsureKept(_pack, HolderDef.GlobalId, prop);
                key = LedgerKey.Holder(HolderDef.GlobalId);
                subject = "Global";
                hidden = view && HiddenValues.HolderValueHidden(s, _pack, HolderDef.GlobalId, prop, held!);
                break;
            case OutputTarget.Holder when !string.IsNullOrWhiteSpace(o.HolderVariable):
            {
                // B9 #13b: the holder the picker chose; hidden as any holder's value on a view (its current owner, B9 #13a)
                var hid = holderTargets[o.HolderVariable!].HolderId;
                EnsureKept(_pack, hid, prop);
                key = LedgerKey.Holder(hid);
                subject = HolderText(_pack, hid);
                hidden = view && HiddenValues.HolderValueHidden(s, _pack, hid, prop, held!);
                break;
            }
            case OutputTarget.Holder:
                if (o.HolderId == null || (o.HolderId != HolderDef.GlobalId && _pack.ById<HolderDef>(o.HolderId) == null))
                    throw new EvalError("An output refers to a deleted holder");
                EnsureKept(_pack, o.HolderId, prop);
                key = LedgerKey.Holder(o.HolderId);
                subject = HolderText(_pack, o.HolderId);
                hidden = view && HiddenValues.HolderValueHidden(s, _pack, o.HolderId, prop, held!);
                break;
            default:
                throw new EvalError(outputError(oi, $": {o.Action} cannot target {o.Target}"));
        }
        double? value = null;
        if (!hidden)
            value = earlier.LastOrDefault(w => w.Key == key && w.PropertyId == prop.Id && w.ValueKey == null) is { } write ? write.After : run.ReadKnown(key, prop);
        return (new RevealPreview(subject, prop.Id, value, margin), prop);
    }

    /// <summary>
    /// The log line of a reveal, or null for an unknown value. Margin 0: "{subject}: {Prop} {value}" (labels as the Ledger shows them).
    /// Otherwise a window of margin + 1 values holding the value, drawn with the runner's roller (one die of margin + 1 faces, never logged):
    /// k = roll − 1, lo = value − k, hi = lo + margin; a value of 0 or more never gets a window starting below 0 (then 0 to margin).
    /// </summary>
    private string? RevealText(RevealPreview r)
    {
        if (r.Value is not { } v) return null;
        var prop = _pack.ById<PropertyDef>(r.PropertyId);
        var name = prop?.Name ?? r.PropertyId;
        if (r.Margin <= 0) return $"{r.Subject}: {name} {(prop == null ? Value.Of(v).Display() : PropertyLabels.Text(_pack, prop, v))}";
        var die = _roller(1, r.Margin + 1) is { Count: > 0 } d ? d[0] : 1;
        var k = Math.Clamp(die - 1, 0, r.Margin);
        var lo = v - k;
        var hi = lo + r.Margin;
        if (v >= 0 && lo < 0) { lo = 0; hi = r.Margin; }
        return $"{r.Subject}: {name} between {lo.ToString("0.##", CultureInfo.InvariantCulture)} and {hi.ToString("0.##", CultureInfo.InvariantCulture)}";
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
    internal static void EnsureKept(RulesPack pack, string holderId, PropertyDef prop)
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
    /// System line (output order); one System line per reveal with a known value (output order, B7 #47; a window is drawn now with the
    /// runner's roller, no dice line); the working line when no own write carries it; then removals and moves (output order), so every line
    /// before a removal is logged against a piece that still exists. The caller seeds the placed piece right after its AddPiece executes.
    /// </summary>
    public IReadOnlyList<GameCommand> Commands(FormulaTrace t, string? note, string? tag = null, string? subject = null, string? sideId = null)
    {
        var list = new List<GameCommand>();
        if (t.Result == null || t.Error != null) return list;
        foreach (var d in t.Rolls) list.Add(new RollDice(d.Purpose, d.Count, d.Sides, d.Results, PieceId: t.PieceId, SideId: sideId) { FormulaId = t.Formula.Id });
        var own = t.Outputs.Where(o => o.Target is not (OutputTarget.Piece or OutputTarget.Placed)).ToList();
        var onPieces = t.Outputs.Where(o => o.Target == OutputTarget.Piece).ToList();
        var onPlaced = t.Outputs.Where(o => o.Target == OutputTarget.Placed).ToList();
        var effects = t.Effects ?? Array.Empty<PieceEffect>();
        // The acting side's working leaves out the Piece and Placed parts (those go on the targets' own lines, which the target's side reads)
        // and lists the effects by the names the acting player sees.
        var reveals = t.Reveals ?? Array.Empty<RevealPreview>();
        var revealParts = reveals.Select(r => $"reveal {_pack.ById<PropertyDef>(r.PropertyId)?.Name ?? r.PropertyId}");
        var working = t.Calc == null ? t.Working
            : t.Calc + (own.Count + effects.Count + reveals.Count == 0 ? ""
                : " → " + string.Join("; ", own.Select(o => o.Part).Concat(revealParts).Concat(effects.Select(e => e.Text))));
        foreach (var e in effects.Where(e => e.Kind == PieceEffectKind.Place))
            list.Add(new AddPiece(e.PieceId, e.TypeString!, e.StateString ?? "", e.MapId!, e.Pos, e.StackOnto, e.LogText));
        var first = true;
        foreach (var o in own)
        {
            // The first own line records the applied run (runs(), spec #71): exactly one stamped line per run.
            list.Add(new LedgerDelta(o.Key, o.PropertyId, o.Before, o.After, "formula", t.Formula.Id, t.Formula.RuleRefId, note, working, Tag: tag,
                Subject: subject, PieceId: t.PieceId, SideId: sideId) { AppliedRun = first ? t.Formula.Id : null, ValueKey = o.ValueKey });
            first = false;
        }
        // A Piece (or Placed) write is the target's line: keyed on it, stamped with its side, subject its real name, working "{formula}: {part}".
        foreach (var o in onPieces.Concat(onPlaced))
        {
            LedgerKey.TryParse(o.Key, out _, out var targetId);
            list.Add(new LedgerDelta(o.Key, o.PropertyId, o.Before, o.After, "formula", t.Formula.Id, t.Formula.RuleRefId, note,
                $"{t.Formula.Name}: {o.Part}", Tag: tag, Subject: o.TargetName ?? o.Subject, PieceId: targetId, SideId: o.SideId) { ValueKey = o.ValueKey });
        }
        // A layer or mask change: the trait state, then a System line by the piece's real name, stamped with its side.
        foreach (var e in effects.Where(e => e.Kind is PieceEffectKind.SetLayer or PieceEffectKind.SetMask))
        {
            list.Add(new SetTraitState(e.PieceId, e.TraitIndex, e.NewState ?? ""));
            list.Add(new AddLogLine(LogKind.System, e.LogText, e.PieceId, SideId: e.SideId));
        }
        // B7 #47: one public System line per reveal with a known value (no piece, no side: every player who can see the subject keeps it;
        // PlayerView drops a line naming a counter the viewer cannot see). A window is drawn here; its die is never logged.
        foreach (var r in reveals)
            if (RevealText(r) is { } text) list.Add(new AddLogLine(LogKind.System, text));
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
    private sealed class RunScope(FormulaRunner owner, GameState s, RunRequest r, FormulaDef formula, PieceInstance? piece, Dictionary<string, IReadOnlyList<int>> byKey,
        List<string> sharedKeys, Dictionary<string, int> rollSides)
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
        /// <summary>The side answering this run (<see cref="RunRequest.AskSideId"/>); null = none.</summary>
        public string? AskSideId => r.AskSideId;
        /// <summary>The compiled filters of this run's aggregate calls, each filter text compiled once per run (or per pass), sharing <see cref="Sides"/>.</summary>
        public AggregateFilters Filters => _filters ??= _pass?.Filters ?? new(s, owner._pack, owner._model) { SharedSides = Sides };
        private AggregateFilters? _filters;

        /// <summary>
        /// B7: the chooser's view (<see cref="PlayerView.For"/> of this run's state for <see cref="RunRequest.PickSides"/>), on which an
        /// Input's choices filter and a Piece binding's candidates are judged; built on first use, at most once per run, and only for a run
        /// with PickSides on a full state. Null otherwise: the run's own state is the chooser's (solo, a hook run, a run on a player view).
        /// </summary>
        public Chooser? ChooserView
        {
            get
            {
                if (_chooserDone) return _chooser;
                _chooserDone = true;
                if (r.PickSides == null || s.IsPlayerView) return null;
                PieceChoices.ViewBuilds++;
                var view = PlayerView.For(s, owner._pack, owner._model, r.PickSides);
                return _chooser = new Chooser(owner, view);
            }
        }
        private Chooser? _chooser;
        private bool _chooserDone;

        /// <summary>The chooser's view with its own side map and aggregate filters (a filter on the view reads only the view).</summary>
        public sealed class Chooser(FormulaRunner owner, GameState view)
        {
            public GameState State => view;
            public Lazy<Func<PieceInstance, string?>> Sides { get; } = PieceFilter.SideMap(view, owner._pack);
            private AggregateFilters? _filters;
            public AggregateFilters Filters => _filters ??= new(view, owner._pack, owner._model) { SharedSides = Sides };
        }

        private readonly Dictionary<string, IReadOnlyList<int>> _byKey = byKey;
        private int _inlineDice;
        public IReadOnlyDictionary<string, IReadOnlyList<int>> RollsByKey => _byKey;

        /// <summary>Rolls (or reuses the pinned roll for) a key: a Roll binding's variable name, or "dice#i" for the i-th inline dice() call of this run.
        /// A pin whose count or sides do not match is ignored. Inline call order follows evaluation order, so an `if` that changes branch can renumber keys.</summary>
        public IReadOnlyList<int> RollKey(string key, int n, int sides)
        {
            if (_noRolls) throw new RollsForbidden();
            // A group's dice (B4): kept for count(), dice() and the working, but not logged here (the group's first run logged them).
            // Matched on count and sides: an inline key is looked up with its sides (RollGroups.GroupKey), so a branch rolling other dice
            // under the same dice#i rolls its own.
            if (r.SharedRolls != null && r.SharedRolls.TryGetValue(RollGroups.GroupKey(key, sides), out var group) && group != null && group.Count == n
                && group.All(x => x >= 1 && x <= sides))
            {
                _byKey[key] = group;
                rollSides[key] = sides;
                if (!sharedKeys.Contains(key)) sharedKeys.Add(key);
                return group;
            }
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
            rollSides[key] = sides;
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
                var auto = b is { Kind: not (BindingKind.Input or BindingKind.Piece) } && !HolderChoices.IsPicker(b);   // a pick is always asked
                try
                {
                    var use = b == null || string.IsNullOrWhiteSpace(b.When) || r.ForceWhen?.Contains(name) == true ? true : Condition(Parse(formula, b.When));
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
            var auto = b.Kind is not (BindingKind.Input or BindingKind.Piece) && !HolderChoices.IsPicker(b);
            // B9 #13b: a holder picker's value is always read from its pick (never an override, never filled automatically)
            if (HolderChoices.IsPicker(b)) return PickedHolder(b);
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
                    var prop = Unkeyed(Prop(b));
                    return new(name, Value.Of(Read(LedgerKey.Piece(piece.Id), prop)), true, false, null,
                        $"{prop.Name} of {PieceInfo.Name(piece)}");
                }
                case BindingKind.Global:
                {
                    var prop = Unkeyed(Prop(b));
                    EnsureKept(owner._pack, HolderDef.GlobalId, prop);
                    return new(name, Value.Of(Read(LedgerKey.Holder(HolderDef.GlobalId), prop)), true, false, null,
                        $"{prop.Name} (global)");
                }
                case BindingKind.Holder:
                {
                    var hid = b.HolderId;
                    if (hid == null || (hid != HolderDef.GlobalId && owner._pack.ById<HolderDef>(hid) == null)) throw new Missing("Unknown holder");
                    var prop = Unkeyed(Prop(b));
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

        /// <summary>B7 #47: a Reveal's value as <see cref="Read"/> reads it, or null when it cannot be worked out (never an error).</summary>
        public double? ReadKnown(string key, PropertyDef prop)
        {
            try { return Read(key, prop); }
            catch (Missing) { return null; }
        }

        /// <summary>
        /// A Piece binding: the typed label (trimmed, case-insensitive) resolved among the candidates on the chooser's view
        /// (<see cref="ChooserView"/>, <see cref="PieceChoices"/>), then that piece's property as a Unit binding reads it. Never filled automatically. On a player view
        /// state the placeholder of a masked piece, and a non-Public value of a side the view was not made for, are hidden (missing), not Default.
        /// </summary>
        private VariableState PickedPiece(VariableBinding b)
        {
            var name = b.Variable;
            var prop = Unkeyed(Prop(b));
            var (picked, shown) = Pick(b);
            if (s.IsPlayerView && (PlayerView.IsPlaceholder(picked)
                    || HiddenValues.PieceValueHidden(owner._pack, prop, picked, Sides, HiddenValues.HeldBy(s))))
                throw new Missing(HiddenText);
            return new(name, Value.Of(Read(LedgerKey.Piece(picked.Id), prop)), false, false, null, $"{prop.Name} of {shown}");
        }

        /// <summary>
        /// The piece a Piece binding's typed label picks among the candidates of <see cref="RunRequest.PickSides"/> (the run piece's hex first;
        /// "same hex" only that hex), with its name as the requester sees it; throws <see cref="Missing"/> when it cannot be picked yet.
        /// </summary>
        public (PieceInstance Piece, string Name) Pick(VariableBinding b)
        {
            // Once per variable per run: a Piece binding and the Piece outputs acting on it share one pick (one viewer state on the host).
            if (_picks.TryGetValue(b.Variable, out var known)) return known;
            return _picks[b.Variable] = PickOnce(b);
        }

        private readonly Dictionary<string, (PieceInstance Piece, string Name)> _picks = new(StringComparer.Ordinal);

        private (PieceInstance Piece, string Name) PickOnce(VariableBinding b)
        {
            if (b.SameHex && piece == null) throw new Missing("Needs a unit");
            // The candidates on the chooser's view, shared with the choices of this run's Inputs (one view per run).
            var list = PieceChoices.Named(ChooserView?.State ?? s, owner._pack, owner._model, b, piece?.Id);
            if (list.Count == 0)
                throw new Missing(b.SameHex ? PieceChoices.NoPieceInHex
                    : string.IsNullOrWhiteSpace(b.PieceFilter) ? PieceChoices.NoPiece : $"No piece matches \"{b.PieceFilter.Trim()}\"");
            if (!r.Typed.TryGetValue(b.Variable, out var raw) || string.IsNullOrWhiteSpace(raw)) throw new Missing("Waiting for input");
            var typed = raw.Trim();
            var hit = list.FirstOrDefault(c => string.Equals(c.Label, typed, StringComparison.OrdinalIgnoreCase));
            if (hit.PieceId == null || !s.Pieces.TryGetValue(hit.PieceId, out var picked) || picked == null) throw new Missing(PieceChoices.Unknown);
            return (picked, hit.Name.Length > 0 ? hit.Name : hit.Label);
        }

        /// <summary>
        /// B9 #13b: a holder picker (<see cref="HolderChoices"/>): the typed label (trimmed, case-insensitive) resolved among the listed holders
        /// the chooser may act on (<see cref="RunRequest.PickSides"/>), then the binding's property on that holder as a fixed Holder binding
        /// reads it (Default when unset). Working: "{Prop} of {holder name}". Never filled automatically.
        /// </summary>
        private VariableState PickedHolder(VariableBinding b)
        {
            var prop = Unkeyed(Prop(b));
            var hid = PickHolder(b).HolderId;
            EnsureKept(owner._pack, hid, prop);
            return new(b.Variable, Value.Of(Read(LedgerKey.Holder(hid), prop)), false, false, null, $"{prop.Name} of {HolderText(owner._pack, hid)}");
        }

        /// <summary>
        /// The holder a picker's typed label picks among <see cref="HolderChoices.For"/> of this run's state for <see cref="RunRequest.PickSides"/>;
        /// throws <see cref="Missing"/> ("No holder to choose", "Waiting for input", "Choose one of the listed holders") when it cannot be picked.
        /// Once per variable per run: the binding and the outputs naming it share one list.
        /// </summary>
        public HolderChoice PickHolder(VariableBinding b)
        {
            if (_holderPicks.TryGetValue(b.Variable, out var known)) return known ?? throw new Missing(_holderMissing[b.Variable]);
            try { return _holderPicks[b.Variable] = PickHolderOnce(b); }
            catch (Missing m)
            {
                _holderPicks[b.Variable] = null;
                _holderMissing[b.Variable] = m.Message;
                throw;
            }
        }

        private readonly Dictionary<string, HolderChoice?> _holderPicks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _holderMissing = new(StringComparer.Ordinal);

        private HolderChoice PickHolderOnce(VariableBinding b)
        {
            var list = HolderChoices.For(s, owner._pack, b, r.PickSides);
            if (list.Count == 0) throw new Missing(HolderChoices.None);
            if (!r.Typed.TryGetValue(b.Variable, out var raw) || string.IsNullOrWhiteSpace(raw)) throw new Missing("Waiting for input");
            return HolderChoices.Find(list, raw) ?? throw new Missing(HolderChoices.Unknown);
        }

        private PropertyDef Prop(VariableBinding b) =>
            (b.Property == null ? null : owner._pack.ById<PropertyDef>(b.Property)) ?? throw new Missing("Unknown property");

        /// <summary>A binding never reads a keyed property (B6: it has no single value); the run fails with the validator's advice.</summary>
        private static PropertyDef Unkeyed(PropertyDef prop) =>
            prop.KeyedBy != null && KeyedFacts.IsKeyed(prop) ? throw KeyedValues.BareRead(prop) : prop;

        /// <summary>
        /// An Input binding: the typed value (or its auto value), checked against its choices. Choices from a table are filtered for the run
        /// piece on the chooser's view (<see cref="ChooserView"/>, B7) when the run has one, else on the run's state, so
        /// <see cref="InputChoices.NotOneText"/> means "not in the chooser's list", never "excluded by a value the chooser cannot see".
        /// </summary>
        private VariableState Input(VariableBinding b)
        {
            var name = b.Variable;
            string? source = null;
            // Choices from a table (B4): this counter's keys; none left, or a filter error, leaves the box missing whatever is typed.
            List<string>? choices = null;
            var numeric = false;
            if (!string.IsNullOrWhiteSpace(b.ChoicesTable))
            {
                var keys = InputChoices.TableKeys(owner._pack, b.ChoicesTable) ?? throw new EvalError($"The choices table for {name} was deleted");
                numeric = InputChoices.AllNumeric(keys);
                try
                {
                    // B7: judged on the chooser's view (the chooser's own list: a row excluded only by a value hidden from the chooser is
                    // kept), with the run piece as that view has it; a run piece the chooser cannot see has no choice.
                    if (piece != null && !string.IsNullOrWhiteSpace(b.ChoicesFilter) && ChooserView is { } view)
                        choices = view.State.Pieces.TryGetValue(r.PieceId!, out var shown) && shown != null
                            ? InputChoices.Filter(view.State, owner._pack, b, keys, shown, view.Sides, view.Filters, strict: true)
                            : new List<string>();
                    else choices = InputChoices.Filter(s, owner._pack, b, keys, piece, Sides, Filters, strict: true);
                }
                catch (EvalError ex) { return new(name, null, false, false, ex.Message); }
                if (choices.Count == 0) return new(name, null, false, false, InputChoices.NoChoiceText);
            }
            if (!r.Typed.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                var auto = r.UseAutoValues || r.AutoInputs?.Contains(name) == true;
                if (!auto || b.AlwaysAsk || string.IsNullOrWhiteSpace(b.AutoValue)) return new(name, null, false, false, "Waiting for input");
                try { raw = Evaluator.Evaluate(Parse(formula, b.AutoValue), new RunContext(this)).Display(); }
                catch (Exception ex) when (ex is EvalError or ParseError) { return new(name, null, false, false, ex.Message); }
                source = "default";
            }
            raw = raw.Trim();
            if (choices != null)
            {
                var hit = choices.FirstOrDefault(c => string.Equals(c, raw, StringComparison.OrdinalIgnoreCase));
                if (hit == null) return new(name, null, false, false, InputChoices.NotOneText);
                return numeric && TryNum(hit, out var kn) ? new(name, Value.Of(kn), false, false, null, source)
                    : new(name, Value.Of(hit), false, false, null, source);
            }
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

    /// <summary>The name of the side answering a run (the id when its name is blank, or when the pack has no such side); "" for none.</summary>
    internal static string AskSideName(RulesPack pack, string? sideId) =>
        string.IsNullOrWhiteSpace(sideId) ? ""
        : pack.Sides?.FirstOrDefault(x => x != null && x.Id == sideId) is { } side && !string.IsNullOrWhiteSpace(side.Name) ? side.Name : sideId;

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
        public double Segment() => run.State.Position?.Inner ?? 0;
        public Value Side() => PieceFilter.SideValue(run.Piece, run.Pack, run.Sides);
        public Value PhaseSide() => PieceFilter.PhaseSideValue(run.Pack, Sequencer.Current(run.Pack, run.State));
        public Value AskSide() => Value.Of(AskSideName(run.Pack, run.AskSideId));
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
        // B6: places and keyed values
        public Value Hex() => KeyedValues.Hex(run.Piece, run.Model);
        public double Distance(string a, string b) => KeyedValues.Distance(run.Model, run.Piece, a, b);
        public double Keyed(string property, Value key, string? holder) =>
            KeyedValues.Read(run.State, run.Pack, run.Model, run.Piece, property, key, holder, run.Sides);
    }
}
