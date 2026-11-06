using System.Globalization;
using System.Text;

namespace Quartermaster.Rules.Expr;

internal enum TokKind { Number, String, Ident, Op, End }

internal readonly record struct Token(TokKind Kind, string Text, int Start, int Length, double Number = 0)
{
    public int End => Start + Length;
}

internal static class Lexer
{
    public static List<Token> Tokenize(string s)
    {
        var toks = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (IsDigit(c) || (c == '.' && i + 1 < s.Length && IsDigit(s[i + 1])))
            {
                int st = i;
                while (i < s.Length && IsDigit(s[i])) i++;
                if (i + 1 < s.Length && s[i] == '.' && IsDigit(s[i + 1]))
                {
                    i++;
                    while (i < s.Length && IsDigit(s[i])) i++;
                }
                string t = s.Substring(st, i - st);
                if (!double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
                    || double.IsInfinity(v))
                    throw new ParseError(st, $"Bad number '{t}'");
                toks.Add(new Token(TokKind.Number, t, st, i - st, v));
                continue;
            }

            if (IsIdentStart(c))
            {
                int st = i;
                while (i < s.Length && (IsIdentStart(s[i]) || IsDigit(s[i]))) i++;
                toks.Add(new Token(TokKind.Ident, s.Substring(st, i - st), st, i - st));
                continue;
            }

            if (c == '"')
            {
                int st = i++;
                var sb = new StringBuilder();
                bool closed = false;
                while (i < s.Length)
                {
                    char d = s[i];
                    if (d == '\\')
                    {
                        if (i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '\\'))
                        {
                            sb.Append(s[i + 1]);
                            i += 2;
                            continue;
                        }
                        throw new ParseError(i, "Unknown escape in text (use \\\" or \\\\)");
                    }
                    if (d == '"') { i++; closed = true; break; }
                    sb.Append(d);
                    i++;
                }
                if (!closed) throw new ParseError(st, "Text is missing its closing quote");
                toks.Add(new Token(TokKind.String, sb.ToString(), st, i - st));
                continue;
            }

            // Two-char operators.
            if (i + 1 < s.Length && s[i + 1] == '=' && (c == '=' || c == '!' || c == '<' || c == '>'))
            {
                toks.Add(new Token(TokKind.Op, s.Substring(i, 2), i, 2));
                i += 2;
                continue;
            }

            string? op = c switch
            {
                '+' => "+",
                '-' or '−' => "-",
                '*' or '×' => "*",
                '/' or '÷' => "/",
                '<' => "<",
                '>' => ">",
                '(' => "(",
                ')' => ")",
                ',' => ",",
                _ => null,
            };
            if (op != null)
            {
                toks.Add(new Token(TokKind.Op, op, i, 1));
                i++;
                continue;
            }

            if (c == '=') throw new ParseError(i, "Unexpected '=' (use '==' to compare)");
            if (c == '!') throw new ParseError(i, "Unexpected '!' (use 'not' or '!=')");
            throw new ParseError(i, $"Unexpected character '{c}'");
        }
        toks.Add(new Token(TokKind.End, "", s.Length, 0));
        return toks;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
    private static bool IsIdentStart(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
}
