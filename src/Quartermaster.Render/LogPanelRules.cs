using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>The UI-free parts of the log panel shared by the WinForms and Avalonia apps: which rows are listed, a row's cells, and export naming/content.</summary>
public static class LogPanelRules
{
    public const string ExportTitle = "Export log";
    public const string ExportFileName = "game-log";
    public const string ChatPlaceholder = "Chat — press Enter to send";

    /// <summary>The rows the panel lists: the kind and search filter, and (when <paramref name="showMoves"/> is false) without move, place and delete rows.</summary>
    public static IReadOnlyList<LogEntry> Visible(IReadOnlyList<LogEntry> log, IReadOnlySet<string> kinds, string? search, bool showMoves)
    {
        var rows = LogFilter.Apply(log, new LogFilterSpec(kinds, search ?? ""));
        if (!showMoves) rows = rows.Where(r => LogFilter.DisplayKind(r.Kind) is not (LogKind.Move or LogKind.Place or LogKind.Delete)).ToList();
        return rows;
    }

    /// <summary>The four cells of a row: time, kind, "Turn · Phase", text.</summary>
    public static (string Time, string Kind, string TurnPhase, string Text) Cells(LogEntry r, string timeFormat) =>
        (LogFilter.Time(r, timeFormat), LogFilter.DisplayKind(r.Kind), LogFilter.TurnPhase(r), r.Text ?? "");

    /// <summary>True when the chosen export file is HTML (by its extension); anything else is plain text.</summary>
    public static bool IsHtml(string path) => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes the rows to <paramref name="path"/> as HTML or text (UTF-8 with BOM, as the WinForms export).</summary>
    public static void Export(string path, IReadOnlyList<LogEntry> rows) =>
        File.WriteAllText(path, IsHtml(path) ? LogFilter.ToHtml(rows) : LogFilter.ToText(rows), System.Text.Encoding.UTF8);
}
