using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules.Expr;

/// <summary>A user-caused evaluation problem (never a crash).</summary>
public sealed class EvalError : Exception
{
    public AstExpr? At { get; }
    public EvalError(string message, AstExpr? at = null) : base(message) => At = at;
}

/// <summary>Everything the evaluator needs from the outside world.</summary>
public interface IEvalContext
{
    /// <summary>Throws <see cref="EvalError"/> when the value is missing.</summary>
    Value GetVariable(string name);
    Value Lookup(string tableName, Value row, Value? col);
    IReadOnlyList<int> RollDice(int n, int sides);
    bool Has(string property);
    Value PieceName();
    Value Marker(string key);
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
            case "marker": return ctx.Marker(TextArg(a[0], ctx, "marker"));
            default: throw new EvalError($"Unknown function '{c.Function}'", c);
        }
    }
}
