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
        LogKind.Dice, LogKind.Move, LogKind.Place, LogKind.Delete, LogKind.Formula, LogKind.Ledger, LogKind.Note, LogKind.Turn, LogKind.System,
    };

    /// <summary>The kind shown for an entry: kinds this build does not know count as system.</summary>
    public static string DisplayKind(string? kind) =>
        kind != null && AllKinds.Contains(kind, StringComparer.Ordinal) ? kind : LogKind.System;

    public static IReadOnlyList<LogEntry> Apply(IReadOnlyList<LogEntry> log, LogFilterSpec f)
    {
        var search = f.Search ?? "";
        return log.Where(e => f.Kinds.Contains(DisplayKind(e.Kind))
            && (search.Length == 0 || (e.Text ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static string Time(LogEntry e) => e.At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static string ToText(IReadOnlyList<LogEntry> rows)
    {
        var sb = new StringBuilder();
        foreach (var e in rows) sb.Append('[').Append(Time(e)).Append("] ").Append(DisplayKind(e.Kind)).Append("  ").Append(e.Text).AppendLine();
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
