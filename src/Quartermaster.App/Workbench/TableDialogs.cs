using Quartermaster.Rules;
using Quartermaster.Rules.Tables;

namespace Quartermaster.App;

/// <summary>Edits the column keys of a TwoD table: one key per line.</summary>
sealed class ColumnsDialog : Form
{
    readonly TextBox _box = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };

    ColumnsDialog(IEnumerable<string> keys)
    {
        Text = "Columns";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(300, 260);
        _box.Text = string.Join(Environment.NewLine, keys);
        var label = new Label { Text = "One column key per line (a number, a range like 3-5, 7+, or text):", Dock = DockStyle.Top, Height = 32 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 132, Top = 228, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 214, Top = 228, Width = 75 };
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 40) };
        host.Controls.Add(_box);
        Controls.Add(ok);
        Controls.Add(cancel);
        Controls.Add(host);
        Controls.Add(label);
        ok.BringToFront();
        cancel.BringToFront();
        AcceptButton = null;
        CancelButton = cancel;
    }

    /// <summary>The new keys (at least one), or null when cancelled.</summary>
    public static List<string>? Ask(IWin32Window owner, IEnumerable<string> keys)
    {
        using var d = new ColumnsDialog(keys);
        if (d.ShowDialog(owner) != DialogResult.OK) return null;
        var list = d._box.Text.Replace("\r\n", "\n").Split('\n').Select(s => s.Trim()).ToList();
        while (list.Count > 1 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        return list.Count == 0 ? new List<string> { "" } : list;
    }
}

/// <summary>Previews clipboard text as a grid; the user decides whether the first row/column are keys.</summary>
sealed class PasteTableDialog : Form
{
    readonly CheckBox _header = new() { Text = "First row is the column header", Checked = true, AutoSize = true };
    readonly CheckBox _keys = new() { Text = "First column is the row key", Checked = true, AutoSize = true };
    readonly DataGridView _preview = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize, BackgroundColor = SystemColors.AppWorkspace,
    };
    readonly string _text;
    readonly bool _tsvOnly;
    readonly Label _note;
    readonly string _noteText;
    readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
    PastedGrid _grid = new(new(), new(), new());

    PasteTableDialog(string clipboard, string? title = null, string? targetDescription = null, bool tsvOnly = false)
    {
        _text = clipboard;
        _tsvOnly = tsvOnly;
        Text = title ?? "Paste table from clipboard";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 360);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(6, 4, 0, 0) };
        top.Controls.Add(_header);
        top.Controls.Add(_keys);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(_ok);
        _noteText = targetDescription ?? "OK replaces the rows, columns and cells of the selected table.";
        var note = _note = new Label { Dock = DockStyle.Bottom, Height = 30, Text = _noteText, Padding = new Padding(6, 0, 0, 0) };
        Controls.Add(_preview);
        Controls.Add(note);
        Controls.Add(bottom);
        Controls.Add(top);
        AcceptButton = _ok;
        CancelButton = cancel;
        _header.CheckedChanged += (_, _) => Refresh2();
        _keys.CheckedChanged += (_, _) => Refresh2();
        Refresh2();
    }

    void Refresh2()
    {
        _grid = TablePaste.Parse(_text, _header.Checked, _keys.Checked, _tsvOnly);
        _preview.Columns.Clear();
        _preview.Rows.Clear();
        var width = _grid.Cells.Count == 0 ? _grid.ColumnKeys.Count : Math.Max(_grid.Cells.Max(r => r.Count), _grid.ColumnKeys.Count);
        if (_keys.Checked) _preview.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Row key" });
        for (var c = 0; c < width; c++)
            _preview.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c < _grid.ColumnKeys.Count ? _grid.ColumnKeys[c] : "" });
        for (var r = 0; r < _grid.Cells.Count; r++)
        {
            var vals = new List<object>();
            if (_keys.Checked) vals.Add(r < _grid.RowKeys.Count ? _grid.RowKeys[r] : "");
            for (var c = 0; c < width; c++) vals.Add(c < _grid.Cells[r].Count ? _grid.Cells[r][c] : "");
            if (vals.Count > 0) _preview.Rows.Add(vals.ToArray());
        }
        _ok.Enabled = _grid.Cells.Count > 0;
        _note.ForeColor = SystemColors.ControlText;
        _note.Text = _noteText;
        if (_tsvOnly && Problem(_grid, _keys.Checked) is { } problem)
        {
            _ok.Enabled = false;
            _note.Text = problem;
            _note.ForeColor = Color.Firebrick;
        }
    }

    /// <summary>Why an OCR grid can't be imported as parsed (null when it can).</summary>
    internal static string? Problem(PastedGrid g, bool firstColumnIsKey) =>
        firstColumnIsKey && g.Cells.Count > 0 && g.Cells.All(r => r.Count == 0)
            ? "This grid has one column: with \"First column is the row key\" there are no values. Clear that box, or split the column."
            : null;

    /// <summary>The parsed grid, or null when cancelled or the clipboard held no table.</summary>
    public static PastedGrid? Ask(IWin32Window owner, string clipboard)
    {
        using var d = new PasteTableDialog(clipboard);
        return d.ShowDialog(owner) == DialogResult.OK ? d._grid : null;
    }

    /// <summary>
    /// Previews prefilled TSV (an OCR grid) with the usual header/key checkboxes; <paramref name="targetDescription"/> says where OK
    /// puts the rows. Null when cancelled. Smoke runs confirm with the default checkboxes without showing the dialog.
    /// </summary>
    public static PastedGrid? Ask(IWin32Window owner, string tsv, string targetDescription)
    {
        using var d = new PasteTableDialog(tsv, "Import into table", targetDescription, tsvOnly: true);
        if (Program.Smoke)
        {
            Program.AppLog($"smoke: paste table dialog auto-confirmed ({targetDescription})");
            return d._ok.Enabled ? d._grid : null;
        }
        return d.ShowDialog(owner) == DialogResult.OK ? d._grid : null;
    }
}
