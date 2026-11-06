using System.Text;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules.Expr;

/// <summary>Renders a formula with values substituted, using math symbols.</summary>
public static class WorkingLine
{
    public static string Render(AstExpr e, string source, Func<string, Value?> values)
    {
        var sb = new StringBuilder();
        Write(e, source ?? "", values, sb);
        return sb.ToString();
    }

    internal static bool Parenthesised(AstExpr e, string src)
    {
        if (e.Start < 0 || e.Start >= src.Length || src[e.Start] != '(') return false;
        // A Binary naturally starts where its left operand starts; any wider span means grouping parens.
        if (e is Binary b) return e.Start < b.Left.Start || e.Start + e.Length > b.Right.Start + b.Right.Length;
        return true;
    }

    private static void Write(AstExpr e, string src, Func<string, Value?> values, StringBuilder sb)
    {
        bool paren = Parenthesised(e, src);
        if (paren) sb.Append('(');
        switch (e)
        {
            case NumberLit n: sb.Append(Value.FormatNumber(n.Value)); break;
            case TextLit t: sb.Append('"').Append(t.Value).Append('"'); break;
            case BoolLit b: sb.Append(b.Value ? "true" : "false"); break;
            case VarRef v:
            {
                var val = values(v.Name);
                sb.Append(val is { } x ? x.Display() : "?");
                break;
            }
            case Unary u:
                sb.Append(u.Op == "-" ? "−" : "not ");
                Write(u.Operand, src, values, sb);
                break;
            case Binary b:
                Write(b.Left, src, values, sb);
                sb.Append(' ').Append(Symbol(b.Op)).Append(' ');
                Write(b.Right, src, values, sb);
                break;
            case Call c when c.Function == "lookup":
                sb.Append(((VarRef)c.Args[0]).Name).Append('[');
                for (int i = 1; i < c.Args.Count; i++)
                {
                    if (i > 1) sb.Append(", ");
                    Write(c.Args[i], src, values, sb);
                }
                sb.Append(']');
                break;
            case Call c:
                sb.Append(c.Function).Append('(');
                for (int i = 0; i < c.Args.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Write(c.Args[i], src, values, sb);
                }
                sb.Append(')');
                break;
        }
        if (paren) sb.Append(')');
    }

    internal static string Symbol(string op) => op switch
    {
        "*" => "×",
        "/" => "÷",
        "-" => "−",
        "<=" => "≤",
        ">=" => "≥",
        "!=" => "≠",
        "==" => "=",
        _ => op,
    };
}
