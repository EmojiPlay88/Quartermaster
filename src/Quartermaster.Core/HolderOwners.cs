using System.Runtime.CompilerServices;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Core;

/// <summary>
/// A holder's owner as play goes on (B9 #13a): its <see cref="HolderDef.OwnerExpr"/> evaluated against a game state. The expression reads
/// only Public values (the validator checks it), straight from the state. A keyed() or computed read may pass through
/// <see cref="HiddenValues"/> on a player view, but a Public value returns before any owner is asked for, so those checks never hide anything:
/// the full state and every player view give the same owner and owner resolution never recurses into visibility. A text names the side whose name
/// or id equals it (trimmed, case-insensitive; a former id reads as its listing side), "" no owner; a whole number N from 1 to the number
/// of sides the Nth side, 0 no owner. Anything else (an evaluation error, a text naming no side, a number out of range or not whole) falls
/// back to the fixed owner (<see cref="HolderDef.SideId"/>, a dangling id = no owner), as does a holder without an expression or no state.
/// Callers ask <see cref="Ownership.SideOfHolder(string, RulesPack, GameState?)"/> or <see cref="Ownership.HolderSides"/>, never this class.
/// </summary>
public static class HolderOwners
{
    /// <summary>True when some holder of the pack has an owner expression.</summary>
    public static bool HasDynamic(RulesPack? pack)
    {
        if (pack?.Holders == null) return false;
        foreach (var h in pack.Holders)
            if (h != null && !string.IsNullOrWhiteSpace(h.OwnerExpr)) return true;
        return false;
    }

    /// <summary>The holder's owner in <paramref name="s"/> (see the class remarks); null = no owner (public).</summary>
    public static string? Owner(GameState? s, RulesPack pack, HolderDef h)
    {
        if (h == null || pack == null) return null;
        var fixedOwner = Ownership.Existing(h.SideId, pack);
        if (s == null || string.IsNullOrWhiteSpace(h.OwnerExpr) || pack.Sides is not { Count: > 0 }) return fixedOwner;
        if (h.Id == HolderDef.GlobalId) return null;
        _active ??= new HashSet<string>(StringComparer.Ordinal);
        if (!_active.Add(h.Id ?? "")) return fixedOwner;   // a guard only: an expression of Public values never comes back here
        try
        {
            Evaluations++;
            var at = s.Position ?? new PhasePosition(s.Turn.Turn, 1, "", -1);
            var v = Evaluator.Evaluate(Parse(h), new OwnerContext(pack, s, at, h));
            return Named(pack, v, out var side) ? side : fixedOwner;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return fixedOwner; }   // never throws
        finally { _active.Remove(h.Id ?? ""); }
    }

    /// <summary>How many owner expressions were evaluated on this thread (tests read it).</summary>
    [ThreadStatic] internal static int Evaluations;

    [ThreadStatic] private static HashSet<string>? _active;

    /// <summary>The parsed owner expression of each holder record (records are replaced on edit), or the <see cref="ParseError"/> it gave.</summary>
    private static readonly ConditionalWeakTable<HolderDef, object> ParseCache = new();

    private static AstExpr Parse(HolderDef h)
    {
        var hit = ParseCache.GetValue(h, d =>
        {
            try { return Parser.Parse(d.OwnerExpr!); }
            catch (ParseError pe) { return pe; }
        });
        return hit is ParseError err ? throw new ParseError(err.Position, err.Detail) : (AstExpr)hit;
    }

    /// <summary>True when <paramref name="v"/> names an owner: <paramref name="side"/> is its id, or null for no owner.</summary>
    private static bool Named(RulesPack pack, Value v, out string? side)
    {
        side = null;
        var sides = pack.Sides;
        if (v.Kind == ValueKind.Number)
        {
            var n = v.Number;
            if (!double.IsFinite(n) || n != Math.Floor(n) || n < 0 || n > sides.Count) return false;
            side = n == 0 ? null : sides[(int)n - 1]?.Id;
            return n == 0 || !string.IsNullOrEmpty(side);
        }
        if (v.Kind != ValueKind.Text) return false;
        var t = (v.Text ?? "").Trim();
        if (t.Length == 0) return true;
        foreach (var sd in sides)
            if (sd != null && !string.IsNullOrEmpty(sd.Id)
                && (string.Equals(sd.Id, t, StringComparison.OrdinalIgnoreCase) || string.Equals(sd.Name?.Trim(), t, StringComparison.OrdinalIgnoreCase)))
            { side = sd.Id; return true; }
        if (SideFacts.FormerMap(pack) is { } former)
            foreach (var (from, to) in former)
                if (string.Equals(from, t, StringComparison.OrdinalIgnoreCase)) { side = to; return true; }
        return false;
    }

    /// <summary>
    /// The phase-condition context at the state's position, with two changes: a bare property name reads this holder's value when the holder
    /// keeps the property (its Default when unset), else the global holder's (an error when Global does not keep it either, so the owner is
    /// the fixed one); piece aggregates are refused.
    /// </summary>
    private sealed class OwnerContext : PhaseConditions.PhaseContext
    {
        private readonly RulesPack pack;
        private readonly GameState s;
        private readonly PhasePosition at;
        private readonly HolderDef holder;

        public OwnerContext(RulesPack pack, GameState s, PhasePosition at, HolderDef holder) : base(pack, s, at, null)
        {
            this.pack = pack; this.s = s; this.at = at; this.holder = holder;
        }

        public override Value GetVariable(string name)
        {
            var def = pack.PropertyByName(name) ?? throw new EvalError($"Unknown property '{name}'");
            // B9 final review I2: a holder property kept by neither this holder nor Global has no value to read here (not its Default):
            // the owner falls back to the fixed one
            if (def.Scope != PropertyScope.Pieces && !PropertyFacts.KeptBy(def, holder.Id) && !PropertyFacts.KeptBy(def, HolderDef.GlobalId))
                throw new EvalError($"{name} is kept by neither this holder nor the global holder");
            if (def.Scope == PropertyScope.Pieces || !PropertyFacts.KeptBy(def, holder.Id)) return base.GetVariable(name);
            if (def.KeyedBy != null && KeyedFacts.IsKeyed(def)) throw KeyedValues.BareRead(def);
            return Value.Of(PropertyValues.GetOrDefault(s, pack, null, LedgerKey.Holder(holder.Id), def,
                PropertyFacts.IsComputed(def) ? new AggregateFilters(s, pack, null) { At = at } : null));
        }

        public override Value Aggregate(string fn, string? property, string? filter) =>
            throw new EvalError($"{fn}() is not available in an owner expression");
    }
}
