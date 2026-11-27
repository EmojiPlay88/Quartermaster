using System.Globalization;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// The choices of an Input box for one counter (B4). With <see cref="VariableBinding.ChoicesTable"/>: that table's row keys (normalised as
/// <see cref="KeyChoices"/> does, a repeated key once) for which <see cref="VariableBinding.ChoicesFilter"/> is true on the run piece; no
/// filter, or no run piece, gives every key. Otherwise <see cref="KeyChoices.For"/>. The filter is a unit filter (the counter's own values,
/// has(), name(), side(), lookups, aggregates) plus row(), the key tested (a number when it is numeric, else text). A run with
/// <see cref="RunRequest.PickSides"/> (hot-seat, a guest's intent on the host) judges the filter on the chooser's view, as the chooser's
/// own list is made (B7).
/// </summary>
public static class InputChoices
{
    /// <summary>A typed value that is not among this counter's choices (never names them).</summary>
    public const string NotOneText = "Not one of the choices for this piece";

    /// <summary>The filter leaves no choice for this counter.</summary>
    public const string NoChoiceText = "No choice for this piece";

    /// <summary>Prefix of a filter error on a row.</summary>
    public const string FilterErrorPrefix = "Choices filter: ";

    /// <summary>
    /// The variable's choices on <paramref name="pieceId"/>, or null when it is not an Input or offers none. A row whose filter fails is left
    /// out (the run reports the error); on a player view a row whose filter reads data the view lacks is kept (the host decides).
    /// </summary>
    public static IReadOnlyList<string>? For(GameState s, RulesPack pack, ModuleModel model, FormulaDef f, string variable, string? pieceId)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(f);
        var b = f.Bindings.FirstOrDefault(x => x.Variable == variable);
        if (b is not { Kind: BindingKind.Input }) return null;
        if (string.IsNullOrWhiteSpace(b.ChoicesTable)) return KeyChoices.For(pack, f, variable);
        if (TableKeys(pack, b.ChoicesTable) is not { } keys) return null;
        var piece = pieceId != null && s.Pieces.TryGetValue(pieceId, out var p) ? p : null;
        var sides = PieceFilter.SideMap(s, pack);
        return Filter(s, pack, b, keys, piece, sides, new AggregateFilters(s, pack, model) { SharedSides = sides }, strict: false);
    }

    /// <summary>The table's row keys, normalised and each once (ignoring case); null for an unknown table or one with range keys.</summary>
    internal static IReadOnlyList<string>? TableKeys(RulesPack pack, string tableId)
    {
        if (pack.ById<TableDef>(tableId.Trim()) is not { } t) return null;
        var res = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in t.RowKeys ?? new List<string>())
        {
            var n = TableLookup.Normalise(k ?? "");
            if (n.Length == 0) continue;
            if (TableLookup.ParseKey(n).Kind is TableKeyKind.Range or TableKeyKind.AtLeast or TableKeyKind.AtMost) return null;
            if (seen.Add(n)) res.Add(n);
        }
        return res;
    }

    /// <summary>True when the choice values are numbers (every key numeric), as for a Choices list.</summary>
    internal static bool AllNumeric(IReadOnlyList<string> keys) => keys.Count > 0 && keys.All(k => TryNum(k, out _));

    internal static bool TryNum(string s, out double d) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);

    /// <summary>
    /// The keys whose filter is true on <paramref name="piece"/> (every key without a filter or a piece). A missing value is no match (as in a
    /// unit filter); on a player view a missing or hidden value keeps the row. Another error: with <paramref name="strict"/> an
    /// <see cref="EvalError"/> "Choices filter: {message}", else the row is left out.
    /// </summary>
    internal static List<string> Filter(GameState s, RulesPack pack, VariableBinding b, IReadOnlyList<string> keys, PieceInstance? piece,
        Lazy<Func<PieceInstance, string?>> sides, AggregateFilters filters, bool strict)
    {
        if (piece == null || string.IsNullOrWhiteSpace(b.ChoicesFilter)) return keys.ToList();
        Quartermaster.Rules.Expr.Expr e;
        try { e = Parser.Parse(b.ChoicesFilter); }
        catch (ParseError pe)
        {
            if (strict) throw new EvalError(FilterErrorPrefix + pe.Message);
            return new();
        }
        var res = new List<string>();
        foreach (var k in keys)
        {
            var row = TryNum(k, out var n) ? Value.Of(n) : Value.Of(k);
            bool keep;
            try { keep = Evaluator.Evaluate(e, new PieceFilter.FilterContext(piece, s, pack, sides, filters, row: row)).IsTruthy; }
            catch (EvalError ex) when (s.IsPlayerView && (ex.HiddenData || ex.MissingValue)) { keep = true; }
            catch (EvalError ex) when (ex.MissingValue) { keep = false; }
            catch (EvalError ex)
            {
                if (strict) throw new EvalError(FilterErrorPrefix + ex.Message);
                keep = false;
            }
            if (keep) res.Add(k);
        }
        return res;
    }
}
