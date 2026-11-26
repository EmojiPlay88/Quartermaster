using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// Formula input masking (spec 8a.2): which inputs of a formula come from data the viewer may not see, and how the formula dialog, the
/// batch grid and the cheat sheet show them. The one source of truth for the host (<see cref="FormulaIntentRunner"/>) and the local
/// dialogs, so the boxes the host reports as hidden are exactly the boxes the dialog masks.
/// <para>A formula reads another side's data through its bindings: a Unit binding reads the subject piece, a Holder binding a holder's
/// value (Global is public, tables are pack data); and through holder(H, P) in its text, auto values and table row/column expressions,
/// marked by <see cref="HolderCallInput"/>; and through a piece aggregate (sum, countof, hexsum...) that reads every piece, marked by
/// <see cref="AggregateCallInput"/> when it counts or reads a property that is not Public (itself or in its filter), or whatever it reads once
/// the viewer's view may lack pieces or values (<see cref="PieceAccess.PiecesMayHide"/>: from the pack and module, so host and guest agree);
/// and through a table read (a Table binding, or lookup() in its expressions, marked by <see cref="TableCallInput"/>) of a table a non-Public
/// property overrides, which reads that property on the run piece and so hides exactly when a Private Unit binding would. Whether a value is
/// hidden is decided by the one visibility rule, <see cref="HiddenValues"/> (B7): a viewer who sees everything (solo, every side held, a game
/// that is over) has nothing hidden; else an input is hidden when it reads any value of a piece the viewer cannot see, a value of a piece of
/// a side not held unless its property is Public, a value of a holder owned by a side not held unless its property is Public (Holder
/// bindings, holder(H, P) and keyed() with a holder argument pass the property they read), a holder missing from the pack, or a place
/// value that is not Public. A binding (any kind but Piece) whose own expressions read a hidden variable is hidden too. The rule depends only on
/// the rules pack and the pieces, never on whether a value is set, so it gives the same answer on the host's full state and on a guest's
/// filtered snapshot. A table lookup counts as hidden when any other input is, since its keys may be computed from them. A computed property
/// built from hidden data (<see cref="FormulaVariables.ComputedFromHidden"/>) is hidden wherever it is read, even on the viewer's own unit
/// or the global holder.</para>
/// </summary>
public static class FormulaMasking
{
    /// <summary>A hidden box on the host's own screen (hot-seat, or the online host): the value is calculated here but never shown.</summary>
    public const string HiddenText = ModuleSession.HiddenText;

    /// <summary>A hidden box on a guest's screen: the guest does not have the value; the host fills it in.</summary>
    public const string HostCalculatesText = "hidden — the host calculates";

    /// <summary>The working line shown in place of one that would show a hidden input.</summary>
    public const string WorkingHiddenText = "calculated (inputs hidden)";

    /// <summary>The status shown in place of an evaluation error when an input is hidden (an error may quote a hidden value).</summary>
    public const string ErrorHiddenText = "The formula could not be calculated.";

    /// <summary>
    /// The entry <see cref="HiddenVariables"/> adds when the formula's own expressions read a hidden holder through holder(H, P): no box is
    /// hidden by it (it is not a variable name), but the formula counts as masked, so its error and working are never shown.
    /// </summary>
    public const string HolderCallInput = "holder()";

    /// <summary>
    /// The entry <see cref="HiddenVariables"/> adds when the formula's own expressions use a piece aggregate that may read hidden data (see
    /// <see cref="FormulaVariables.AggregateReadsHidden"/>): like <see cref="HolderCallInput"/>, it hides no box but masks the formula.
    /// </summary>
    public const string AggregateCallInput = "aggregate()";

    /// <summary>
    /// The entry <see cref="HiddenVariables"/> adds when the formula's own expressions look up a table that a non-Public property overrides
    /// (<see cref="Quartermaster.Rules.Tables.CellOverrideFacts.HiddenOverride"/>) while it runs on a piece whose private values the viewer
    /// cannot see: like <see cref="HolderCallInput"/>, it hides no box but masks the formula.
    /// </summary>
    public const string TableCallInput = "lookup()";

    /// <summary>
    /// The entry <see cref="HiddenVariables"/> adds when the formula acts on a picked piece (a <c>Target = Piece</c> output) that may hold data
    /// the viewer cannot see: an output writes a property that is not Public, or the viewer's view may lack pieces or values
    /// (<see cref="PieceAccess.PiecesMayHide"/>) and the formula has any Piece output (a write or an effect). Like <see cref="HolderCallInput"/>,
    /// it hides no box but masks the formula's working and errors (the target's values before and after, its name and layer levels).
    /// </summary>
    public const string PieceOutputInput = "piece output";

    /// <summary>
    /// The entry <see cref="HiddenVariables"/> adds when the formula's own expressions read a keyed value through keyed() that the viewer may
    /// not see (B6): a place that is not Public, a named holder whose values are hidden, an unknown property or holder, or a non-Public value of
    /// a run piece the viewer cannot read. Like <see cref="HolderCallInput"/>, it hides no box but masks the formula.
    /// </summary>
    public const string KeyedCallInput = "keyed()";

    /// <summary>
    /// The entry <see cref="HiddenVariables"/> adds when an output writes a place that is not Public (B6): its working shows the value before
    /// the change. Like <see cref="HolderCallInput"/>, it hides no box but masks the formula.
    /// </summary>
    public const string PlaceOutputInput = "place output";

    private static readonly IReadOnlySet<string> NoSides = new HashSet<string>();

    /// <summary>
    /// True when a viewer holding <paramref name="sides"/> may see this output's value (before and after): a viewer who sees everything
    /// (<see cref="HiddenValues.SeesEverything"/>: solo, every side held, the game is over) sees it; otherwise
    /// the viewer may edit its ledger value, and a keyed value is not hidden from it (<see cref="KeyedValues.HiddenFor"/>: a Private place,
    /// another side's keyed value). Decides the dialog's "(negative)" mark and the batch grids' ledger text.
    /// </summary>
    public static bool OutputShown(ModuleSession s, IReadOnlySet<string>? sides, OutputPreview o)
    {
        if (HiddenValues.SeesEverything(s.Game?.State, s.Rules, sides)) return true;
        if (o == null || !s.CanEditLedgerFor(o.Key, sides)) return false;
        if (o.ValueKey == null) return true;   // unkeyed: the ledger rule above
        if (s.Rules is not { } pack || s.Game?.State is not { } st) return false;
        var prop = (pack.Properties ?? new List<PropertyDef>()).FirstOrDefault(p => p != null && p.Id == o.PropertyId);
        return prop != null && !KeyedValues.HiddenFor(st, pack, sides, o.Key, prop);
    }

    /// <summary>
    /// The sides whose view the local dialogs mask for: null in solo (no masking); the viewer's sides in hot-seat and on the online host's
    /// screen; the guest's held sides (none before one is picked) for an online guest.
    /// </summary>
    public static IReadOnlySet<string>? ViewerSides(ModuleSession s) =>
        s.IsGuest ? s.GuestSides ?? NoSides : s.ViewSides;

    /// <summary>
    /// The sides a Piece binding's candidates are listed and resolved for (<see cref="RunRequest.PickSides"/>): the viewer's sides when the
    /// dialog runs on the full state (hot-seat, the online host's screen); null in solo and on an online guest, whose state is already its view.
    /// </summary>
    public static IReadOnlySet<string>? PickSides(ModuleSession s) => s.IsGuest ? null : s.ViewSides;

    /// <summary>The hidden inputs of a formula for the current viewer of <paramref name="s"/> (empty in solo).</summary>
    public static HashSet<string> HiddenFor(ModuleSession s, FormulaDef f, string? pieceId) =>
        s.Rules is { } pack && s.Game?.State is { } st
            ? HiddenVariables(pack, s.Model, st, f, pieceId, ViewerSides(s))
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Masks a dialog model for the current viewer of <paramref name="s"/>: its hidden inputs, and whether the host calculates them (guest).</summary>
    public static void Configure(FormulaDialogModel m, ModuleSession s)
    {
        m.Hidden = HiddenFor(s, m.Formula, m.PieceId);
        m.HostCalculates = s.IsGuest;
        m.PickSides = PickSides(s);
        var sides = ViewerSides(s);
        m.OutputVisible = sides == null ? null : o => OutputShown(s, sides, o);
    }

    /// <summary>
    /// The variables of <paramref name="f"/> whose value comes from data a viewer holding <paramref name="sides"/> may not see. Works on a
    /// full state and on a filtered one alike. Empty when the viewer sees everything (<see cref="HiddenValues.SeesEverything"/>: solo, every
    /// side held, or the game is over). Fails closed: a subject piece that is
    /// not in the state, or that the viewer cannot see, hides every Unit input.
    /// </summary>
    public static HashSet<string> HiddenVariables(RulesPack pack, ModuleModel? model, GameState state, FormulaDef f, string? pieceId,
        IReadOnlySet<string>? sides)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (pack == null || f == null || HiddenValues.SeesEverything(state, pack, sides)) return hidden;   // as PlayerView.For
        var holds = sides!.Contains;   // the viewer's held sides, as every HiddenValues caller takes them
        var access = new PieceAccess(state, pack, model, sides);
        var props = pack.Properties ?? new List<PropertyDef>();
        PropertyDef? PropById(string? id) => id == null ? null : props.FirstOrDefault(p => p != null && p.Id == id);
        bool PropPrivate(string? prop) => !PropertyFacts.IsPublic(pack, PropById(prop));   // computed from hidden data: Private
        // A value of the run piece (null property: any of its values) is hidden: the piece is not in the state, the viewer cannot see it, or
        // HiddenValues hides that value of a piece of its side.
        bool RunPieceValueHidden(PropertyDef? prop)
        {
            if (!state.Pieces.TryGetValue(pieceId!, out var p) || p == null || !access.CanSee(pieceId)) return true;
            try { return HiddenValues.PieceValueHidden(pack, prop, access.SideOf(p), holds); }
            catch (Exception) { return true; }   // fail closed
        }
        // The run piece's private values are hidden (no piece: a table read is the printed cell).
        bool RunPieceHidden() => pieceId != null && RunPieceValueHidden(null);
        // A table read of the run piece's own row may be a non-Public override: hidden as a Private Unit read is.
        bool TableHidden(string? tableId) => tableId != null && Quartermaster.Rules.Tables.CellOverrideFacts.HiddenOverride(pack, tableId) && RunPieceHidden();
        // A unit-less run has no unit values to read.
        bool UnitHidden(string? prop) => pieceId != null && RunPieceValueHidden(PropById(prop));
        // A holder's value of a property (null: the whole key) as HiddenValues decides it; no holder named reads the Global holder.
        bool HolderHidden(string? holderId, PropertyDef? prop) =>
            holderId != null && HiddenValues.HolderValueHidden(pack, holderId, prop, holds);
        foreach (var b in f.Bindings ?? new List<VariableBinding>())
        {
            if (b?.Variable == null) continue;
            var isHidden = b.Kind switch
            {
                BindingKind.Unit => UnitHidden(b.Property),
                BindingKind.Holder => HolderHidden(b.HolderId, PropById(b.Property)),
                BindingKind.Table => TableHidden(b.TableId),   // otherwise decided below
                _ => false,   // Global is public; Input, Roll, Location and Moved are the player's own; Piece is decided below
            };
            if (isHidden) hidden.Add(b.Variable);
        }
        // holder(H, P) in the formula's own expressions reads the holder as a Holder binding would; an unresolvable call fails closed.
        if (FormulaVariables.HolderReads(f).Any(r => r.Holder == null || pack.HolderByName(r.Holder) is not { } h
                || HolderHidden(h.Id, r.Property == null ? null : pack.PropertyByName(r.Property))))
            hidden.Add(HolderCallInput);
        // Aggregates read every piece, hidden or not: a count, a non-public property or a filter over one reads data this viewer may not see,
        // and so does any aggregate once the viewer's view may lack pieces or values (a map it cannot see, a Hideable or Obscurable trait).
        if (FormulaVariables.AggregateReadsHidden(pack, f, PieceAccess.PiecesMayHide(pack, model, sides))) hidden.Add(AggregateCallInput);
        // lookup() of a table a non-Public property overrides reads the run piece's value, as a Table binding does.
        // Cheapest first: a pack with no hidden override (most packs) never parses the formula's expressions for lookup() calls.
        if (Quartermaster.Rules.Tables.CellOverrideFacts.AnyHidden(pack) && RunPieceHidden()
            && FormulaVariables.TablesRead(f).Any(t => FormulaVariables.LookupHidden(pack, t))) hidden.Add(TableCallInput);
        // B6: keyed(P, key[, H]) reads a keyed value as PlayerView and keyed() on a view decide; a place output's working shows its Before.
        // Packs without keyed properties (most packs) never parse the formula for keyed() calls.
        if (props.Any(KeyedFacts.IsKeyed))
            AddKeyedHidden(pack, f, hidden, pieceId, UnitHidden, HolderHidden);
        // A computed property built from hidden data shows that data wherever it is read: through a binding of any kind (the viewer's own
        // unit and the global holder included) or a variable, which is hidden itself, and through holder(H, P), which hides like a hidden holder.
        hidden.UnionWith(FormulaVariables.ComputedHiddenVariables(pack, f));
        // A picked piece's non-Public property: the pick is the viewer's own, its value is the host's (the cheapest safe rule).
        // A masked pick (any property) once the viewer's view may lack pieces or values: the same static rule as the aggregates.
        var mayHide = PieceAccess.PiecesMayHide(pack, model, sides);
        hidden.UnionWith(FormulaVariables.PieceBindingsHidden(pack, f, mayHide));
        if (FormulaVariables.ComputedHolderCallHidden(pack, f)) hidden.Add(HolderCallInput);
        // An output acting on a picked piece (B3): its working and errors show the target's values, name and layer levels. Hidden when an
        // output writes a non-Public property, or (the viewer's view may lack pieces or values) the formula has any Piece output, write or effect.
        var pieceOutputs = (f.Outputs ?? new List<FormulaOutput>()).Where(o => o is { Target: OutputTarget.Piece }).ToList();
        if (pieceOutputs.Count > 0 && (mayHide || pieceOutputs.Any(o => o.Action == OutputAction.Write && PropPrivate(o.Property))))
            hidden.Add(PieceOutputInput);
        if (hidden.Count > 0)
            foreach (var b in f.Bindings ?? new List<VariableBinding>())
                if (b is { Kind: BindingKind.Table, Variable: not null }) hidden.Add(b.Variable);
        AddDerivedHidden(f, hidden);
        return hidden;
    }

    /// <summary>
    /// B7, the last step of <see cref="HiddenVariables"/>: a fixpoint over the bindings. A binding of any kind but Piece whose own expressions
    /// (<see cref="FormulaVariables.Sources"/>: auto value, "Only when", dice count, row/column) read a hidden variable is hidden too, since its
    /// value would show the hidden one. A Piece binding is the viewer's own pick and stays open. An Input's "Only when" is not one of them: the
    /// Input's value is typed by the viewer, never computed from its guard, and a guard over hidden data leaves the box typeable and is the
    /// host's to decide (<see cref="GuardsOnHidden"/>); its auto value is.
    /// </summary>
    private static void AddDerivedHidden(FormulaDef f, HashSet<string> hidden)
    {
        if (hidden.Count == 0) return;
        var reads = new List<(string Variable, IReadOnlyList<string> Reads)>();
        foreach (var b in f.Bindings ?? new List<VariableBinding>())
        {
            if (b?.Variable == null || b.Kind == BindingKind.Piece || hidden.Contains(b.Variable)) continue;
            var vars = new List<string>();
            // An Input's only derived expression is its auto value (see above).
            var sources = b.Kind == BindingKind.Input ? new[] { b.AutoValue } : FormulaVariables.Sources(b).ToArray<string?>();
            foreach (var src in sources)
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                try { vars.AddRange(Parser.Variables(Parser.Parse(src))); }
                catch (ParseError) { }   // never runs
            }
            if (vars.Count > 0) reads.Add((b.Variable, vars));
        }
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (variable, vars) in reads)
                if (!hidden.Contains(variable) && vars.Any(hidden.Contains)) changed |= hidden.Add(variable);
        }
    }

    /// <summary>
    /// B6: adds <see cref="KeyedCallInput"/> when any keyed() read of the formula may be hidden from the viewer, every binding whose own
    /// expressions (auto value, row/column, "Only when", dice count) make such a read (its value would show it), and
    /// <see cref="PlaceOutputInput"/> when an output writes a place that is not Public. The rule is the one of <see cref="PlayerView"/> and
    /// keyed() on a view: a place that is not Public is hidden from every viewer not holding every side; a named holder hides as a Holder
    /// binding does; a run piece's value hides as a Unit binding does; an unknown property or holder fails closed.
    /// </summary>
    private static void AddKeyedHidden(RulesPack pack, FormulaDef f, HashSet<string> hidden, string? pieceId,
        Func<string?, bool> unitHidden, Func<string?, PropertyDef?, bool> holderHidden)
    {
        bool PrivatePlace(PropertyDef? p) => KeyedFacts.IsPlace(p) && !PropertyFacts.IsPublic(pack, p);
        bool ReadHidden((string Property, string? Holder, Quartermaster.Rules.Expr.Expr Key) r)
        {
            if (pack.PropertyByName(r.Property ?? "") is not { } def) return true;   // fail closed
            if (PrivatePlace(def)) return true;
            if (r.Holder != null) return pack.HolderByName(r.Holder) is not { } h || holderHidden(h.Id, def);
            if (def.Scope == PropertyScope.Pieces || (def.Scope == PropertyScope.Both && pieceId != null)) return unitHidden(def.Id);   // the run piece's value
            return false;   // the Global holder's value of a Public or Private non-place property: public, as a Global binding
        }
        bool SourceHidden(string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return false;
            try { return ExprFacts.KeyedReads(Parser.Parse(src)).Any(ReadHidden); }
            catch (ParseError) { return false; }   // never runs
        }
        // A Piece binding's filter or an Input's choices filter reads each candidate (not the run piece): any keyed() read of a value that is
        // not Public or of a named holder may be hidden there (fails closed, as an aggregate's filter does).
        bool FilterHidden(string? src)
        {
            if (string.IsNullOrWhiteSpace(src)) return false;
            try
            {
                return ExprFacts.KeyedReads(Parser.Parse(src)).Any(k => k.Holder != null
                    || pack.PropertyByName(k.Property ?? "") is not { } d || !PropertyFacts.IsPublic(pack, d));
            }
            catch (ParseError) { return false; }   // never runs
        }
        if (FormulaVariables.KeyedReads(f).Any(ReadHidden)
            || (f.Bindings ?? new List<VariableBinding>()).Any(b => b != null && (FilterHidden(b.PieceFilter) || FilterHidden(b.ChoicesFilter))))
            hidden.Add(KeyedCallInput);
        if (hidden.Contains(KeyedCallInput))
            foreach (var b in f.Bindings ?? new List<VariableBinding>())
                if (b?.Variable != null && FormulaVariables.Sources(b).Any(SourceHidden))
                    hidden.Add(b.Variable);
        var props = pack.Properties ?? new List<PropertyDef>();
        if ((f.Outputs ?? new List<FormulaOutput>()).Any(o => o is { Action: OutputAction.Write, Property: not null }
                && PrivatePlace(props.FirstOrDefault(p => p != null && p.Id == o.Property))))
            hidden.Add(PlaceOutputInput);
    }

    /// <summary>The entry <see cref="GuardsOnHidden"/> adds when the formula's precondition depends on hidden data (not a variable name).</summary>
    public const string PreconditionGuard = "(precondition)";

    /// <summary>
    /// The bindings whose "Only when" condition depends on data the viewer may not see, plus <see cref="PreconditionGuard"/> when the
    /// precondition does: the condition reads a <paramref name="hidden"/> variable, or one whose own guard, auto value or table key does
    /// (transitively), or (when <see cref="HolderCallInput"/> is hidden) any holder(H, P) call along the way, or (when
    /// <see cref="AggregateCallInput"/> is hidden) any piece aggregate along the way, or (when <see cref="TableCallInput"/> is hidden) any lookup() call,
    /// or (when <see cref="PieceOutputInput"/> is hidden) any Piece variable a Piece output acts on. A viewer without the data cannot decide such a guard,
    /// so an online guest's dialog leaves it to the host. Conservative: any holder() call, aggregate or lookup() counts once one is hidden.
    /// Empty when nothing is hidden.
    /// </summary>
    public static HashSet<string> GuardsOnHidden(FormulaDef f, IReadOnlySet<string> hidden)
    {
        var guards = new HashSet<string>(StringComparer.Ordinal);
        if (f == null || hidden == null || hidden.Count == 0) return guards;
        var holderHidden = hidden.Contains(HolderCallInput);
        var aggregateHidden = hidden.Contains(AggregateCallInput);
        var tableHidden = hidden.Contains(TableCallInput);
        var keyedHidden = hidden.Contains(KeyedCallInput);
        // A Piece output whose data may be hidden: the variables naming its target are read on the host (a guest's view may lack the target or
        // its values), so a guard reaching one is the host's to decide, as a guard reaching a holder() call is when HolderCallInput is hidden.
        var targetVars = hidden.Contains(PieceOutputInput)
            ? new HashSet<string>((f.Outputs ?? new List<FormulaOutput>()).Where(o => o is { Target: OutputTarget.Piece, PieceVariable: not null })
                .Select(o => o.PieceVariable!), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        bool HasHiddenCall(string? src)
        {
            if (string.IsNullOrWhiteSpace(src) || !(holderHidden || aggregateHidden || tableHidden || keyedHidden)) return false;
            try
            {
                var e = Parser.Parse(src);
                return (holderHidden && ExprFacts.HolderReads(e).Count > 0) || (aggregateHidden && ExprFacts.AggregateReads(e).Count > 0)
                    || (tableHidden && ExprFacts.LookupTables(e).Count > 0) || (keyedHidden && ExprFacts.KeyedReads(e).Count > 0);
            }
            catch (ParseError) { return false; }
        }
        bool Reaches(string? cond)
        {
            if (string.IsNullOrWhiteSpace(cond)) return false;
            IReadOnlyList<string> vars;
            try { vars = Parser.Variables(Parser.Parse(cond)); }
            catch (ParseError) { return false; }   // never runs
            if (HasHiddenCall(cond)) return true;
            foreach (var v in FormulaVariables.Reach(f, vars))
            {
                if (hidden.Contains(v) || targetVars.Contains(v)) return true;
                if ((f.Bindings ?? new List<VariableBinding>()).FirstOrDefault(b => b?.Variable == v) is { } b && FormulaVariables.Sources(b).Any(HasHiddenCall))
                    return true;
            }
            return false;
        }
        foreach (var b in f.Bindings ?? new List<VariableBinding>())
            if (b?.Variable != null && Reaches(b.When)) guards.Add(b.Variable);
        if (Reaches(f.Precondition)) guards.Add(PreconditionGuard);
        return guards;
    }

    /// <summary>
    /// Online guest: sends the dialog's typed values (or Calculate) to the host through the port, waits for its verdict and shows it on
    /// the model. Nothing is computed locally from hidden data; the verdict carries states, never values.
    /// </summary>
    /// <param name="preview">The guest's Calculate: the host only runs it and sends back the visible values and its (pinned) dice.</param>
    public static async Task<FormulaVerdict> SendAsync(IActionPort port, FormulaDialogModel m, string? runId, bool calculate, bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(m);
        var verdict = await port.ApplyFormulaAsync(m.Intent(runId, calculate, preview)).ConfigureAwait(true)
            ?? FormulaVerdict.Refused(GuestSession.NoAnswerText);
        m.ApplyVerdict(verdict);
        return verdict;
    }
}
