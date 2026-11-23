using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>Decides which pieces a formula (or property) applies to, using the piece-facing expression functions.</summary>
public static class PieceFilter
{
    /// <summary>Null or empty matches every piece. Parse and evaluation errors give false.</summary>
    public static bool Matches(string? appliesTo, PieceInstance p, GameState s, ModuleModel model, RulesPack pack) =>
        Compile(appliesTo, s, pack, model)(p);

    /// <summary>
    /// Parses once. Null or empty matches every piece. A parse error, or an evaluation error on a piece, gives false. The hex aggregates
    /// (hexsum() ...) need <paramref name="model"/> for grid locations; without it they are not available (an error, so false).
    /// </summary>
    public static Func<PieceInstance, bool> Compile(string? expr, GameState s, RulesPack pack, ModuleModel? model = null)
    {
        if (string.IsNullOrWhiteSpace(expr)) return _ => true;
        var e = TryParse(expr);
        if (e == null) return _ => false;
        var sides = SideMap(s, pack);
        var filters = AggregateFilters(s, pack, model);   // shared by every piece this filter is evaluated on
        return p =>
        {
            try { return Evaluator.Evaluate(e, new FilterContext(p, s, pack, sides, filters, model)).IsTruthy; }
            catch (Exception) { return false; }
        };
    }

    /// <summary>
    /// Like <see cref="Matches"/>, with values about to be seeded on this piece (property id -> value) read as if already in its ledger
    /// (a ledger value still wins). Used by seeding, so an applies-to sees a sibling's sheet value rather than its Default.
    /// </summary>
    internal static bool MatchesWithPending(string? appliesTo, PieceInstance p, GameState s, RulesPack pack, IReadOnlyDictionary<string, double> pending)
    {
        if (string.IsNullOrWhiteSpace(appliesTo)) return true;
        var e = TryParse(appliesTo);
        if (e == null) return false;
        try { return Evaluator.Evaluate(e, new FilterContext(p, s, pack, SideMap(s, pack), AggregateFilters(s, pack, null), null, pending)).IsTruthy; }
        catch (Exception) { return false; }
    }

    /// <summary>The parsed expression when it parses and reads nothing about a piece (<see cref="ExprFacts.IsGameLevel"/>); else null.</summary>
    internal static Quartermaster.Rules.Expr.Expr? ParseGameLevel(string? text, RulesPack pack)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var e = TryParse(text);
        return e != null && ExprFacts.IsGameLevel(e, pack) ? e : null;
    }

    /// <summary>Evaluates a game-level expression with no piece: piece functions throw, so an error (or a hidden value in a player view) gives false.</summary>
    internal static bool EvaluateGameLevel(Quartermaster.Rules.Expr.Expr e, GameState s, RulesPack pack)
    {
        try { return Evaluator.Evaluate(e, new GameContext(s, pack)).IsTruthy; }
        catch (Exception) { return false; }
    }

    /// <summary>Parses and evaluates an expression that reads no piece, once. A parse or evaluation error gives false.</summary>
    public static bool EvaluateGameLevel(string expr, GameState s, RulesPack pack) =>
        TryParse(expr) is { } e && EvaluateGameLevel(e, s, pack);

    private static Quartermaster.Rules.Expr.Expr? TryParse(string text)
    {
        try { return Parser.Parse(text); }
        catch (Exception) { return null; }
    }

    /// <summary>Formulas that need a unit (a Unit binding or output) and apply to this piece, ordered by name.</summary>
    public static IReadOnlyList<FormulaDef> FormulasFor(RulesPack pack, PieceInstance p, GameState s, ModuleModel model) =>
        pack.Formulas
            .Where(f => f.Bindings.Any(b => b.Kind == BindingKind.Unit) || f.Outputs.Any(o => o.Target == OutputTarget.Unit))
            .Where(f => Matches(f.AppliesTo, p, s, model, pack))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>True iff the piece's ledger holds a value for the property with this name (no default fallback).</summary>
    internal static bool HasProperty(GameState s, RulesPack pack, PieceInstance? p, string name)
    {
        if (p == null) return false;
        var def = pack.PropertyByName(name);
        return def != null && Ledger.Get(s, LedgerKey.Piece(p.Id), def.Id) != null;
    }

    internal static Value MarkerValue(PieceInstance? p, string key)
    {
        if (p == null) throw new EvalError("This formula needs a unit");
        return PieceInfo.Markers(p).TryGetValue(key, out var v) ? Value.Of(v) : throw new EvalError($"No marker '{key}'");
    }

    internal static Value NameValue(PieceInstance? p) =>
        p == null ? throw new EvalError("This formula needs a unit") : Value.Of(PieceInfo.Name(p));

    /// <summary>
    /// The side map (<see cref="Ownership.PieceSides"/>), compiled on first use and then shared, so one compiled filter or one formula
    /// run parses the side unit filters once rather than once per piece.
    /// </summary>
    internal static Lazy<Func<PieceInstance, string?>> SideMap(GameState s, RulesPack pack) =>
        new(() => Ownership.PieceSides(s, pack), LazyThreadSafetyMode.None);

    /// <summary>side(): the name of the side owning the piece, "" when none. Refused inside a side's unit filter (it would recurse).</summary>
    internal static Value SideValue(PieceInstance? p, RulesPack pack, Lazy<Func<PieceInstance, string?>> sides)
    {
        if (p == null) throw new EvalError("This formula needs a unit");
        if (Ownership.InUnitFilter) throw new EvalError("side() is not available in a side's unit filter");
        var id = sides.Value(p);
        return Value.Of(id == null ? "" : (pack.Sides ?? new List<SideDef>()).FirstOrDefault(x => x != null && x.Id == id)?.Name ?? "");
    }

    /// <summary>The compiled filters of aggregate calls (sum(P, "filter") ...), each filter text compiled once per evaluation context.</summary>
    internal static Func<string, Func<PieceInstance, bool>> AggregateFilters(GameState s, RulesPack pack, ModuleModel? model) =>
        PieceAggregates.Cached(text => Compile(text, s, pack, model));

    /// <summary>The name of the phase's acting side; "" when there is no phase or it has no (existing) side.</summary>
    internal static Value PhaseSideValue(RulesPack pack, PhaseDef? phase) =>
        Value.Of(phase == null ? "" : Sequencer.ActingSide(pack, phase)?.Name ?? "");

    internal static Value MapValue(PieceInstance? p) =>
        p == null ? throw new EvalError("This formula needs a unit") : Value.Of(p.MapId ?? "");

    /// <summary>
    /// holder(Holder, Property): the holder's value, else the property's Default. A player's view removes every value (and the ledger
    /// key) of a holder owned by a side the viewer does not hold, so in a view a sided holder with no ledger key is hidden, not unset, and
    /// does not fall back to the Default. A present key belongs to a holder the viewer may see. A holder of a side the view was made for
    /// (<see cref="GameState.ViewSides"/>) is never stripped, so there a missing value reads the Default.
    /// </summary>
    internal static Value HolderRead(GameState s, RulesPack pack, string holder, string property)
    {
        var h = pack.HolderByName(holder) ?? throw new EvalError($"No holder named '{holder}'");
        var def = pack.PropertyByName(property) ?? throw new EvalError($"Unknown property '{property}'");
        if (def.Scope == PropertyScope.Pieces) throw new EvalError($"{def.Name} is not a holder property");
        var key = LedgerKey.Holder(h.Id);
        var v = Ledger.Get(s, key, def.Id);
        if (v != null) return Value.Of(v.Value);
        if (s.IsPlayerView && Ownership.SideOfHolder(h.Id, pack) is { } side && !s.Ledgers.ContainsKey(key)
            && s.ViewSides?.Contains(side) != true)
            throw new EvalError($"{def.Name} has no value on {h.Name}");
        return Value.Of(def.Default);
    }

    internal static double TurnOf(GameState s) => s.Position?.Turn ?? s.Turn.Turn;
    internal static double StageOf(GameState s) => s.Position?.Repeat ?? 1;

    /// <summary>A Holders-scope property reads the global holder's value (else its Default).</summary>
    internal static Value GlobalValue(GameState s, PropertyDef def) => Value.Of(Ledger.GetOrDefault(s, LedgerKey.Holder(HolderDef.GlobalId), def));

    internal static Value LookupTable(RulesPack pack, string tableName, Value row, Value? col) =>
        TableLookup.Lookup(TableNamed(pack, tableName), row, col);

    /// <summary>lookuprange(): the column key of the first cell of the row that holds the value (<see cref="TableLookup.RangeColumn"/>).</summary>
    internal static Value LookupRangeTable(RulesPack pack, string tableName, Value row, Value value) =>
        TableLookup.RangeColumn(TableNamed(pack, tableName), row, value);

    private static TableDef TableNamed(RulesPack pack, string tableName) =>
        pack.TableByName(tableName) ?? throw new EvalError($"No table named '{tableName}'");

    /// <summary>Parsed applies-to per property record (records are replaced on edit, so this never goes stale); null = does not parse.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PropertyDef, ParsedAppliesTo> AppliesToCache = new();

    private sealed class ParsedAppliesTo(Quartermaster.Rules.Expr.Expr? expr) { public Quartermaster.Rules.Expr.Expr? Expr { get; } = expr; }

    private sealed class FilterContext(PieceInstance p, GameState s, RulesPack pack, Lazy<Func<PieceInstance, string?>> sides,
        Func<string, Func<PieceInstance, bool>> filters, ModuleModel? model, IReadOnlyDictionary<string, double>? pending = null) : IEvalContext
    {
        private readonly HashSet<string> _checking = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _declared = new(StringComparer.Ordinal);

        /// <summary>The piece's ledger value, else a value about to be seeded, else null.</summary>
        private double? Stored(PropertyDef def) =>
            Ledger.Get(s, LedgerKey.Piece(p.Id), def.Id) ?? (pending != null && pending.TryGetValue(def.Id, out var v) ? v : null);

        public Value GetVariable(string name)
        {
            var def = pack.PropertyByName(name) ?? throw new EvalError($"Unknown property '{name}'");
            if (def.Scope == PropertyScope.Holders) return GlobalValue(s, def);
            var v = Stored(def);
            if (v != null) return Value.Of(v.Value);
            if (!HiddenInView(def) && Declared(def)) return Value.Of(def.Default);
            throw new EvalError($"{name} has no value on this piece");
        }

        /// <summary>
        /// In a player's view a missing value may be hidden rather than unset: a Private value, or any value of the placeholder left for
        /// another side's masked piece (its whole ledger was removed). Those do not fall back to the Default.
        /// </summary>
        private bool HiddenInView(PropertyDef def) =>
            s.IsPlayerView && (def.Visibility == Visibility.Private || PlayerView.IsPlaceholder(p));

        /// <summary>True when the property is declared on this piece: not Holders-scoped, and its applies-to is empty or matches. Re-entry counts as not declared.</summary>
        private bool Declared(PropertyDef def)
        {
            if (def.Scope == PropertyScope.Holders) return false;
            if (string.IsNullOrWhiteSpace(def.AppliesTo)) return true;
            if (_declared.TryGetValue(def.Id, out var known)) return known;
            if (!_checking.Add(def.Id)) return false;
            try
            {
                var e = AppliesToCache.GetValue(def, d => new ParsedAppliesTo(TryParse(d.AppliesTo!))).Expr;
                var result = false;
                if (e != null)
                {
                    try { result = Evaluator.Evaluate(e, this).IsTruthy; }
                    catch (Exception ex) when (ex is EvalError or ParseError) { result = false; }
                }
                _declared[def.Id] = result;
                return result;
            }
            finally { _checking.Remove(def.Id); }
        }

        public Value Lookup(string tableName, Value row, Value? col) => LookupTable(pack, tableName, row, col);
        public Value LookupRange(string table, Value row, Value value) => LookupRangeTable(pack, table, row, value);
        public IReadOnlyList<int> RollDice(int n, int sides) => throw new EvalError("Dice are not allowed here");
        public bool Has(string property) => pack.PropertyByName(property) is { } def && Stored(def) != null;
        public Value PieceName() => NameValue(p);
        public Value Marker(string key) => MarkerValue(p, key);
        public double Turn() => TurnOf(s);
        public double Stage() => StageOf(s);
        public Value Side() => SideValue(p, pack, sides);
        public Value PhaseSide() => PhaseSideValue(pack, Sequencer.Current(pack, s));
        public Value MapName() => MapValue(p);
        public Value HolderValue(string holder, string property) => HolderRead(s, pack, holder, property);
        public Value Aggregate(string fn, string? property, string? filter) =>
            PieceAggregates.Evaluate(fn, property, filter, s, pack, model, p, filters);
    }

    /// <summary>The context of an expression read once for the whole game: globals, holder(), turn, stage and phaseside; nothing of a piece.</summary>
    private sealed class GameContext(GameState s, RulesPack pack) : IEvalContext
    {
        private readonly Func<string, Func<PieceInstance, bool>> _filters = AggregateFilters(s, pack, null);

        public Value GetVariable(string name)
        {
            var def = pack.PropertyByName(name) ?? throw new EvalError($"Unknown property '{name}'");
            if (def.Scope == PropertyScope.Holders) return GlobalValue(s, def);
            throw new EvalError("This formula needs a unit");
        }

        public Value Lookup(string tableName, Value row, Value? col) => LookupTable(pack, tableName, row, col);
        public Value LookupRange(string table, Value row, Value value) => LookupRangeTable(pack, table, row, value);
        public IReadOnlyList<int> RollDice(int n, int sides) => throw new EvalError("Dice are not allowed here");
        public bool Has(string property) => false;
        public Value PieceName() => NameValue(null);
        public Value Marker(string key) => MarkerValue(null, key);
        public double Turn() => TurnOf(s);
        public double Stage() => StageOf(s);
        public Value Side() => SideValue(null, pack, SideMap(s, pack));
        public Value PhaseSide() => PhaseSideValue(pack, Sequencer.Current(pack, s));
        public Value MapName() => MapValue(null);
        public Value HolderValue(string holder, string property) => HolderRead(s, pack, holder, property);
        public Value Aggregate(string fn, string? property, string? filter) =>
            PieceAggregates.Evaluate(fn, property, filter, s, pack, null, null, _filters);
    }
}
