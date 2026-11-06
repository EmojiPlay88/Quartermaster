namespace Quartermaster.Rules.Expr;

/// <summary>A formula could not be understood. Position is a 0-based char index into the source.</summary>
public sealed class ParseError : Exception
{
    public int Position { get; }
    public string Detail { get; }

    public ParseError(int position, string detail) : base($"{detail} at position {position}")
    {
        Position = position;
        Detail = detail;
    }
}
