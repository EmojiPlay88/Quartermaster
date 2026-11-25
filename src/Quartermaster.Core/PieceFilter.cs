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
        var filters = new AggregateFilters(s, pack, model);   // shared by every piece this filter is evaluated on
        return p =>
        {
            try { return Evaluator.Evaluate(e, new FilterContext(p, s, pack, sides, filters)).IsTruthy; }
            catch (Exception) { return false; }
        };
    }

    /// <summary>
    /// The filter of an aggregate call (sum(P, "filter") ...): like <see cref="Compile"/>, but a parse error (here) or an evaluation error on a piece
    /// (when matching) is thrown, so the aggregate fails instead of skipping pieces. A missing value (<see cref="EvalError.MissingValue"/>: a
    /// property not applicable to the piece or hidden, no such marker) is no match, as in any unit filter. Empty matches every piece.
    /// </summary>
    /// <param name="sides">The caller's side map for this state (shared), or null for the filter's own.</param>
    internal static Func<PieceInstance, bool> CompileStrict(string text, GameState s, RulesPack pack, ModuleModel? model,
        Lazy<Func<PieceInstance, string?>>? sides = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return _ => true;
        var e = Parser.Parse(text);
        sides ??= SideMap(s, pack);
        var filters = new AggregateFilters(s, pack, model) { SharedSides = sides };
        return p =>
        {
            try { return Evaluator.Evaluate(e, new FilterContext(p, s, pack, sides, filters)).IsTruthy; }
            catch (EvalError ex) when (ex.MissingValue) { return false; }
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
        try { return Evaluator.Evaluate(e, new FilterContext(p, s, pack, SideMap(s, pack), new AggregateFilters(s, pack, null), pending)).IsTruthy; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Evaluates a starting value expression (<see cref="PropertyDef.DefaultExpr"/>, spec #4) on a piece, with values about to be seeded read as
    /// if already in its ledger (see <see cref="MatchesWithPending"/>). dice(), holder(), runs() and the piece aggregates are not available
    /// (an <see cref="EvalError"/>), so a seed is deterministic and never copies another side's values. Throws on any error.
    /// </summary>
    /// <remarks>The ban holds however the call is reached: directly, or inside a computed property or computed global the expression reads
    /// (<see cref="InStartingValue"/> is set on this thread while it is evaluated), so a seed never stores a value derived from them.</remarks>
    internal static Value EvaluateWithPending(Quartermaster.Rules.Expr.Expr e, PieceInstance p, GameState s, RulesPack pack, IReadOnlyDictionary<string, double> pending)
    {
        _startingValueDepth++;
        try { return Evaluator.Evaluate(e, new FilterContext(p, s, pack, SideMap(s, pack), new AggregateFilters(s, pack, null), pending, startingValue: true)); }
        finally { _startingValueDepth--; }
    }

    [ThreadStatic] private static int _startingValueDepth;

    /// <summary>True while a starting value expression is evaluated on this thread (<see cref="EvaluateWithPending"/>).</summary>
    internal static bool InStartingValue => _startingValueDepth > 0;

    /// <summary>Throws <c>{fn}() is not available in a starting value</c> while a starting value is evaluated on this thread.</summary>
    internal static void RefuseInStartingValue(string fn)
    {
        if (InStartingValue) throw new EvalError($"{fn}() is not available in a starting value");
    }

    /// <summary>The parsed expression when it parses and reads nothing about a piece (<see cref="ExprFacts.IsGameLevel"/>); else null.</summary>
    internal static Quartermaster.Rules.Expr.Expr? ParseGameLevel(string? text, RulesPack pack)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var e = TryParse(text);
        return e != null && ExprFacts.IsGameLevel(e, pack) ? e : null;
    }

    /// <summary>
    /// Evaluates a game-level expression with no piece: piece functions throw, so an error (or a hidden value in a player view) gives false.
    /// <paramref name="model"/> gives grid locations to a hex aggregate inside a whole-game aggregate's filter (countof("hexcount() > 1")).
    /// </summary>
    internal static bool EvaluateGameLevel(Quartermaster.Rules.Expr.Expr e, GameState s, RulesPack pack, ModuleModel? model = null)
    {
        try { return Evaluator.Evaluate(e, new GameContext(s, pack, model)).IsTruthy; }
        catch (Exception) { return false; }
    }

    /// <summary>Parses and evaluates an expression that reads no piece, once. A parse or evaluation error gives false.</summary>
    public static bool EvaluateGameLevel(string expr, GameState s, RulesPack pack, ModuleModel? model = null) =>
        TryParse(expr) is { } e && EvaluateGameLevel(e, s, pack, model);

    private static Quartermaster.Rules.Expr.Expr? TryParse(string text)
    {
        try { return Parser.Parse(text); }
        catch (Exception) { return null; }
    }

    /// <summary>Formulas that need a unit (a Unit binding or output, or a Piece binding limited to the unit's hex) and apply to this piece, ordered by name.</summary>
    public static IReadOnlyList<FormulaDef> FormulasFor(RulesPack pack, PieceInstance p, GameState s, ModuleModel model) =>
        pack.Formulas
            .Where(f => f.Bindings.Any(b => b.Kind == BindingKind.Unit || (b.Kind == BindingKind.Piece && b.SameHex)) || f.Outputs.Any(o => o.Target == OutputTarget.Unit))
            .Where(f => Matches(f.AppliesTo, p, s, model, pack))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// True iff the piece's ledger holds a value for the property with this name (no default fallback); for a computed property, true when
    /// it applies to the piece (it is never stored).
    /// </summary>
    internal static bool HasProperty(GameState s, RulesPack pack, PieceInstance? p, string name, ModuleModel? model = null)
    {
        if (p == null) return false;
        var def = pack.PropertyByName(name);
        if (def == null) return false;
        return PropertyFacts.IsComputed(def) ? PropertyValues.AppliesTo(s, pack, model, p, def) : Ledger.Get(s, LedgerKey.Piece(p.Id), def.Id) != null;
    }

    internal static Value MarkerValue(PieceInstance? p, string key)
    {
        if (p == null) throw new EvalError("This formula needs a unit");
        return PieceInfo.Markers(p).TryGetValue(key, out var v) ? Value.Of(v) : throw new EvalError($"No marker '{key}'") { MissingValue = true };
    }

    /// <summary>
    /// layer("name"): the level of the piece's layer of that name (trimmed, case-insensitive; the first match), 0 when the layer is inactive.
    /// A piece without such a layer is a missing value (as marker()); a layer that follows a property is an error; another side's masked
    /// placeholder in a player view is hidden data.
    /// </summary>
    internal static double LayerValue(PieceInstance? p, GameState s, string name)
    {
        if (p == null) throw new EvalError("This formula needs a unit");
        if (s.IsPlayerView && PlayerView.IsPlaceholder(p)) throw new EvalError("layer() cannot be read in this view") { HiddenData = true };
        var (index, def) = PieceInfo.FindLayer(PieceInfo.TypeOf(p), name)
            ?? throw new EvalError($"{PieceInfo.Name(p)} has no layer \"{(name ?? "").Trim()}\"") { MissingValue = true };
        if (def.FollowProperty) throw new EvalError($"Layer \"{def.Name.Trim()}\" follows a property; read the property instead");
        var (level, active) = TraitState.LayerLevel(def, index < p.TraitStates.Count ? p.TraitStates[index] : "");
        return active ? level : 0;
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
    /// <remarks>A computed property is evaluated for the holder (<see cref="PropertyValues"/>); <paramref name="filters"/> is the caller's aggregate state.</remarks>
    internal static Value HolderRead(GameState s, RulesPack pack, string holder, string property, ModuleModel? model = null, AggregateFilters? filters = null)
    {
        RefuseInStartingValue("holder");
        var h = pack.HolderByName(holder) ?? throw new EvalError($"No holder named '{holder}'");
        var def = pack.PropertyByName(property) ?? throw new EvalError($"Unknown property '{property}'");
        if (def.Scope == PropertyScope.Pieces) throw new EvalError($"{def.Name} is not a holder property");
        return HolderKeyValue(s, pack, h.Id, h.Name, def, model, filters);
    }

    /// <summary>The holder's value of a holder (or Both) property, with the player-view rule of <see cref="HolderRead"/>.</summary>
    private static Value HolderKeyValue(GameState s, RulesPack pack, string holderId, string holderName, PropertyDef def, ModuleModel? model,
        AggregateFilters? filters)
    {
        var key = LedgerKey.Holder(holderId);
        var v = PropertyValues.Get(s, pack, filters?.Model ?? model, key, def, filters);
        if (v != null) return Value.Of(v.Value);
        if (s.IsPlayerView && Ownership.SideOfHolder(holderId, pack) is { } side && !s.Ledgers.ContainsKey(key)
            && s.ViewSides?.Contains(side) != true)
            throw new EvalError($"{def.Name} has no value on {holderName}") { MissingValue = true };
        return Value.Of(def.Default);
    }

    internal static double TurnOf(GameState s) => s.Position?.Turn ?? s.Turn.Turn;
    internal static double StageOf(GameState s) => s.Position?.Repeat ?? 1;

    /// <summary>A Holders-scope property reads the global holder's value (else its Default; a computed one is evaluated for the global holder).</summary>
    internal static Value GlobalValue(GameState s, RulesPack pack, PropertyDef def, AggregateFilters? filters) =>
        Value.Of(PropertyValues.GetOrDefault(s, pack, filters?.Model, LedgerKey.Holder(HolderDef.GlobalId), def, filters));

    /// <summary>The context a computed property is evaluated in for a piece: the unit filter's (the piece's properties, globals, functions).</summary>
    /// <param name="pending">Values about to be seeded on the piece (see <see cref="MatchesWithPending"/>), read as if already in its ledger.</param>
    internal static FilterContext PieceContext(PieceInstance p, GameState s, RulesPack pack, AggregateFilters filters,
        IReadOnlyDictionary<string, double>? pending = null) =>
        new(p, s, pack, ReferenceEquals(filters.State, s) ? filters.Sides : SideMap(s, pack), filters, pending);

    /// <summary>The turn, stage and phase an expression reads: the phase condition's candidate (<see cref="AggregateFilters.At"/>), else the game's.</summary>
    private static double TurnAt(GameState s, AggregateFilters? f) => f?.At is { } at ? at.Turn : TurnOf(s);
    private static double StageAt(GameState s, AggregateFilters? f) => f?.At is { } at ? at.Repeat : StageOf(s);
    private static Value PhaseSideAt(GameState s, RulesPack pack, AggregateFilters? f) =>
        PhaseSideValue(pack, f?.At is { } at ? PhaseConditions.PhaseAt(pack, at) : Sequencer.Current(pack, s));

    /// <summary>
    /// The context a computed property is evaluated in for a holder: holder (and Both) properties read this holder's values, globals and
    /// holder() as anywhere; a unit property or piece function is "This formula needs a unit".
    /// </summary>
    internal static IEvalContext HolderContext(GameState s, RulesPack pack, string holderId, AggregateFilters filters) =>
        new GameContext(s, pack, filters.Model, holderId, filters);

    internal static Value LookupTable(RulesPack pack, string tableName, Value row, Value? col) =>
        TableLookup.Lookup(TableNamed(pack, tableName), row, col);

    /// <summary>lookup() while a piece is evaluated: the located cell, or the piece's own override of it (<see cref="CellOverrides.Read"/>).</summary>
    internal static Value LookupTable(RulesPack pack, string tableName, Value row, Value? col, PieceInstance? piece, GameState s,
        ModuleModel? model, AggregateFilters? filters = null, Lazy<Func<PieceInstance, string?>>? sides = null)
    {
        var t = TableNamed(pack, tableName);
        var (r, c) = TableLookup.Locate(t, row, col);
        return CellOverrides.Read(t, r, c, piece, s, pack, model, filters, sides).Value;
    }

    /// <summary>lookuprange(): the column key of the first cell of the row that holds the value (<see cref="TableLookup.RangeColumn"/>).</summary>
    internal static Value LookupRangeTable(RulesPack pack, string tableName, Value row, Value value) =>
        TableLookup.RangeColumn(TableNamed(pack, tableName), row, value);

    private static TableDef TableNamed(RulesPack pack, string tableName) =>
        pack.TableByName(tableName) ?? throw new EvalError($"No table named '{tableName}'");

    /// <summary>Parsed applies-to per property record (records are replaced on edit, so this never goes stale); null = does not parse.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PropertyDef, ParsedAppliesTo> AppliesToCache = new();

    private sealed class ParsedAppliesTo(Quartermaster.Rules.Expr.Expr? expr) { public Quartermaster.Rules.Expr.Expr? Expr { get; } = expr; }

    internal sealed class FilterContext(PieceInstance p, GameState s, RulesPack pack, Lazy<Func<PieceInstance, string?>> sides,
        AggregateFilters filters, IReadOnlyDictionary<string, double>? pending = null, bool startingValue = false) : IEvalContext
    {
        /// <summary>In a starting value (<see cref="EvaluateWithPending"/>) these calls fail.</summary>
        private static EvalError NotInStartingValue(string fn) => new($"{fn}() is not available in a starting value");

        private readonly HashSet<string> _checking = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _declared = new(StringComparer.Ordinal);

        /// <summary>The piece's ledger value, else a value about to be seeded, else null.</summary>
        private double? Stored(PropertyDef def) =>
            Ledger.Get(s, LedgerKey.Piece(p.Id), def.Id) ?? (pending != null && pending.TryGetValue(def.Id, out var v) ? v : null);

        public Value GetVariable(string name)
        {
            var def = pack.PropertyByName(name) ?? throw new EvalError($"Unknown property '{name}'");
            if (def.Scope == PropertyScope.Holders) return GlobalValue(s, pack, def, filters);
            if (PropertyFacts.IsComputed(def))
                return PropertyValues.Get(s, pack, filters.Model, LedgerKey.Piece(p.Id), def, filters, pending) is { } c ? Value.Of(c)
                    : throw new EvalError($"{name} has no value on this piece") { MissingValue = true };
            var v = Stored(def);
            if (v != null) return Value.Of(v.Value);
            if (!HiddenInView(def) && Declared(def)) return Value.Of(def.Default);
            throw new EvalError($"{name} has no value on this piece") { MissingValue = true };
        }

        /// <summary>
        /// In a player's view a missing value may be hidden rather than unset: a Private value, or any value of the placeholder left for
        /// another side's masked piece (its whole ledger was removed). Those do not fall back to the Default.
        /// </summary>
        private bool HiddenInView(PropertyDef def) =>
            s.IsPlayerView && (!PropertyFacts.IsPublic(pack, def) || PlayerView.IsPlaceholder(p));

        /// <summary>True when the property is declared on this piece: not Holders-scoped, and its applies-to is empty or matches. Re-entry counts as not declared.</summary>
        internal bool Declared(PropertyDef def)
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
                    catch (Exception ex) when (ex is EvalError or ParseError && !PropertyValues.IsCycle(ex)) { result = false; }   // a cycle is reported
                }
                _declared[def.Id] = result;
                return result;
            }
            finally { _checking.Remove(def.Id); }
        }

        public Value Lookup(string tableName, Value row, Value? col) => LookupTable(pack, tableName, row, col, p, s, filters.Model, filters, sides);
        public Value LookupRange(string table, Value row, Value value) => LookupRangeTable(pack, table, row, value);
        public IReadOnlyList<int> RollDice(int n, int sides) => throw (startingValue || InStartingValue ? NotInStartingValue("dice") : new EvalError("Dice are not allowed here"));
        public bool Has(string property) =>
            pack.PropertyByName(property) is { } def && (PropertyFacts.IsComputed(def) ? Declared(def) : Stored(def) != null);
        public Value PieceName() => NameValue(p);
        public Value Marker(string key) => MarkerValue(p, key);
        public double Layer(string name) => LayerValue(p, s, name);
        public double Turn() => TurnAt(s, filters);
        public double Stage() => StageAt(s, filters);
        public Value Side() => SideValue(p, pack, sides);
        public Value PhaseSide() => PhaseSideAt(s, pack, filters);
        public Value MapName() => MapValue(p);
        public Value HolderValue(string holder, string property) =>
            startingValue ? throw NotInStartingValue("holder") : HolderRead(s, pack, holder, property, filters.Model, filters);
        public Value Aggregate(string fn, string? property, string? filter) =>
            startingValue ? throw NotInStartingValue(fn) : PieceAggregates.Evaluate(fn, property, filter, p, filters);
        public double Runs(string formulaId, string period) =>
            startingValue ? throw NotInStartingValue("runs") : RunIndex.Count(s, pack, formulaId, p.Id, period, sides);
    }

    /// <summary>
    /// The context of an expression read once for the whole game: globals, holder(), turn, stage and phaseside; nothing of a piece. With
    /// <paramref name="holderId"/> it is a holder's context (a computed holder property): holder and Both properties read that holder.
    /// </summary>
    private sealed class GameContext(GameState s, RulesPack pack, ModuleModel? model, string? holderId = null, AggregateFilters? filters = null) : IEvalContext
    {
        private AggregateFilters? _filters = filters;

        private AggregateFilters Filters => _filters ??= new(s, pack, model);

        public Value GetVariable(string name)
        {
            var def = pack.PropertyByName(name) ?? throw new EvalError($"Unknown property '{name}'");
            if (holderId != null && def.Scope != PropertyScope.Pieces)
                return HolderKeyValue(s, pack, holderId, holderId == HolderDef.GlobalId ? "Global" : pack.ById<HolderDef>(holderId)?.Name ?? holderId, def,
                    model, PropertyFacts.IsComputed(def) ? Filters : _filters);
            if (def.Scope == PropertyScope.Holders)
                return Value.Of(PropertyValues.GetOrDefault(s, pack, model, LedgerKey.Holder(HolderDef.GlobalId), def, PropertyFacts.IsComputed(def) ? Filters : _filters));
            throw new EvalError("This formula needs a unit");
        }

        public Value Lookup(string tableName, Value row, Value? col) => LookupTable(pack, tableName, row, col);
        public Value LookupRange(string table, Value row, Value value) => LookupRangeTable(pack, table, row, value);
        public IReadOnlyList<int> RollDice(int n, int sides)
        {
            RefuseInStartingValue("dice");
            throw new EvalError("Dice are not allowed here");
        }
        public bool Has(string property) => false;
        public Value PieceName() => NameValue(null);
        public Value Marker(string key) => MarkerValue(null, key);
        public double Turn() => TurnAt(s, _filters);
        public double Stage() => StageAt(s, _filters);
        public Value Side() => SideValue(null, pack, SideMap(s, pack));
        public Value PhaseSide() => PhaseSideAt(s, pack, _filters);
        public Value MapName() => MapValue(null);
        public Value HolderValue(string holder, string property) => HolderRead(s, pack, holder, property, model, _filters);
        public Value Aggregate(string fn, string? property, string? filter) =>
            PieceAggregates.Evaluate(fn, property, filter, null, Filters);
        /// <summary>No piece: the formula's runs made without a unit (hidden in a player view).</summary>
        public double Runs(string formulaId, string period) => RunIndex.Count(s, pack, formulaId, null, period);
    }
}
