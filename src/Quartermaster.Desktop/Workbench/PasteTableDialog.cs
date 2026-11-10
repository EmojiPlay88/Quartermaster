using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Previews clipboard (or scanned TSV) text as a grid; the user decides whether the first row / column are keys (WinForms PasteTableDialog).
/// Closes with the parsed <see cref="PastedGrid"/>, or null when cancelled.
/// </summary>
sealed class PasteTableDialog : Window
{
    public const string DefaultNote = "OK replaces the rows, columns and cells of the selected table.";

    readonly string _text;
    readonly bool _tsvOnly;
    readonly string _noteText;
    readonly Grid _preview = new();
    PastedGrid _grid = new(new(), new(), new());

    public CheckBox HeaderBox { get; }
    public CheckBox KeysBox { get; }
    public Button OkButton { get; }
    public Button CancelButton { get; }
    public TextBlock Note { get; }

    /// <summary>The preview's header row, and its body rows (cell text), as currently shown.</summary>
    public IReadOnlyList<string> PreviewHeaders { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<string>> PreviewRows { get; private set; } = Array.Empty<IReadOnlyList<string>>();

    public PasteTableDialog(string clipboard, string? title = null, string? targetDescription = null, bool tsvOnly = false)
    {
        _text = clipboard;
        _tsvOnly = tsvOnly;
        Title = title ?? "Paste table from clipboard";
        Width = 520;
        Height = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        HeaderBox = new CheckBox { Content = "First row is the column header", IsChecked = true };
        KeysBox = new CheckBox { Content = "First column is the row key", IsChecked = true };
        OkButton = new Button { Content = "OK", Width = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        CancelButton = new Button { Content = "Cancel", Width = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        OkButton.Click += (_, _) => Close(_grid);
        CancelButton.Click += (_, _) => Close(null);
        _noteText = targetDescription ?? DefaultNote;
        Note = new TextBlock { Text = _noteText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 6, 2), MinHeight = 30 };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(6, 4, 0, 4) };
        top.Children.Add(HeaderBox);
        top.Children.Add(KeysBox);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(6) };
        bottom.Children.Add(OkButton);
        bottom.Children.Add(CancelButton);
        var scroll = new ScrollViewer
        {
            Content = _preview, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Background = Brushes.Gray,
        };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(Note, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(bottom);
        dock.Children.Add(Note);
        dock.Children.Add(scroll);
        Content = dock;

        HeaderBox.IsCheckedChanged += (_, _) => Refresh();
        KeysBox.IsCheckedChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>The grid as currently parsed.</summary>
    public PastedGrid Parsed => _grid;

    void Refresh()
    {
        var keys = KeysBox.IsChecked == true;
        _grid = TablePaste.Parse(_text, HeaderBox.IsChecked == true, keys, _tsvOnly);
        var width = _grid.Cells.Count == 0 ? _grid.ColumnKeys.Count : Math.Max(_grid.Cells.Max(r => r.Count), _grid.ColumnKeys.Count);

        var headers = new List<string>();
        if (keys) headers.Add("Row key");
        for (var c = 0; c < width; c++) headers.Add(c < _grid.ColumnKeys.Count ? _grid.ColumnKeys[c] : "");
        var rows = new List<IReadOnlyList<string>>();
        for (var r = 0; r < _grid.Cells.Count; r++)
        {
            var vals = new List<string>();
            if (keys) vals.Add(r < _grid.RowKeys.Count ? _grid.RowKeys[r] : "");
            for (var c = 0; c < width; c++) vals.Add(c < _grid.Cells[r].Count ? _grid.Cells[r][c] : "");
            if (vals.Count > 0) rows.Add(vals);
        }
        PreviewHeaders = headers;
        PreviewRows = rows;
        Build(headers, rows);

        OkButton.IsEnabled = _grid.Cells.Count > 0;
        Note.Text = _noteText;
        Note.ClearValue(TextBlock.ForegroundProperty);
        if (_tsvOnly && TablePaste.Problem(_grid, keys) is { } problem)
        {
            OkButton.IsEnabled = false;
            Note.Text = problem;
            Note.Foreground = Brushes.Firebrick;
        }
    }

    /// <summary>A plain read-only grid: a grey header row and the cells, each in a thin border.</summary>
    void Build(List<string> headers, List<IReadOnlyList<string>> rows)
    {
        _preview.Children.Clear();
        _preview.RowDefinitions.Clear();
        _preview.ColumnDefinitions.Clear();
        for (var c = 0; c < headers.Count; c++) _preview.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto) { MinWidth = 60 });
        for (var r = 0; r <= rows.Count; r++) _preview.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        void Cell(int r, int c, string text, bool header)
        {
            var b = new Border
            {
                BorderBrush = Brushes.DarkGray, BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(4, 2),
                Background = header ? Brushes.Gainsboro : Brushes.White,
                Child = new TextBlock { Text = text, FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal, Foreground = Brushes.Black },
            };
            Grid.SetRow(b, r);
            Grid.SetColumn(b, c);
            _preview.Children.Add(b);
        }
        for (var c = 0; c < headers.Count; c++) Cell(0, c, headers[c], true);
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++) Cell(r + 1, c, rows[r][c], false);
    }
}
