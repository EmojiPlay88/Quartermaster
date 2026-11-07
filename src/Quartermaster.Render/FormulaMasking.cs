using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// Formula input masking (spec 8a.2): which inputs of a formula come from data the viewer may not see, and how the formula dialog, the
/// batch grid and the cheat sheet show them. The one source of truth for the host (<see cref="FormulaIntentRunner"/>) and the local
/// dialogs, so the boxes the host reports as hidden are exactly the boxes the dialog masks.
/// <para>A formula reads another side's data only through its bindings: a Unit binding reads the subject piece, a Holder binding a holder's
/// value (Global is public, tables are pack data). An input is hidden when it reads a private property of a piece the viewer cannot see or
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
        if (hidden.Count > 0)
            foreach (var b in f.Bindings!)
                if (b is { Kind: BindingKind.Table, Variable: not null }) hidden.Add(b.Variable);
        return hidden;
    }

    /// <summary>
    /// Online guest: sends the dialog's typed values (or Calculate) to the host through the port, waits for its verdict and shows it on
    /// the model. Nothing is computed locally from hidden data; the verdict carries states, never values.
    /// </summary>
    public static async Task<FormulaVerdict> SendAsync(IActionPort port, FormulaDialogModel m, string? runId, bool calculate)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(m);
        var verdict = await port.ApplyFormulaAsync(m.Intent(runId, calculate)).ConfigureAwait(true)
            ?? FormulaVerdict.Refused(GuestSession.NoAnswerText);
        m.ApplyVerdict(verdict);
        return verdict;
    }
}
