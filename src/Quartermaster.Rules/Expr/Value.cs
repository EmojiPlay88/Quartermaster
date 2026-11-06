using System.Globalization;

namespace Quartermaster.Rules.Expr;

public enum ValueKind { Number, Text, Bool }

/// <summary>A formula value: number, text or bool.</summary>
public readonly record struct Value
{
    public ValueKind Kind { get; init; }
    public double Number { get; init; }
    public string Text { get; init; }
    public bool Bool { get; init; }

    public static Value Of(double n) => new() { Kind = ValueKind.Number, Number = n, Text = "" };
    public static Value Of(string s) => new() { Kind = ValueKind.Text, Text = s ?? "" };
    public static Value Of(bool b) => new() { Kind = ValueKind.Bool, Bool = b, Text = "" };

    public bool IsTruthy => Kind == ValueKind.Bool ? Bool : Kind == ValueKind.Number && Number != 0;

    public string Display() => Kind switch
    {
        ValueKind.Number => FormatNumber(Number),
        ValueKind.Bool => Bool ? "true" : "false",
        _ => Text ?? "",
    };

    internal static string FormatNumber(double d)
    {
        var s = d.ToString("0.##", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}
