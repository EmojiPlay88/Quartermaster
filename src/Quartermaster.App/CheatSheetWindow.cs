using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// Semi-automatic mode: a modeless window beside the formula dialog listing where each value comes from (Variable / Source / Value) and
/// every table the formula reads drawn whole with the used cell in gold. Owned by the dialog, so it closes with it.
/// </summary>
sealed class CheatSheetWindow : Form
{
    readonly ListView _list = new() { View = View.Details, FullRowSelect = true, GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Top, Height = 140 };
    readonly FlowLayoutPanel _tables = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill };
    readonly TableLayoutPanel _root = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
    string _signature = "";

    public CheatSheetWindow()
    {
        Text = "Cheat sheet";
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(420, 420);
        _list.Columns.Add("Variable", 80);
        _list.Columns.Add("Source", 150);
        _list.Columns.Add("Value", 150);
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _list.Dock = DockStyle.Fill;
        _root.Controls.Add(_list, 0, 0);
        _root.Controls.Add(_tables, 0, 1);
        Controls.Add(_root);
    }

    /// <summary>Redraws from the latest expected run. Cheap when nothing changed.</summary>
    public void Render(IReadOnlyList<CheatRow> rows)
    {
        var sig = string.Join("\n", rows.Select(r => $"{r.Variable}|{r.Source}|{r.ValueText}|{r.Table?.Id}|{r.HighlightRow}|{r.HighlightCol}"));
        if (sig == _signature) return;
        _signature = sig;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in rows) _list.Items.Add(new ListViewItem(new[] { r.Variable, r.Source, r.ValueText }));
        _list.EndUpdate();

        foreach (var c in _tables.Controls.Cast<Control>().ToList()) { _tables.Controls.Remove(c); c.Dispose(); }
        foreach (var group in rows.Where(r => r.Table != null).GroupBy(r => r.Table!.Id))
        {
            var table = group.First().Table!;
            var hits = group.Where(r => r.HighlightRow != null && r.HighlightCol != null).Select(r => (r.HighlightRow!.Value, r.HighlightCol!.Value)).ToHashSet();
            _tables.Controls.Add(new Label { Text = table.Name, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 6, 0, 2) });
            _tables.Controls.Add(MakeGrid(table, hits));
        }
    }

    DataGridView MakeGrid(TableDef t, HashSet<(int Row, int Col)> hits)
    {
        var grid = new DataGridView
        {
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = true,
            RowHeadersWidth = 90, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            BackgroundColor = SystemColors.Window, EnableHeadersVisualStyles = true, Margin = new Padding(0, 0, 0, 6),
            SelectionMode = DataGridViewSelectionMode.CellSelect, TabStop = false,
        };
        foreach (var key in t.ColumnKeys) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = key, Width = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
        for (var r = 0; r < t.RowKeys.Count; r++)
        {
            var cells = r < t.Cells.Count ? t.Cells[r] : new List<string>();
            var values = Enumerable.Range(0, t.ColumnKeys.Count).Select(c => c < cells.Count ? (object)cells[c] : "").ToArray();
            var idx = grid.Rows.Add(values);
            grid.Rows[idx].HeaderCell.Value = t.RowKeys[r];
        }
        foreach (var (row, col) in hits)
            if (row >= 0 && row < grid.Rows.Count && col >= 0 && col < grid.Columns.Count)
            {
                grid.Rows[row].Cells[col].Style.BackColor = Color.Gold;
                grid.Rows[row].Cells[col].Style.SelectionBackColor = Color.Gold;
                grid.Rows[row].Cells[col].Style.SelectionForeColor = SystemColors.ControlText;
            }
        grid.ClearSelection();
        grid.AutoSize = true;
        grid.ScrollBars = ScrollBars.None;
        return grid;
    }

    /// <summary>Test/smoke: texts of the variable list rows.</summary>
    public IReadOnlyList<string> RowTexts => _list.Items.Cast<ListViewItem>().Select(i => string.Join(" | ", i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text))).ToList();

    /// <summary>Smoke: how many table cells are drawn gold.</summary>
    public int GoldCells => _tables.Controls.OfType<DataGridView>().Sum(g => g.Rows.Cast<DataGridViewRow>().Sum(r => r.Cells.Cast<DataGridViewCell>().Count(c => c.Style.BackColor == Color.Gold)));

    public void SaveScreenshot(string path)
    {
        PerformLayout();
        foreach (Control c in Controls) c.Update();
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: cheat sheet screenshot saved to " + path);
    }
}
