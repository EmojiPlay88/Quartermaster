namespace Quartermaster.Rules.Expr;

/// <summary>Formula syntax tree node. Start/Length is the span in the source text.</summary>
public abstract record Expr(int Start, int Length);

public sealed record NumberLit(double Value, int Start, int Length) : Expr(Start, Length);

public sealed record TextLit(string Value, int Start, int Length) : Expr(Start, Length);

public sealed record BoolLit(bool Value, int Start, int Length) : Expr(Start, Length);

public sealed record VarRef(string Name, int Start, int Length) : Expr(Start, Length);

/// <summary>Op is "-" or "not".</summary>
public sealed record Unary(string Op, Expr Operand, int Start, int Length) : Expr(Start, Length);

/// <summary>Op is one of + - * / == != &lt; &lt;= &gt; &gt;= and or.</summary>
public sealed record Binary(string Op, Expr Left, Expr Right, int Start, int Length) : Expr(Start, Length);

/// <summary>Function is lower-cased. "if" is a Call with three arguments.</summary>
public sealed record Call(string Function, IReadOnlyList<Expr> Args, int Start, int Length) : Expr(Start, Length);
