namespace Quartermaster.Rules.Expr;

/// <summary>Hand-written recursive-descent parser for the formula language.</summary>
public static class Parser
{
    private const int MaxDepth = 200;
    private const int MaxLength = 10_000;

    // name -> (min, max); max -1 = unlimited
    private static readonly Dictionary<string, (int Min, int Max)> Arity = new()
    {
        ["if"] = (3, 3),
        ["min"] = (2, -1),
        ["max"] = (2, -1),
        ["round"] = (1, 2),
        ["floor"] = (1, 1),
        ["ceil"] = (1, 1),
        ["abs"] = (1, 1),
        ["clamp"] = (3, 3),
        ["lookup"] = (2, 3),
        ["dice"] = (2, 2),
        ["has"] = (1, 1),
        ["name"] = (0, 0),
        ["marker"] = (1, 1),
        ["startswith"] = (2, 2),
        ["contains"] = (2, 2),
    };

    public static Expr Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text)) throw new ParseError(0, "Formula is empty");
        if (text.Length > MaxLength) throw new ParseError(MaxLength, "Formula is too long");
        var p = new State(Lexer.Tokenize(text));
        var e = p.ParseOr();
        var t = p.Peek;
        if (t.Kind != TokKind.End) throw new ParseError(t.Start, $"Unexpected '{t.Text}'");
        return e;
    }

    /// <summary>Distinct variable names in first-occurrence order, excluding lookup's table-name argument.</summary>
    public static IReadOnlyList<string> Variables(Expr e)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        Walk(e);
        return list;

        void Walk(Expr n)
        {
            switch (n)
            {
                case VarRef v:
                    if (seen.Add(v.Name)) list.Add(v.Name);
                    break;
                case Unary u:
                    Walk(u.Operand);
                    break;
                case Binary b:
                    Walk(b.Left);
                    Walk(b.Right);
                    break;
                case Call c:
                    for (int i = c.Function == "lookup" ? 1 : 0; i < c.Args.Count; i++) Walk(c.Args[i]);
                    break;
            }
        }
    }

    private sealed class State
    {
        private readonly List<Token> _t;
        private int _i;
        private int _depth;

        public State(List<Token> tokens) => _t = tokens;

        public Token Peek => _t[_i];
        private Token Next() => _t[_i++];

        private bool IsOp(string op) => Peek.Kind == TokKind.Op && Peek.Text == op;
        private bool IsKw(string kw) =>
            Peek.Kind == TokKind.Ident && string.Equals(Peek.Text, kw, StringComparison.OrdinalIgnoreCase);

        private void Enter(Token at)
        {
            if (++_depth > MaxDepth) throw new ParseError(at.Start, "Formula nests too deeply");
        }

        private static int EndOf(Expr e) => e.Start + e.Length;

        // AST height per node (reference-keyed); bounded so later recursive walkers cannot overflow the stack.
        private readonly Dictionary<Expr, int> _height = new(ReferenceEqualityComparer.Instance);

        private int HeightOf(Expr e) => _height[e];

        private T Track<T>(T node, int height, Token at) where T : Expr
        {
            if (height > MaxDepth) throw new ParseError(at.Start, "Formula nests too deeply");
            _height[node] = height;
            return node;
        }

        private Expr Bin(string op, Expr left, Expr right)
        {
            var node = new Binary(op, left, right, left.Start, EndOf(right) - left.Start);
            return Track(node, 1 + Math.Max(HeightOf(left), HeightOf(right)), Peek);
        }

        private Expr Un(string op, Expr operand, Token t) =>
            Track(new Unary(op, operand, t.Start, EndOf(operand) - t.Start), 1 + HeightOf(operand), t);

        public Expr ParseOr()
        {
            Enter(Peek);
            var left = ParseAnd();
            while (IsKw("or"))
            {
                Next();
                var right = ParseAnd();
                left = Bin("or", left, right);
            }
            _depth--;
            return left;
        }

        private Expr ParseAnd()
        {
            var left = ParseNot();
            while (IsKw("and"))
            {
                Next();
                var right = ParseNot();
                left = Bin("and", left, right);
            }
            return left;
        }

        private Expr ParseNot()
        {
            if (IsKw("not"))
            {
                var t = Next();
                Enter(t);
                var operand = ParseNot();
                _depth--;
                return Un("not", operand, t);
            }
            return ParseCmp();
        }

        private static bool IsCmp(Token t) =>
            t.Kind == TokKind.Op && (t.Text is "==" or "!=" or "<" or "<=" or ">" or ">=");

        private Expr ParseCmp()
        {
            var left = ParseAdd();
            if (IsCmp(Peek))
            {
                var op = Next();
                var right = ParseAdd();
                if (IsCmp(Peek)) throw new ParseError(Peek.Start, "Comparisons can't be chained");
                return Bin(op.Text, left, right);
            }
            return left;
        }

        private Expr ParseAdd()
        {
            var left = ParseMul();
            while (IsOp("+") || IsOp("-"))
            {
                var op = Next();
                var right = ParseMul();
                left = Bin(op.Text, left, right);
            }
            return left;
        }

        private Expr ParseMul()
        {
            var left = ParseUnary();
            while (IsOp("*") || IsOp("/"))
            {
                var op = Next();
                var right = ParseUnary();
                left = Bin(op.Text, left, right);
            }
            return left;
        }

        private Expr ParseUnary()
        {
            if (IsOp("-"))
            {
                var t = Next();
                Enter(t);
                var operand = ParseUnary();
                _depth--;
                return Un("-", operand, t);
            }
            return ParsePrimary();
        }

        private Expr ParsePrimary()
        {
            var t = Peek;
            switch (t.Kind)
            {
                case TokKind.Number:
                    Next();
                    return Track(new NumberLit(t.Number, t.Start, t.Length), 1, t);
                case TokKind.String:
                    Next();
                    return Track(new TextLit(t.Text, t.Start, t.Length), 1, t);
                case TokKind.Ident:
                    return ParseIdent();
                case TokKind.Op when t.Text == "(":
                {
                    Next();
                    var inner = ParseOr();
                    if (!IsOp(")")) throw new ParseError(Peek.Start, "Expected ')'");
                    var close = Next();
                    var wrapped = inner with { Start = t.Start, Length = close.End - t.Start };
                    return Track(wrapped, HeightOf(inner), t);
                }
                case TokKind.End:
                    throw new ParseError(t.Start, "Formula ended unexpectedly");
                default:
                    throw new ParseError(t.Start, $"Unexpected '{t.Text}'");
            }
        }

        private Expr ParseIdent()
        {
            var t = Next();
            string lower = t.Text.ToLowerInvariant();
            if (lower == "true") return Track(new BoolLit(true, t.Start, t.Length), 1, t);
            if (lower == "false") return Track(new BoolLit(false, t.Start, t.Length), 1, t);
            if (lower is "and" or "or" or "not")
                throw new ParseError(t.Start, $"Unexpected '{t.Text}'");

            if (!IsOp("(")) return Track(new VarRef(t.Text, t.Start, t.Length), 1, t);

            if (!Arity.TryGetValue(lower, out var range))
                throw new ParseError(t.Start, $"Unknown function '{t.Text}'");
            Next(); // (
            var args = new List<Expr>();
            if (!IsOp(")"))
            {
                while (true)
                {
                    args.Add(ParseOr());
                    if (IsOp(",")) { Next(); continue; }
                    break;
                }
            }
            if (!IsOp(")")) throw new ParseError(Peek.Start, "Expected ')'");
            var close = Next();

            if (args.Count < range.Min || (range.Max >= 0 && args.Count > range.Max))
                throw new ParseError(t.Start, $"'{lower}' takes {Describe(range)}, not {args.Count}");
            if (lower == "lookup" && args[0] is not VarRef)
                throw new ParseError(args[0].Start, "lookup needs a table name");
            var call = new Call(lower, args, t.Start, close.End - t.Start);
            return Track(call, 1 + args.Select(HeightOf).DefaultIfEmpty(0).Max(), t);
        }

        private static string Describe((int Min, int Max) r)
        {
            if (r.Max < 0) return $"at least {r.Min} arguments";
            if (r.Min == r.Max) return r.Min == 1 ? "1 argument" : $"{r.Min} arguments";
            return $"{r.Min} to {r.Max} arguments";
        }
    }
}
