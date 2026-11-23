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
/// the viewer's view may lack pieces or values (<see cref="PieceAccess.PiecesMayHide"/>: from the pack and module, so host and guest agree). An input is hidden when it reads a private property of a piece the viewer cannot see or
/// that belongs to a side not held, or any value of a holder owned by a side not held (or missing from the pack). The rule depends only on
/// the rules pack and the pieces, never on whether a value is set, so it gives the same answer on the host's full state and on a guest's
/// filtered snapshot. A table lookup counts as hidden when any other input is, since its keys may be computed from them.</para>
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

    private static readonly IReadOnlySet<string> NoSides = new HashSet<string>();

    /// <summary>
    /// The sides whose view the local dialogs mask for: null in solo (no masking); the viewer's sides in hot-seat and on the online host's
    /// screen; the guest's held sides (none before one is picked) for an online guest.
    /// </summary>
    public static IReadOnlySet<string>? ViewerSides(ModuleSession s) =>
        s.IsGuest ? s.GuestSides ?? NoSides : s.ViewSides;

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
        var sides = ViewerSides(s);
        m.OutputVisible = sides == null ? null : key => s.CanEditLedgerFor(key, sides);
    }

    /// <summary>
    /// The variables of <paramref name="f"/> whose value comes from data a viewer holding <paramref name="sides"/> may not see. Works on a
    /// full state and on a filtered one alike. Empty in solo (null sides) or when every side is held. Fails closed: a subject piece that is
    /// not in the state, or that the viewer cannot see, hides every Unit input.
    /// </summary>
    public static HashSet<string> HiddenVariables(RulesPack pack, ModuleModel? model, GameState state, FormulaDef f, string? pieceId,
        IReadOnlySet<string>? sides)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (sides == null || pack == null || f == null) return hidden;
        if ((pack.Sides ?? new List<SideDef>()).All(x => x == null || sides.Contains(x.Id))) return hidden;   // as PlayerView.For
        var access = new PieceAccess(state, pack, model, sides);
        bool PropPrivate(string? prop) =>
            prop == null || (pack.Properties ?? new List<PropertyDef>()).FirstOrDefault(p => p != null && p.Id == prop)?.Visibility != Visibility.Public;
        bool UnitHidden(string? prop)
        {
            if (pieceId == null) return false;   // a unit-less run has no unit values to read
            if (!state.Pieces.TryGetValue(pieceId, out var p) || p == null || !access.CanSee(pieceId)) return true;
            try { return access.NotHeld(access.SideOf(p)) && PropPrivate(prop); }
            catch (Exception) { return true; }   // fail closed
        }
        bool HolderHidden(string? holderId)
        {
            if (holderId == null || holderId == HolderDef.GlobalId) return false;
            if (!(pack.Holders ?? new List<HolderDef>()).Any(h => h != null && h.Id == holderId)) return true;   // unknown holder (as PlayerView)
            return access.NotHeld(Ownership.SideOfHolder(holderId, pack));
        }
        foreach (var b in f.Bindings ?? new List<VariableBinding>())
        {
            if (b?.Variable == null) continue;
            var isHidden = b.Kind switch
            {
                BindingKind.Unit => UnitHidden(b.Property),
                BindingKind.Holder => HolderHidden(b.HolderId),
                _ => false,   // Global is public; Input, Roll, Location and Moved are the player's own; Table is decided below
            };
            if (isHidden) hidden.Add(b.Variable);
        }
        // holder(H, P) in the formula's own expressions reads the holder as a Holder binding would; an unresolvable call fails closed.
        if (FormulaVariables.HolderReads(f).Any(r => r.Holder == null || pack.HolderByName(r.Holder) is not { } h || HolderHidden(h.Id)))
            hidden.Add(HolderCallInput);
        // Aggregates read every piece, hidden or not: a count, a non-public property or a filter over one reads data this viewer may not see,
        // and so does any aggregate once the viewer's view may lack pieces or values (a map it cannot see, a Hideable or Obscurable trait).
        if (FormulaVariables.AggregateReadsHidden(pack, f, PieceAccess.PiecesMayHide(pack, model, sides))) hidden.Add(AggregateCallInput);
        if (hidden.Count > 0)
            foreach (var b in f.Bindings ?? new List<VariableBinding>())
                if (b is { Kind: BindingKind.Table, Variable: not null }) hidden.Add(b.Variable);
        return hidden;
    }

    /// <summary>The entry <see cref="GuardsOnHidden"/> adds when the formula's precondition depends on hidden data (not a variable name).</summary>
    public const string PreconditionGuard = "(precondition)";

    /// <summary>
    /// The bindings whose "Only when" condition depends on data the viewer may not see, plus <see cref="PreconditionGuard"/> when the
    /// precondition does: the condition reads a <paramref name="hidden"/> variable, or one whose own guard, auto value or table key does
    /// (transitively), or (when <see cref="HolderCallInput"/> is hidden) any holder(H, P) call along the way, or (when
    /// <see cref="AggregateCallInput"/> is hidden) any piece aggregate along the way. A viewer without the data cannot decide such a guard,
    /// so an online guest's dialog leaves it to the host. Conservative: any holder() call, or any aggregate, counts once one is hidden.
    /// Empty when nothing is hidden.
    /// </summary>
    public static HashSet<string> GuardsOnHidden(FormulaDef f, IReadOnlySet<string> hidden)
    {
        var guards = new HashSet<string>(StringComparer.Ordinal);
        if (f == null || hidden == null || hidden.Count == 0) return guards;
        var holderHidden = hidden.Contains(HolderCallInput);
        var aggregateHidden = hidden.Contains(AggregateCallInput);
        bool HasHiddenCall(string? src)
        {
            if (string.IsNullOrWhiteSpace(src) || !(holderHidden || aggregateHidden)) return false;
            try
            {
                var e = Parser.Parse(src);
                return (holderHidden && ExprFacts.HolderReads(e).Count > 0) || (aggregateHidden && ExprFacts.AggregateReads(e).Count > 0);
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
                if (hidden.Contains(v)) return true;
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
