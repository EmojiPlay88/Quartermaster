using System.Text;
using System.Text.RegularExpressions;

namespace Quartermaster.Rules.Tables;

public sealed record PastedGrid(List<string> ColumnKeys, List<string> RowKeys, List<List<string>> Cells);

/// <summary>Turns clipboard text (TSV, CSV or space-aligned) into a table grid.</summary>
public static class TablePaste
{
    public static PastedGrid Parse(string clipboard, bool firstRowIsHeader, bool firstColumnIsKey)
    {
        var text = (clipboard ?? "").TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
        List<List<string>> rows;
        if (text.Contains('\t')) rows = Lines(text).Select(l => l.Split('\t').Select(c => c.Trim()).ToList()).ToList();
        else if (text.Contains(',')) rows = Csv(text);
        else rows = Lines(text).Select(l => Regex.Split(l.Trim(), @" {2,}").Select(c => c.Trim()).ToList()).ToList();

        int width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        foreach (var r in rows) while (r.Count < width) r.Add("");
        while (width > 0 && rows.All(r => r[width - 1].Length == 0))
        {
            foreach (var r in rows) r.RemoveAt(width - 1);
            width--;
        }

        var cols = new List<string>();
        if (firstRowIsHeader && rows.Count > 0)
        {
            cols = rows[0].Skip(firstColumnIsKey ? 1 : 0).ToList();
            rows.RemoveAt(0);
        }
        var keys = new List<string>();
        var cells = new List<List<string>>();
        foreach (var r in rows)
        {
            if (firstColumnIsKey && r.Count > 0) { keys.Add(r[0]); cells.Add(r.Skip(1).ToList()); }
            else cells.Add(r);
        }
        return new PastedGrid(cols, keys, cells);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l));

    private static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, wasQuoted = false;

        void EndCell() { row.Add(wasQuoted ? cur.ToString() : cur.ToString().Trim()); cur.Clear(); wasQuoted = false; }
        void EndRow()
        {
            EndCell();
            if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
            row = new List<string>();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; }
                    else quoted = false;
                }
                else cur.Append(ch);
            }
            else if (ch == '"' && cur.ToString().Trim().Length == 0) { cur.Clear(); quoted = true; wasQuoted = true; }
            else if (ch == ',') EndCell();
            else if (ch == '\n') EndRow();
            else cur.Append(ch);
        }
        EndRow();
        return rows;
    }
}
