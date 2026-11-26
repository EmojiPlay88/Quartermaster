using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules.Expr;

/// <summary>A user-caused evaluation problem (never a crash).</summary>
public sealed class EvalError : Exception
{
    public AstExpr? At { get; }
    /// <summary>The value read is missing (not applicable to the piece, hidden, no such marker) rather than wrong: a unit filter reads it as no match.</summary>
    public bool MissingValue { get; init; }
    /// <summary>
    /// The value exists but this player's view cannot answer it (a piece aggregate that may read hidden pieces or values): not a missing value
    /// (a filter does not read it as no match), but a formula's "Only when" condition over it is undecided, so the host decides.
    /// </summary>
    public bool HiddenData { get; init; }
    public EvalError(string message, AstExpr? at = null) : base(message) => At = at;
}

/// <summary>Everything the evaluator needs from the outside world.</summary>
public interface IEvalContext
{
    /// <summary>Throws <see cref="EvalError"/> when the value is missing.</summary>
    Value GetVariable(string name);
    Value Lookup(string tableName, Value row, Value? col);
    /// <summary>The column key of the first cell of the table row that holds the value (see <c>TableLookup.RangeColumn</c>).</summary>
    Value LookupRange(string table, Value row, Value value) => throw new EvalError("lookuprange() is not available here");
    /// <summary>The dice of a Roll binding as drawn (only the formula runner knows them).</summary>
    IReadOnlyList<int> DiceOf(string variable) => throw new EvalError("count() is not available here");
    IReadOnlyList<int> RollDice(int n, int sides);
    bool Has(string property);
    Value PieceName();
    Value Marker(string key);
    /// <summary>The level of the named layer on the piece being evaluated.</summary>
    double Layer(string name) => throw new EvalError("layer() is not available here");
    /// <summary>The current turn number (1 when the context has no turn).</summary>
    double Turn() => 1;
    /// <summary>The current stage: how many times the turn's phase span has repeated (1 when unknown).</summary>
    double Stage() => 1;
    /// <summary>The current segment: how many times the inner block of the phase span has repeated (0 outside an inner block).</summary>
    double Segment() => 0;
    /// <summary>The side of the piece being evaluated.</summary>
    Value Side() => throw new EvalError("side() is not available here");
    /// <summary>The side the current phase belongs to.</summary>
    Value PhaseSide() => throw new EvalError("phaseside() is not available here");
    /// <summary>The name of the map the piece being evaluated is on.</summary>
    Value MapName() => throw new EvalError("map() is not available here");
    /// <summary>A holder's property value, addressed by holder name and property name.</summary>
    Value HolderValue(string holder, string property) => throw new EvalError("holder() is not available here");
    /// <summary>
    /// A piece-sum: <paramref name="fn"/> is sum, countof, maxof, minof or a hex*/attached* one (lower-case); <paramref name="property"/> is the bare property name
    /// (null for countof, hexcount and attachedcount); <paramref name="filter"/> is the unit-filter text (null when a hex*/attached* call omits it; "" means every piece).
    /// </summary>
    Value Aggregate(string fn, string? property, string? filter) => throw new EvalError($"{fn}() is not available here");
    /// <summary>How many times a formula has run in a period ("stage", "turn" or "game", lower-case).</summary>
    double Runs(string formulaId, string period) => throw new EvalError("runs() is not available here");
    /// <summary>The row key being tested by a choices filter (a number when the key is numeric, else text).</summary>
    Value Row() => throw new EvalError("row() can only be used in a choices filter");
}

public static class Evaluator
{
    public static Value Evaluate(AstExpr e, IEvalContext ctx) => Eval(e, ctx);

    private static Value Eval(AstExpr e, IEvalContext ctx)
    {
        switch (e)
        {
            case NumberLit n: return Num(n.Value, e);
            case TextLit t: return Value.Of(t.Value);
            case BoolLit b: return Value.Of(b.Value);
            case VarRef v: return ctx.GetVariable(v.Name);
            case Unary u: return EvalUnary(u, ctx);
            case Binary b: return EvalBinary(b, ctx);
            case Call c: return EvalCall(c, ctx);
            default: throw new EvalError("Unsupported expression", e);
        }
    }

    private static Value Num(double d, AstExpr at)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new EvalError("Result is not a finite number", at);
        return Value.Of(d);
    }

    private static string KindName(Value v) => v.Kind switch
    {
        ValueKind.Number => "a number",
        ValueKind.Text => "text",
        _ => "true/false",
    };

    private static Value EvalUnary(Unary u, IEvalContext ctx)
    {
        var v = Eval(u.Operand, ctx);
        if (u.Op == "-")
        {
            if (v.Kind != ValueKind.Number) throw new EvalError($"'-' needs a number, got {KindName(v)}", u);
            return Num(-v.Number, u);
        }
        return Value.Of(!Truthy(v, "not", u));
    }

    private static bool Truthy(Value v, string what, AstExpr at)
    {
        if (v.Kind == ValueKind.Text) throw new EvalError($"'{what}' needs true/false or a number, got text", at);
        return v.IsTruthy;
    }

    private static Value EvalBinary(Binary b, IEvalContext ctx)
    {
        if (b.Op == "and")
        {
            if (!Truthy(Eval(b.Left, ctx), "and", b)) return Value.Of(false);
            return Value.Of(Truthy(Eval(b.Right, ctx), "and", b));
        }
        if (b.Op == "or")
        {
            if (Truthy(Eval(b.Left, ctx), "or", b)) return Value.Of(true);
            return Value.Of(Truthy(Eval(b.Right, ctx), "or", b));
        }

        var l = Eval(b.Left, ctx);
        var r = Eval(b.Right, ctx);
        if (b.Op == "==") return Value.Of(Same(l, r));
        if (b.Op == "!=") return Value.Of(!Same(l, r));

        if (l.Kind != ValueKind.Number || r.Kind != ValueKind.Number)
        {
            var bad = l.Kind != ValueKind.Number ? l : r;
            throw new EvalError($"'{b.Op}' needs numbers, got {KindName(bad)}", b);
        }
        double x = l.Number, y = r.Number;
        switch (b.Op)
        {
            case "+": return Num(x + y, b);
            case "-": return Num(x - y, b);
            case "*": return Num(x * y, b);
            case "/":
                if (y == 0) throw new EvalError("Division by zero", b);
                return Num(x / y, b);
            case "<": return Value.Of(x < y);
            case "<=": return Value.Of(x <= y);
            case ">": return Value.Of(x > y);
            case ">=": return Value.Of(x >= y);
            default: throw new EvalError($"Unknown operator '{b.Op}'", b);
        }
    }

    private static bool Same(Value l, Value r)
    {
        if (l.Kind != r.Kind) return false;
        return l.Kind switch
        {
            ValueKind.Number => l.Number == r.Number,
            ValueKind.Bool => l.Bool == r.Bool,
            _ => string.Equals(l.Text, r.Text, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static double NumArg(AstExpr arg, IEvalContext ctx, string fn)
    {
        var v = Eval(arg, ctx);
        if (v.Kind != ValueKind.Number) throw new EvalError($"'{fn}' needs numbers, got {KindName(v)}", arg);
        return v.Number;
    }

    private static int IntArg(AstExpr arg, IEvalContext ctx, string fn, string what, int min, int max)
    {
        double d = NumArg(arg, ctx, fn);
        if (d != Math.Floor(d) || d < min || d > max)
            throw new EvalError($"'{fn}' {what} must be a whole number from {min} to {max}", arg);
        return (int)d;
    }

    private static string TextArg(AstExpr arg, IEvalContext ctx, string fn)
    {
        var v = Eval(arg, ctx);
        if (v.Kind != ValueKind.Text) throw new EvalError($"'{fn}' needs text, got {KindName(v)}", arg);
        return v.Text;
    }

    /// <summary>Every function name <see cref="EvalCall"/> dispatches (lower-case; the parser matches names case-insensitively).</summary>
    public static IReadOnlyList<string> FunctionNames { get; } = new[]
    {
        "if", "min", "max", "round", "floor", "ceil", "abs", "clamp", "lookup", "lookuprange", "count",
        "dice", "has", "startswith", "contains", "name", "marker", "layer", "turn", "stage", "segment", "mod",
        "side", "phaseside", "map", "holder", "runs", "row",
        "sum", "countof", "maxof", "minof", "hexsum", "hexcount", "hexmax", "hexmin",
        "attachedsum", "attachedcount", "attachedmax", "attachedmin",
    };

    /// <summary>Every binary and unary operator the formula language accepts ('-' is both binary and unary).</summary>
    public static IReadOnlyList<string> OperatorNames { get; } = new[]
    {
        "+", "-", "*", "/", "<", "<=", ">", ">=", "==", "!=", "and", "or", "not",
    };

    private static Value EvalCall(Call c, IEvalContext ctx)
    {
        var a = c.Args;
        switch (c.Function)
        {
            case "if":
            {
                var cond = Truthy(Eval(a[0], ctx), "if", a[0]);
                return Eval(cond ? a[1] : a[2], ctx);
            }
            case "min":
            case "max":
            {
                double best = NumArg(a[0], ctx, c.Function);
                for (int i = 1; i < a.Count; i++)
                {
                    double d = NumArg(a[i], ctx, c.Function);
                    best = c.Function == "min" ? Math.Min(best, d) : Math.Max(best, d);
                }
                return Num(best, c);
            }
            case "round":
            {
                double x = NumArg(a[0], ctx, "round");
                int digits = a.Count > 1 ? IntArg(a[1], ctx, "round", "digits", 0, 10) : 0;
                return Num(Math.Round(x, digits, MidpointRounding.AwayFromZero), c);
            }
            case "floor": return Num(Math.Floor(NumArg(a[0], ctx, "floor")), c);
            case "ceil": return Num(Math.Ceiling(NumArg(a[0], ctx, "ceil")), c);
            case "abs": return Num(Math.Abs(NumArg(a[0], ctx, "abs")), c);
            case "clamp":
            {
                double x = NumArg(a[0], ctx, "clamp"), lo = NumArg(a[1], ctx, "clamp"), hi = NumArg(a[2], ctx, "clamp");
                if (lo > hi) throw new EvalError("'clamp' low limit is above the high limit", c);
                return Num(Math.Min(Math.Max(x, lo), hi), c);
            }
            case "lookup":
            {
                if (a[0] is not VarRef table) throw new EvalError("lookup needs a table name", a[0]);
                var row = Eval(a[1], ctx);
                Value? col = a.Count > 2 ? Eval(a[2], ctx) : null;
                return ctx.Lookup(table.Name, row, col);
            }
            case "lookuprange":
            {
                if (a[0] is not VarRef table) throw new EvalError("lookuprange needs a table name", a[0]);
                var row = Eval(a[1], ctx);
                return ctx.LookupRange(table.Name, row, Eval(a[2], ctx));
            }
            case "count":
            {
                if (a[0] is not VarRef roll) throw new EvalError("count needs a roll name", a[0]);
                double lo = NumArg(a[1], ctx, "count"), hi = NumArg(a[2], ctx, "count");
                int n = 0;
                foreach (var d in ctx.DiceOf(roll.Name)) if (d >= lo && d <= hi) n++;
                return Num(n, c);
            }
            case "dice":
            {
                int n = IntArg(a[0], ctx, "dice", "count", 1, 100);
                int sides = IntArg(a[1], ctx, "dice", "sides", 2, 1000);
                var rolls = ctx.RollDice(n, sides);
                double sum = 0;
                foreach (var r in rolls) sum += r;
                return Num(sum, c);
            }
            case "has":
            {
                string prop = a[0] is VarRef v ? v.Name : TextArg(a[0], ctx, "has");
                return Value.Of(ctx.Has(prop));
            }
            case "startswith":
                return Value.Of(TextArg(a[0], ctx, "startsWith").StartsWith(TextArg(a[1], ctx, "startsWith"), StringComparison.OrdinalIgnoreCase));
            case "contains":
                return Value.Of(TextArg(a[0], ctx, "contains").Contains(TextArg(a[1], ctx, "contains"), StringComparison.OrdinalIgnoreCase));
            case "name": return ctx.PieceName();
            case "turn": return Num(ctx.Turn(), c);
            case "stage": return Num(ctx.Stage(), c);
            case "segment": return Num(ctx.Segment(), c);
            case "side": return ctx.Side();
            case "phaseside": return ctx.PhaseSide();
            case "map": return ctx.MapName();
            case "holder":
            {
                if (a[0] is not VarRef h || a[1] is not VarRef hp) throw new EvalError("holder needs a holder name and a property name", c);
                return ctx.HolderValue(h.Name, hp.Name);
            }
            case "row": return ctx.Row();
            case "runs":
            {
                if (a[0] is not TextLit id) throw new EvalError("runs() needs a formula id in quotes", c);
                string period = "game";
                if (a.Count > 1)
                {
                    if (a[1] is not TextLit pl || !(pl.Value.Trim().ToLowerInvariant() is "stage" or "turn" or "game"))
                        throw new EvalError("runs() period must be \"stage\", \"turn\" or \"game\"", c);
                    period = pl.Value.Trim().ToLowerInvariant();
                }
                return Num(ctx.Runs(id.Value, period), c);
            }
            case "mod":
            {
                double x = NumArg(a[0], ctx, "mod"), y = NumArg(a[1], ctx, "mod");
                if (y == 0) throw new EvalError("mod: division by zero", c);
                return Num(x - y * Math.Floor(x / y), c);
            }
            case "sum":
            case "countof":
            case "maxof":
            case "minof":
            case "hexsum":
            case "hexcount":
            case "hexmax":
            case "hexmin":
            case "attachedsum":
            case "attachedcount":
            case "attachedmax":
            case "attachedmin":
            {
                var (property, filter) = ExprFacts.AggregateArgs(c);
                if (c.Function is not ("countof" or "hexcount" or "attachedcount") && property is null) throw new EvalError($"{c.Function} needs a property name", c);
                return ctx.Aggregate(c.Function, property, filter);
            }
            case "marker": return ctx.Marker(TextArg(a[0], ctx, "marker"));
            case "layer":
                if (a[0] is not TextLit ln) throw new EvalError("layer() needs a layer name in quotes", c);
                return Num(ctx.Layer(ln.Value), c);
            default: throw new EvalError($"Unknown function '{c.Function}'", c);
        }
    }
}
