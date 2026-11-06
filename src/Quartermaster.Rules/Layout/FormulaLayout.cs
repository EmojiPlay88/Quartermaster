using System.Text;
using Quartermaster.Rules.Expr;
using AstExpr = Quartermaster.Rules.Expr.Expr;

namespace Quartermaster.Rules;

public abstract record LayoutItem;
public sealed record TextItem(string Text) : LayoutItem;
public sealed record BoxItem(string Variable, string Caption, bool Auto) : LayoutItem;
public sealed record MirrorItem(string Variable) : LayoutItem;
public sealed record SubRow(string Label, IReadOnlyList<LayoutItem> Items) : LayoutItem;

public sealed record FormulaLayoutModel(IReadOnlyList<LayoutItem> Main, IReadOnlyList<SubRow> SubRows);

/// <summary>Builds the "layout C" model: formula text with a box per variable and sub-rows for nested lookups.</summary>
public static class FormulaLayout
{
    public static FormulaLayoutModel Build(FormulaDef f, RulesPack pack)
    {
        try { return Build(f, Parser.Parse(f.Text ?? ""), pack); }
        catch (ParseError) { return Raw(f.Text ?? ""); }
    }

    public static FormulaLayoutModel Build(FormulaDef f, AstExpr ast, RulesPack pack)
    {
        try
        {
            var ctx = new Ctx(f, pack);
            var main = ctx.Layout(ast, f.Text ?? "");
            while (ctx.Pending.Count > 0)
            {
                var b = ctx.Pending.Dequeue();
                var t = pack.ById<TableDef>(b.TableId ?? "");
                var label = $"{b.Variable} = {t?.Name ?? b.TableId ?? "?"}[…]";
                ctx.SubRows.Add(new SubRow(label, ctx.LayoutTableArgs(b)));
            }
            return new FormulaLayoutModel(main, ctx.SubRows);
        }
        catch (Exception) { return Raw(f.Text ?? ""); }
    }

    private static FormulaLayoutModel Raw(string text) =>
        new(new LayoutItem[] { new TextItem(text) }, Array.Empty<SubRow>());

    private static string PropName(VariableBinding b, RulesPack pack) =>
        pack.ById<PropertyDef>(b.Property ?? "")?.Name ?? b.Property ?? "?";

    private static string DefaultCaption(VariableBinding b, RulesPack pack)
    {
        if (!string.IsNullOrWhiteSpace(b.Caption)) return b.Caption!;
        switch (b.Kind)
        {
            case BindingKind.Unit: return $"{PropName(b, pack)} (unit)";
            case BindingKind.Global: return $"{PropName(b, pack)} (global)";
            case BindingKind.Holder:
            {
                var h = pack.ById<HolderDef>(b.HolderId ?? "");
                var name = h?.Name ?? (b.HolderId == HolderDef.GlobalId ? "global" : b.HolderId ?? "?");
                return $"{PropName(b, pack)} ({name})";
            }
            case BindingKind.Table: return pack.ById<TableDef>(b.TableId ?? "")?.Name ?? b.TableId ?? b.Variable;
            case BindingKind.Location: return "location";
            case BindingKind.Input:
                return b.Min is { } lo && b.Max is { } hi
                    ? $"{b.Variable} {Value.FormatNumber(lo)}–{Value.FormatNumber(hi)}"
                    : b.Variable;
            case BindingKind.Roll: return $"{b.DiceCount ?? 1}d{b.DiceSides ?? 6}";
            default: return b.Variable;
        }
    }

    private sealed class Ctx
    {
        private readonly FormulaDef _f;
        private readonly RulesPack _pack;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public readonly List<SubRow> SubRows = new();
        public readonly Queue<VariableBinding> Pending = new();

        public Ctx(FormulaDef f, RulesPack pack) { _f = f; _pack = pack; }

        public List<LayoutItem> Layout(AstExpr e, string src)
        {
            var em = new Emitter();
            Write(e, src, em);
            return em.Done();
        }

        public List<LayoutItem> LayoutTableArgs(VariableBinding b)
        {
            var em = new Emitter();
            AddArg(em, b.RowExpr);
            if (!string.IsNullOrWhiteSpace(b.ColExpr))
            {
                em.Text(", ");
                AddArg(em, b.ColExpr);
            }
            return em.Done();
        }

        private void AddArg(Emitter em, string? text)
        {
            text ??= "";
            try { Write(Parser.Parse(text), text, em); }
            catch (ParseError) { em.Text(text); }
        }

        private void Variable(string name, Emitter em)
        {
            if (!_seen.Add(name)) { em.Item(new MirrorItem(name)); return; }
            var b = _f.Bindings.FirstOrDefault(x => x.Variable == name);
            if (b is null) { em.Item(new BoxItem(name, name, false)); return; }
            em.Item(new BoxItem(name, DefaultCaption(b, _pack), b.Kind != BindingKind.Input));
            if (b.Kind == BindingKind.Table
                && (!string.IsNullOrWhiteSpace(b.RowExpr) || !string.IsNullOrWhiteSpace(b.ColExpr)))
                Pending.Enqueue(b);
        }

        private void Args(IReadOnlyList<AstExpr> args, int from, string src, Emitter em)
        {
            for (int i = from; i < args.Count; i++)
            {
                if (i > from) em.Text(", ");
                Write(args[i], src, em);
            }
        }

        private void Write(AstExpr e, string src, Emitter em)
        {
            bool paren = WorkingLine.Parenthesised(e, src);
            if (paren) em.Text("(");
            switch (e)
            {
                case NumberLit n: em.Text(Value.FormatNumber(n.Value)); break;
                case TextLit t: em.Text("\"" + t.Value + "\""); break;
                case BoolLit b: em.Text(b.Value ? "true" : "false"); break;
                case VarRef v: Variable(v.Name, em); break;
                case Unary u:
                    em.Text(u.Op == "-" ? "−" : "not ");
                    Write(u.Operand, src, em);
                    break;
                case Binary b:
                    Write(b.Left, src, em);
                    em.Text(" " + WorkingLine.Symbol(b.Op) + " ");
                    Write(b.Right, src, em);
                    break;
                case Call c when c.Function == "lookup" && c.Args.Count > 0 && c.Args[0] is VarRef tv:
                {
                    var label = tv.Name + "[…]";
                    em.Text(label);
                    var sub = new Emitter();
                    Args(c.Args, 1, src, sub);
                    SubRows.Add(new SubRow(label, sub.Done()));
                    break;
                }
                case Call c when c.Function == "if":
                {
                    em.Text("if(…)");
                    var sub = new Emitter();
                    Args(c.Args, 0, src, sub);
                    SubRows.Add(new SubRow("if", sub.Done()));
                    break;
                }
                case Call c:
                    em.Text(c.Function + "(");
                    Args(c.Args, 0, src, em);
                    em.Text(")");
                    break;
            }
            if (paren) em.Text(")");
        }
    }

    private sealed class Emitter
    {
        private readonly List<LayoutItem> _items = new();
        private readonly StringBuilder _sb = new();

        public void Text(string s) => _sb.Append(s);
        public void Item(LayoutItem i) { Flush(); _items.Add(i); }
        private void Flush() { if (_sb.Length > 0) { _items.Add(new TextItem(_sb.ToString())); _sb.Clear(); } }
        public List<LayoutItem> Done() { Flush(); return _items; }
    }
}
