using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Formulas;

/// <summary>
/// Semi-automatic mode, as WinForms CheatSheetWindow: a modeless window beside the formula window listing where each value comes from
/// (Variable / Source / Value) and every table the formula reads drawn whole with the used cell in gold. Owned by the formula window, so it
/// closes with it.
/// </summary>
sealed class CheatSheetView : Window
{
    public static readonly IReadOnlyList<string> Columns = new[] { "Variable", "Source", "Value" };
    static readonly double[] ColumnWidths = { 80, 150, 150 };

    readonly Grid _list = new() { ColumnDefinitions = new ColumnDefinitions("80,150,150") };
    readonly StackPanel _tables = new() { Orientation = Orientation.Vertical };
    readonly List<string> _rows = new();
    readonly List<string> _tableNames = new();
    int _gold;
    string _signature = "\0";

    public CheatSheetView()
    {
        Title = "Cheat sheet";
        ShowInTaskbar = false;
        Width = 420;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var listFrame = new Border { BorderThickness = new Thickness(1), Height = 146, Child = new ScrollViewer { Content = _list } };
        listFrame.Bind(Border.BorderBrushProperty, listFrame.GetResourceObservable("Classic.BorderBrush"));
        listFrame.Bind(Border.BackgroundProperty, listFrame.GetResourceObservable("Classic.WindowBrush"));
        var root = new DockPanel { Margin = new Thickness(2) };
        DockPanel.SetDock(listFrame, Dock.Top);
        root.Children.Add(listFrame);
        root.Children.Add(new ScrollViewer
        {
            Content = _tables, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        });
        Content = root;
        Render(Array.Empty<CheatRow>());
    }

    /// <summary>Redraws from the latest expected run. Cheap when nothing changed.</summary>
    public void Render(IReadOnlyList<CheatRow> rows)
    {
        var sig = string.Join("\n", rows.Select(r => $"{r.Variable}|{r.Source}|{r.ValueText}|{r.Table?.Id}|{r.HighlightRow}|{r.HighlightCol}"));
        if (sig == _signature) return;
        _signature = sig;

        _list.Children.Clear();
        _list.RowDefinitions.Clear();
        _rows.Clear();
        AddListRow(Columns, header: true);
        foreach (var r in rows)
        {
            AddListRow(new[] { r.Variable, r.Source, r.ValueText }, header: false);
            _rows.Add($"{r.Variable} | {r.Source} | {r.ValueText}");
        }

        _tables.Children.Clear();
        _tableNames.Clear();
        _gold = 0;
        foreach (var group in rows.Where(r => r.Table != null).GroupBy(r => r.Table!.Id))
        {
            var table = group.First().Table!;
            var hits = group.Where(r => r.HighlightRow != null && r.HighlightCol != null).Select(r => (r.HighlightRow!.Value, r.HighlightCol!.Value)).ToHashSet();
            _tableNames.Add(table.Name);
            _tables.Children.Add(new TextBlock { Text = table.Name, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 2) });
            _tables.Children.Add(MakeGrid(table, hits));
        }
    }

    void AddListRow(IReadOnlyList<string> cells, bool header)
    {
        var row = _list.RowDefinitions.Count;
        _list.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < cells.Count; c++)
        {
            var cell = Cell(cells[c], header);
            cell.Width = ColumnWidths[c];
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, c);
            _list.Children.Add(cell);
        }
    }

    Border Cell(string text, bool header, IBrush? back = null)
    {
        var b = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(3, 1),
            Child = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("Classic.BorderBrush"));
        if (back != null) b.Background = back;
        else b.Bind(Border.BackgroundProperty, b.GetResourceObservable(header ? "Classic.FaceBrush" : "Classic.WindowBrush"));
        return b;
    }

    /// <summary>The table whole (row keys as row headers, column keys as column headers), the used cells in gold.</summary>
    Grid MakeGrid(TableDef t, HashSet<(int Row, int Col)> hits)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(90, GridUnitType.Pixel));
        foreach (var _ in t.ColumnKeys) grid.ColumnDefinitions.Add(new ColumnDefinition(60, GridUnitType.Pixel));
        for (var r = 0; r <= t.RowKeys.Count; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); grid.Children.Add(c); }
        Put(Cell("", true), 0, 0);
        for (var c = 0; c < t.ColumnKeys.Count; c++) Put(Cell(t.ColumnKeys[c], true), 0, c + 1);
        for (var r = 0; r < t.RowKeys.Count; r++)
        {
            Put(Cell(t.RowKeys[r], true), r + 1, 0);
            var cells = r < t.Cells.Count ? t.Cells[r] : new List<string>();
            for (var c = 0; c < t.ColumnKeys.Count; c++)
            {
                var gold = hits.Contains((r, c));
                if (gold) _gold++;
                Put(Cell(c < cells.Count ? cells[c] : "", false, gold ? Brushes.Gold : null), r + 1, c + 1);
            }
        }
        return grid;
    }

    /// <summary>Test: the list's column headers.</summary>
    internal IReadOnlyList<string> Headers => Columns;

    /// <summary>Test: texts of the variable list rows ("Variable | Source | Value").</summary>
    internal IReadOnlyList<string> RowTexts => _rows;

    /// <summary>Test: the names of the tables drawn.</summary>
    internal IReadOnlyList<string> TableNames => _tableNames;

    /// <summary>Test: how many table cells are drawn gold.</summary>
    internal int GoldCells => _gold;
}
