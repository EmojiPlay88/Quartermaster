using Quartermaster.Rules;

namespace Quartermaster.Core;

/// <summary>
/// The one rule deciding whether a viewer may see a value (B7). Every check of "may this player see this holder, piece or place value"
/// calls this class; none keeps its own copy. Callers: <see cref="PlayerView"/> (what a view keeps), <see cref="KeyedValues"/>
/// (<c>keyed()</c> in a view, <see cref="KeyedValues.HiddenIn"/>, <see cref="KeyedValues.HiddenFor"/>), <see cref="PieceFilter"/>
/// (<c>holder()</c> in a view), <see cref="PropertyValues"/> (computed piece and holder values in a view), <see cref="CellOverrides"/> (a
/// piece's cell override in a view), <see cref="FormulaRunner"/> (a picked piece's value, a Piece output target and a Reveal's value in a view), and in Render
/// the formula masking, the alert bar and the guest's snapshot (a game-over snapshot is the full state).
/// <para>A viewer is described by the sides it holds: <c>holds(sideId)</c>, or the held set of <see cref="SeesEverything"/>. Widening what
/// a viewer holds (for example a side's allies) widens what it sees, everywhere at once.</para>
/// <para>These are information rules only. What a player may change (<c>PieceAccess</c>, a session's ledger edit right, a guest intent's
/// rule) is decided elsewhere and is never widened by this class.</para>
/// </summary>
public static class HiddenValues
{
    /// <summary>
    /// True when the viewer sees everything, so no hiding rule applies: without a pack, for a null <paramref name="held"/> (solo), when every
    /// side of the pack is held, and when the game is over (<see cref="GameState.GameOver"/>; B7 #73: the end of the game reveals every
    /// value, never any right to act).
    /// </summary>
    public static bool SeesEverything(GameState? full, RulesPack? pack, IReadOnlySet<string>? held)
    {
        if (pack == null || held == null) return true;
        if (full?.GameOver != null) return true;
        foreach (var side in pack.Sides ?? new List<SideDef>())
            if (side != null && !held.Contains(side.Id)) return false;
        return true;
    }

    /// <summary>
    /// True when the value of <paramref name="property"/> (null: the whole ledger key) kept by the holder <paramref name="holderId"/> is
    /// hidden from a viewer holding the sides <paramref name="holds"/> accepts. In this order: a place property (keyed by hex) that is not
    /// Public is hidden; the Global holder's values are seen; a holder missing from the pack is hidden (fail closed); a holder of no (existing)
    /// side is seen; a held owner's values are seen; the whole key of another side's holder is hidden; else the value is hidden unless the
    /// property is Public (B7 #74a: a Public value of another side's holder is seen).
    /// </summary>
    public static bool HolderValueHidden(RulesPack pack, string holderId, PropertyDef? property, Func<string, bool> holds) =>
        HolderValueHidden(pack, holderId, property, holds, null);

    /// <summary><see cref="HolderValueHidden(RulesPack, string, PropertyDef?, Func{string, bool})"/> with the caller's memo of
    /// <see cref="PropertyFacts.IsPublic(RulesPack, PropertyDef?)"/> (a player view decides it once per property).</summary>
    internal static bool HolderValueHidden(RulesPack pack, string holderId, PropertyDef? property, Func<string, bool> holds,
        Func<PropertyDef, bool>? isPublic)
    {
        if (PrivatePlace(pack, property, isPublic)) return true;
        if (holderId == HolderDef.GlobalId) return false;
        if (!HolderExists(pack, holderId)) return true;
        if (Ownership.SideOfHolder(holderId, pack) is not { } owner) return false;
        if (holds(owner)) return false;
        if (property == null) return true;
        return !Public(pack, property, isPublic);
    }

    /// <summary>
    /// True when the value of <paramref name="property"/> (null: the whole ledger key) of a piece of side <paramref name="pieceSide"/> (null:
    /// no side) is hidden from a viewer holding the sides <paramref name="holds"/> accepts. In this order: a place property that is not Public
    /// is hidden; a piece of no side or of a held side is seen; the whole key of another side's piece is hidden; else the value is hidden
    /// unless the property is Public. A piece that is removed, masked or deleted hides every value; the callers check that first.
    /// </summary>
    public static bool PieceValueHidden(RulesPack pack, PropertyDef? property, string? pieceSide, Func<string, bool> holds) =>
        PieceValueHidden(pack, property, pieceSide, holds, null);

    /// <summary><see cref="PieceValueHidden(RulesPack, PropertyDef?, string?, Func{string, bool})"/> with the caller's memo of
    /// <see cref="PropertyFacts.IsPublic(RulesPack, PropertyDef?)"/>.</summary>
    internal static bool PieceValueHidden(RulesPack pack, PropertyDef? property, string? pieceSide, Func<string, bool> holds,
        Func<PropertyDef, bool>? isPublic)
    {
        if (PrivatePlace(pack, property, isPublic)) return true;
        if (pieceSide == null || holds(pieceSide)) return false;
        if (property == null) return true;
        return !Public(pack, property, isPublic);
    }

    /// <summary>
    /// <see cref="PieceValueHidden(RulesPack, PropertyDef?, string?, Func{string, bool})"/> for <paramref name="piece"/>, whose side is read
    /// from <paramref name="sides"/> only when it matters: a Public property is seen whatever the side (the rule gives false for it either
    /// way), so a Public read never evaluates the sides' unit filters.
    /// </summary>
    internal static bool PieceValueHidden(RulesPack pack, PropertyDef? property, PieceInstance piece, Lazy<Func<PieceInstance, string?>> sides,
        Func<string, bool> holds)
    {
        if (PrivatePlace(pack, property, null)) return true;
        if (property != null && PropertyFacts.IsPublic(pack, property)) return false;
        return PieceValueHidden(pack, property, sides.Value(piece), holds, null);
    }

    /// <summary>
    /// <see cref="PieceValueHidden(RulesPack, PropertyDef?, string?, Func{string, bool})"/> for a piece whose side is not known (a deleted
    /// piece, or a piece read inside a side's own unit filter): a side no viewer holds, so only a Public value is seen (fail closed).
    /// </summary>
    internal static bool UnknownSideValueHidden(RulesPack pack, PropertyDef? property, Func<PropertyDef, bool>? isPublic = null) =>
        PieceValueHidden(pack, property, "", NoneHeld, isPublic);

    /// <summary>The sides a player view holds (<see cref="GameState.ViewSides"/>), as the rules above take them; a view without sides holds none.</summary>
    internal static Func<string, bool> HeldBy(GameState view) => side => view.ViewSides?.Contains(side) == true;

    private static readonly Func<string, bool> NoneHeld = _ => false;

    private static bool Public(RulesPack pack, PropertyDef property, Func<PropertyDef, bool>? isPublic) =>
        isPublic?.Invoke(property) ?? PropertyFacts.IsPublic(pack, property);

    /// <summary>A place property (keyed by hex; <see cref="KeyedFacts.IsPlace"/>) that is not Public: only a full view has its values.</summary>
    private static bool PrivatePlace(RulesPack pack, PropertyDef? property, Func<PropertyDef, bool>? isPublic) =>
        property != null && KeyedFacts.IsPlace(property) && !Public(pack, property, isPublic);

    private static bool HolderExists(RulesPack pack, string holderId)
    {
        foreach (var h in pack.Holders ?? new List<HolderDef>())
            if (h != null && h.Id == holderId) return true;
        return false;
    }
}
