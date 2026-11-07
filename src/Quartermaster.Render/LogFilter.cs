using System.Net;
using System.Text;
using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>Which log kinds are shown, plus a case-insensitive text search.</summary>
public sealed record LogFilterSpec(IReadOnlySet<string> Kinds, string Search);

/// <summary>Filtering and export of <see cref="LogEntry"/> rows for the log panel.</summary>
public static class LogFilter
{
    public static readonly IReadOnlyList<string> AllKinds = new[]
    {
        LogKind.Dice, LogKind.Move, LogKind.Place, LogKind.Delete, LogKind.Formula, LogKind.Ledger, LogKind.Note, LogKind.Turn, LogKind.Calc, LogKind.Chat, LogKind.System,
    };

    /// <summary>The filter-button label of a kind: "Calculations" for calc, the kind itself otherwise.</summary>
    public static string KindLabel(string kind) => kind == LogKind.Calc ? "Calculations" : kind;

    /// <summary>"T3 Supply" for an entry tagged with a turn and phase; empty otherwise.</summary>
    public static string TurnPhase(LogEntry e) => e.Turn is { } t ? (string.IsNullOrEmpty(e.Phase) ? $"T{t}" : $"T{t} {e.Phase}") : "";

    /// <summary>The kind shown for an entry: kinds this build does not know count as system.</summary>
    public static string DisplayKind(string? kind) =>
        kind != null && AllKinds.Contains(kind, StringComparer.Ordinal) ? kind : LogKind.System;

    public static IReadOnlyList<LogEntry> Apply(IReadOnlyList<LogEntry> log, LogFilterSpec f)
    {
        var search = f.Search ?? "";
        return log.Where(e => f.Kinds.Contains(DisplayKind(e.Kind))
            && (search.Length == 0 || (e.Text ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static string Time(LogEntry e) => Time(e, "HH:mm:ss");

    /// <summary>Local time in a user format; an unusable format falls back to HH:mm:ss.</summary>
    public static string Time(LogEntry e, string format)
    {
        try { return e.At.ToLocalTime().ToString(format, System.Globalization.CultureInfo.InvariantCulture); }
        catch (FormatException) { return Time(e); }
    }

    public static string ToText(IReadOnlyList<LogEntry> rows)
    {
        var sb = new StringBuilder();
        foreach (var e in rows)
        {
            sb.Append('[').Append(Time(e)).Append("] ");
            if (TurnPhase(e) is { Length: > 0 } tag) sb.Append(tag).Append(' ');
            sb.Append(DisplayKind(e.Kind)).Append("  ").Append(e.Text).AppendLine();
        }
        return sb.ToString();
    }

    public static string ToHtml(IReadOnlyList<LogEntry> rows)
    {
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder("<table border=\"1\" cellspacing=\"0\" cellpadding=\"3\">\n<tr><th>Time</th><th>Kind</th><th>Text</th></tr>\n");
        foreach (var e in rows)
            sb.Append("<tr><td>").Append(H(Time(e))).Append("</td><td>").Append(H(DisplayKind(e.Kind))).Append("</td><td>").Append(H(e.Text)).Append("</td></tr>\n");
        return sb.Append("</table>\n").ToString();
    }
}
