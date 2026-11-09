using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Modal Step Back dialog (WinForms StepBackDialog). List mode ("Step Back To") shows the checkpoints newest first with a summary of what
/// stepping back would undo; confirm mode ("Step Back to Start of Phase") shows just the one target and its summary. Closes with the chosen
/// <see cref="CheckpointRow"/> (Step Back) or null (Cancel, or the window closed).
/// </summary>
public partial class StepBackDialog : Window
{
    /// <summary>WinForms column widths; the Phase column takes what the others leave (at least 150).</summary>
    static readonly double[] Widths = { 50, 110, 100, 150, 80, 60 };
    const int PhaseColumn = 3;

    readonly IReadOnlyList<CheckpointRow> _rows;
    readonly Func<CheckpointRow, string> _summary;
    readonly bool _confirmOnly;

    /// <summary>For the XAML designer only.</summary>
    public StepBackDialog() : this(Array.Empty<CheckpointRow>(), _ => "", confirmOnly: false) { }

    /// <param name="rows">Checkpoints, newest first.</param>
    /// <param name="summary">Plain-words summary of what stepping back to a row would undo.</param>
    /// <param name="confirmOnly">Show only the first row's summary (Step Back to Start of Phase).</param>
    public StepBackDialog(IReadOnlyList<CheckpointRow> rows, Func<CheckpointRow, string> summary, bool confirmOnly)
    {
        _rows = rows;
        _summary = summary;
        _confirmOnly = confirmOnly;
        InitializeComponent();
        Title = confirmOnly ? "Step Back to Start of Phase" : "Step Back To";
        if (confirmOnly)
        {
            Width = 420;
            Height = 220;
            MinWidth = MinHeight = 0;
            CanResize = false;
            ListHost.IsVisible = false;
            Head.IsVisible = true;
            Summary.Height = 110;
            if (rows.Count > 0)
            {
                Head.Text = PhaseTexts.StepBackConfirm(rows[0]);
                Summary.Text = summary(rows[0]);
            }
        }
        else
        {
            Header.ColumnDefinitions = Columns();
            for (var i = 0; i < PhaseTexts.StepBackColumns.Count; i++) Header.Children.Add(Cell(PhaseTexts.StepBackColumns[i], i, bold: true));
            List.ItemsSource = rows.Select(r => Row(PhaseTexts.StepBackCells(r))).ToList();
            List.SelectionChanged += (_, _) => ShowSummary();
            List.DoubleTapped += (_, _) => { if (Selected is { } r) Close(r); };
            if (rows.Count > 0) List.SelectedIndex = 0;
        }
        Ok.Click += (_, _) => Close(Selected);
        Cancel.Click += (_, _) => Close(null);
    }

    static ColumnDefinitions Columns()
    {
        var cols = new ColumnDefinitions();
        for (var i = 0; i < Widths.Length; i++)
            cols.Add(i == PhaseColumn ? new ColumnDefinition(1, GridUnitType.Star) { MinWidth = Widths[i] } : new ColumnDefinition(Widths[i], GridUnitType.Pixel));
        return cols;
    }

    static TextBlock Cell(string text, int column, bool bold = false)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(4, 1), TextTrimming = TextTrimming.CharacterEllipsis };
        if (bold) t.FontWeight = FontWeight.Bold;
        Grid.SetColumn(t, column);
        return t;
    }

    static Grid Row(IReadOnlyList<string> cells)
    {
        var g = new Grid { ColumnDefinitions = Columns(), Tag = cells };
        for (var i = 0; i < cells.Count; i++) g.Children.Add(Cell(cells[i], i));
        return g;
    }

    /// <summary>The checkpoint chosen, or null when nothing is selected.</summary>
    public CheckpointRow? Selected =>
        _rows.Count == 0 ? null : _confirmOnly ? _rows[0] : List.SelectedIndex is var i and >= 0 && i < _rows.Count ? _rows[i] : null;

    /// <summary>The summary text currently shown.</summary>
    public string SummaryText => Summary.Text ?? "";

    void ShowSummary() => Summary.Text = Selected is { } r ? _summary(r) : "";

    // ---- what tests read ----

    internal string HeadText => Head.Text ?? "";
    internal IReadOnlyList<string> Headers => _confirmOnly ? Array.Empty<string>() : Header.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
    internal IReadOnlyList<IReadOnlyList<string>> RowCells => List.Items.OfType<Grid>().Select(g => (IReadOnlyList<string>)g.Tag!).ToList();
    internal IReadOnlyList<string> ButtonTexts => new[] { Ok, Cancel }.Select(b => b.Content as string ?? "").ToList();
    internal void Select(int index) => List.SelectedIndex = index;
    internal void ClickStepBack() => Ok.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    internal void ClickCancel() => Cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
