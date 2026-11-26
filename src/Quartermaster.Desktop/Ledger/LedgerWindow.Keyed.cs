using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Desktop.Ledgers;

/// <summary>
/// The keyed values panel (B6) under the grid: for the one selected unit or holder row, a small table (Property, Key, Value) of its keyed values
/// from the viewer's state (<see cref="LedgerViewRules.KeyedRows"/>: a value the view may lack shows as hidden, never as the Default). The Value is
/// editable like a grid cell (note prompt). The panel is hidden when the row has no keyed values, on the Places tab and for several rows.
/// </summary>
public sealed partial class LedgerWindow
{
    const double KeyedPropertyWidth = 180, KeyedKeyWidth = 140, KeyedValueWidth = 100;

    readonly TextBlock _keyedHeader = new() { FontWeight = FontWeight.Bold, Margin = new Thickness(6, 4, 6, 2) };
    readonly StackPanel _keyedRows = new();
    readonly Border _keyedPanel = new() { IsVisible = false, BorderThickness = new Thickness(0, 1, 0, 0), MaxHeight = 160 };
    IReadOnlyList<KeyedCell> _keyedCells = Array.Empty<KeyedCell>();
    string _keyedLedgerKey = "";
    (int Index, TextBox Box)? _keyedEditing;

    // ---------- what tests read ----------

    internal bool KeyedPanelShown => _keyedPanel.IsVisible;
    internal string KeyedHeaderText => _keyedHeader.Text ?? "";

    /// <summary>The panel's rows as shown: Property, Key, Value.</summary>
    internal IReadOnlyList<string[]> KeyedRowTexts => _keyedCells.Select(c => new[] { c.Property.Name, c.Key, KeyedValueShown(c) }).ToList();

    void InitKeyed()
    {
        _keyedPanel.Bind(Border.BorderBrushProperty, _keyedPanel.GetResourceObservable("Classic.BorderBrush"));
        var scroll = new ScrollViewer { Content = _keyedRows, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var stack = new DockPanel();
        DockPanel.SetDock(_keyedHeader, Dock.Top);
        stack.Children.Add(_keyedHeader);
        stack.Children.Add(scroll);
        _keyedPanel.Child = stack;
    }

    string KeyedValueShown(KeyedCell c) =>
        c.Value is { } v && _s.Rules is { } pack ? PropertyLabels.Text(pack, c.Property, v) : LedgerViewRules.KeyedValueText(c);

    /// <summary>Shows the keyed values of the one selected row, or hides the panel.</summary>
    void UpdateKeyedPanel()
    {
        if (_closing || _keyedEditing != null) return;
        var rows = _list.SelectedItems?.OfType<RowItem>().ToList() ?? new();
        IReadOnlyList<KeyedCell> cells = Array.Empty<KeyedCell>();
        if (!Places && rows.Count == 1 && !rows[0].Data.IsGroupHeader && _s.Rules is { } pack && _s.View is { } view)
        {
            _keyedLedgerKey = rows[0].Data.Row.Key;
            cells = LedgerViewRules.KeyedRows(view, pack, _keyedLedgerKey);
            _keyedHeader.Text = $"Keyed values of {rows[0].Data.Row.Name}";
        }
        _keyedCells = cells;
        _keyedRows.Children.Clear();
        _keyedPanel.IsVisible = cells.Count > 0;
        if (cells.Count == 0) return;
        _keyedRows.Children.Add(KeyedRow("Property", "Key", "Value", header: true, -1));
        for (var i = 0; i < cells.Count; i++)
            _keyedRows.Children.Add(KeyedRow(cells[i].Property.Name, cells[i].Key, KeyedValueShown(cells[i]), header: false, i));
    }

    Control KeyedRow(string property, string key, string value, bool header, int index)
    {
        var weight = header ? FontWeight.Bold : FontWeight.Normal;
        TextBlock Cell(string text, double width, HorizontalAlignment align) => new()
        {
            Text = text, Width = width, FontWeight = weight, HorizontalAlignment = align, TextAlignment = align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 1),
        };
        var valueBlock = Cell(value, KeyedValueWidth, HorizontalAlignment.Right);
        var valueHost = new Border { Child = valueBlock, Background = Brushes.Transparent };
        if (!header && !_keyedCells[index].Hidden) valueHost.DoubleTapped += (_, e) => { e.Handled = true; Run(() => BeginKeyedEdit(index)); };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Cell(property, KeyedPropertyWidth, HorizontalAlignment.Left));
        row.Children.Add(Cell(key, KeyedKeyWidth, HorizontalAlignment.Left));
        row.Children.Add(valueHost);
        return row;
    }

    /// <summary>Starts editing a keyed panel Value (double-click); a hidden value is not editable. Returns the editor, or null.</summary>
    internal TextBox? BeginKeyedEdit(int index)
    {
        if (_closing || _keyedEditing != null || index < 0 || index >= _keyedCells.Count || _keyedCells[index].Hidden) return null;
        if (_keyedRows.Children[index + 1] is not StackPanel { Children: [_, _, Border host] }) return null;
        var cell = _keyedCells[index];
        var box = new TextBox
        {
            Text = cell.Value is { } v ? LedgerTable.FormatNumber(v) : "", Width = KeyedValueWidth, Padding = new Thickness(2, 0), MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Right,
        };
        host.Child = box;
        _keyedEditing = (index, box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; EndKeyedEdit(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndKeyedEdit(false); }
        };
        box.LostFocus += (_, _) => EndKeyedEdit(true);
        box.Focus();
        box.SelectAll();
        return box;
    }

    void EndKeyedEdit(bool commit)
    {
        if (_keyedEditing is not { } ed) return;
        _keyedEditing = null;
        var text = (ed.Box.Text ?? "").Trim();
        var cell = _keyedCells[ed.Index];
        var ledgerKey = _keyedLedgerKey;
        UpdateKeyedPanel();   // the editor goes; the cell shows its value again
        if (!commit || text.Length == 0) return;   // empty input cancels
        Dispatcher.UIThread.Post(() => { if (!_closing) RunAsync(() => CommitKeyedAsync(ledgerKey, cell.Key, cell.Property, text)); });
    }
}
