using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Workbench;

namespace Quartermaster.Desktop.Tests;

[Collection("MacFlag")]
public sealed class EditGridTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;

    public EditGridTests() => ShortcutGestures.IsMac = false;

    public void Dispose() => ShortcutGestures.IsMac = _wasMac;

    static readonly IReadOnlyList<EditColumn> Cols =
    [
        new("Name"), new("Kind", EditColumnKind.Combo, Items: ["A", "B", "C"]), new("On", EditColumnKind.Check), new("Fixed", ReadOnly: true),
    ];

    static IReadOnlyList<IReadOnlyList<string>> Rows(int n) =>
        Enumerable.Range(0, n).Select(i => (IReadOnlyList<string>)[$"n{i}", "A", "False", $"f{i}"]).ToList();

    static (EditGrid G, Window W, List<(int, int, string)> Log) Make(int rows = 3)
    {
        var g = new EditGrid();
        var log = new List<(int, int, string)>();
        g.CellCommitted += (r, c, t) => log.Add((r, c, t));
        g.SetColumns(Cols);
        g.SetRows(Rows(rows));
        var w = new Window { Width = 600, Height = 400, Content = g };
        w.Show();
        g.Focus();
        return (g, w, log);
    }

    static void Key(Window w, Key k, RawInputModifiers m = RawInputModifiers.None) => w.KeyPress(k, m, (PhysicalKey)0, null);

    [AvaloniaFact]
    public void Typing_starts_edit_and_enter_commits_and_moves_down()
    {
        var (g, w, log) = Make();
        g.SelectCell(0, 0);
        w.KeyTextInput("x");
        Assert.True(g.IsEditing);
        Assert.Equal("x", ((TextBox)g.Editor!).Text);
        w.KeyTextInput("y");
        Key(w, Avalonia.Input.Key.Enter);
        Assert.False(g.IsEditing);
        Assert.Equal([(0, 0, "xy")], log);
        Assert.Equal("xy", g.Cell(0, 0));
        Assert.Equal((1, 0), g.CurrentCell);
        w.Close();
    }

    [AvaloniaFact]
    public void F2_edits_existing_text()
    {
        var (g, w, _) = Make();
        g.SelectCell(1, 0);
        Key(w, Avalonia.Input.Key.F2);
        Assert.True(g.IsEditing);
        Assert.Equal("n1", ((TextBox)g.Editor!).Text);
        w.Close();
    }

    [AvaloniaFact]
    public void Escape_cancels_without_commit()
    {
        var (g, w, log) = Make();
        g.BeginEdit(0, 0, "zzz");
        Key(w, Avalonia.Input.Key.Escape);
        Assert.False(g.IsEditing);
        Assert.Empty(log);
        Assert.Equal("n0", g.Cell(0, 0));
        w.Close();
    }

    [AvaloniaFact]
    public void Tab_commits_and_moves_right()
    {
        var (g, w, log) = Make();
        g.BeginEdit(0, 0, "q");
        Key(w, Avalonia.Input.Key.Tab);
        Assert.Equal([(0, 0, "q")], log);
        Assert.Equal((0, 1), g.CurrentCell);
        w.Close();
    }

    [AvaloniaFact]
    public void Arrows_move()
    {
        var (g, w, _) = Make();
        g.SelectCell(0, 0);
        Key(w, Avalonia.Input.Key.Down);
        Key(w, Avalonia.Input.Key.Right);
        Assert.Equal((1, 1), g.CurrentCell);
        Key(w, Avalonia.Input.Key.Up);
        Key(w, Avalonia.Input.Key.Left);
        Assert.Equal((0, 0), g.CurrentCell);
        w.Close();
    }

    [AvaloniaFact]
    public void Read_only_cell_does_not_edit()
    {
        var (g, w, log) = Make();
        g.BeginEdit(0, 3);
        Assert.False(g.IsEditing);
        g.SelectCell(0, 3);
        w.KeyTextInput("x");
        Assert.False(g.IsEditing);
        g.SetCellReadOnly(1, 0, true);
        g.BeginEdit(1, 0);
        Assert.False(g.IsEditing);
        Assert.Empty(log);
        w.Close();
    }

    [AvaloniaFact]
    public void Combo_choice_commits()
    {
        var (g, w, log) = Make();
        g.BeginEdit(0, 1);
        var box = Assert.IsType<ComboBox>(g.Editor);
        Assert.Equal("A", box.SelectedItem);
        box.SelectedItem = "C";
        Assert.True(g.IsEditing);   // the open list is still being navigated
        box.IsDropDownOpen = false;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(g.IsEditing);
        Assert.Equal([(0, 1, "C")], log);
        Assert.Equal("C", g.Cell(0, 1));
        g.SetCellItems(1, 1, ["X", "Y"]);
        g.BeginEdit(1, 1);
        Assert.Equal(["X", "Y", "A"], ((ComboBox)g.Editor!).Items.Cast<string>().ToList());
        w.Close();
    }

    [AvaloniaFact]
    public void Check_toggle_commits_true_false()
    {
        var (g, w, log) = Make();
        g.SelectCell(0, 2);
        Key(w, Avalonia.Input.Key.Space);
        Key(w, Avalonia.Input.Key.Space);
        Assert.Equal([(0, 2, "True"), (0, 2, "False")], log);
        Assert.Equal("False", g.Cell(0, 2));
        Key(w, Avalonia.Input.Key.Space);
        Assert.Equal("☑", g.CellShown(0, 2));
        w.Close();
    }

    [AvaloniaFact]
    public void Error_cell_has_pink_background_and_tooltip()
    {
        var (g, w, _) = Make();
        g.SetCellError(0, 0, "bad");
        Assert.Equal(EditGrid.ErrorBrush, g.CellBackground(0, 0));
        Assert.Equal("bad", g.CellToolTip(0, 0));
        g.SetCellError(0, 0, null);
        Assert.NotEqual(EditGrid.ErrorBrush, g.CellBackground(0, 0));
        Assert.Null(g.CellToolTip(0, 0));
        var blue = new SolidColorBrush(Colors.LightBlue);
        g.SetCellTint(1, 0, blue);
        Assert.Same(blue, g.CellBackground(1, 0));
        w.Close();
    }

    [AvaloniaFact]
    public void Multi_select_rows()
    {
        var (g, w, _) = Make(5);
        Assert.False(g.MultiSelect);
        g.SelectCell(1, 0);
        Assert.Equal([1], g.SelectedRows);
        g.MultiSelect = true;
        Key(w, Avalonia.Input.Key.Down, RawInputModifiers.Shift);
        Key(w, Avalonia.Input.Key.Down, RawInputModifiers.Shift);
        Assert.Equal([1, 2, 3], g.SelectedRows);
        g.SelectCell(0, 0);
        Assert.Equal([0], g.SelectedRows);
        w.Close();
    }

    [AvaloniaFact]
    public void Mouse_click_selects_and_shift_click_extends()
    {
        var (g, w, _) = Make(6);
        g.MultiSelect = true;
        Click(g, w, 1, 0);
        Click(g, w, 3, 0, RawInputModifiers.Shift);
        Assert.Equal([1, 2, 3], g.SelectedRows);
        Click(g, w, 5, 0, RawInputModifiers.Control);
        Assert.Equal([1, 2, 3, 5], g.SelectedRows);
        Click(g, w, 2, 2);   // check cell toggles
        Assert.Equal("True", g.Cell(2, 2));
        w.Close();
    }

    static void Click(EditGrid g, Window w, int row, int col, RawInputModifiers m = RawInputModifiers.None)
    {
        var cell = g.CellControl(row, col);
        var p = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), w)!.Value;
        w.MouseDown(p, MouseButton.Left, m);
        w.MouseUp(p, MouseButton.Left, m);
    }

    [AvaloniaFact]
    public void External_change_while_editing_drops_stale_edit()
    {
        var (g, w, log) = Make();
        g.BeginEdit(0, 0, "stale");
        Assert.True(g.IsEditing);
        g.SetRows(Rows(2));
        Assert.False(g.IsEditing);
        Assert.Empty(log);
        Assert.Equal("n0", g.Cell(0, 0));
        w.Close();
    }

    [AvaloniaFact]
    public void Set_rows_keeps_current_cell_when_it_still_exists()
    {
        var (g, w, _) = Make(4);
        g.SelectCell(2, 1);
        g.SetRows(Rows(4));
        Assert.Equal((2, 1), g.CurrentCell);
        g.SetRows(Rows(2));
        Assert.Equal((0, 0), g.CurrentCell);
        g.SetRows([]);
        Assert.Null(g.CurrentCell);
        w.Close();
    }

    [AvaloniaFact]
    public void Two_thousand_cells_lay_out_under_500_ms()
    {
        var g = new EditGrid();
        g.SetColumns(Enumerable.Range(0, 10).Select(i => new EditColumn("C" + i)).ToList());
        var w = new Window { Width = 900, Height = 600, Content = g };
        w.Show();
        var rows = Enumerable.Range(0, 200).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, 10).Select(c => $"r{r}c{c}").ToList()).ToList();
        var sw = Stopwatch.StartNew();
        g.SetRows(rows);
        w.UpdateLayout();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(200, g.RowCount);
        w.Close();
    }

    [AvaloniaFact]
    public void Commit_and_cancel_edit_are_public()
    {
        var (g, w, log) = Make();
        Assert.False(g.CommitEdit());
        g.BeginEdit(0, 0, "abc");
        Assert.True(g.CommitEdit());
        Assert.False(g.IsEditing);
        Assert.Equal([(0, 0, "abc")], log);
        g.BeginEdit(1, 0, "zzz");
        g.CancelEdit();
        Assert.False(g.IsEditing);
        Assert.Single(log);
        w.Close();
    }

    [AvaloniaFact]
    public void Tooltips_and_header_tint()
    {
        var (g, w, _) = Make();
        g.SetCellToolTip(0, 0, "hint");
        Assert.Equal("hint", g.CellToolTip(0, 0));
        g.SetCellError(0, 0, "bad");
        Assert.Equal("bad", g.CellToolTip(0, 0));
        Assert.Equal(EditGrid.ErrorBrush, g.CellBackground(0, 0));
        g.SetCellError(0, 0, null);
        Assert.Equal("hint", g.CellToolTip(0, 0));
        g.SetCellToolTip(0, 0, null);
        Assert.Null(g.CellToolTip(0, 0));
        g.SetHeaderToolTip(1, "col tip");
        Assert.Equal("col tip", g.HeaderToolTip(1));
        g.SetHeaderTint(1, EditGrid.ErrorBrush);
        Assert.Equal(EditGrid.ErrorBrush, g.HeaderBackground(1));
        g.SetHeaderTint(1, null);
        Assert.NotEqual(EditGrid.ErrorBrush, g.HeaderBackground(1));
        w.Close();
    }

    [AvaloniaFact]
    public void Primary_click_on_mac_uses_meta()
    {
        ShortcutGestures.IsMac = true;
        var (g, w, _) = Make(4);
        g.MultiSelect = true;
        Click(g, w, 0, 0);
        Click(g, w, 2, 0, RawInputModifiers.Control);
        Assert.Equal([2], g.SelectedRows);
        Click(g, w, 0, 0);
        Click(g, w, 2, 0, RawInputModifiers.Meta);
        Assert.Equal([0, 2], g.SelectedRows);
        w.Close();
    }

    [AvaloniaFact]
    public void Fixed_width_columns_scroll_horizontally()
    {
        var g = new EditGrid();
        g.SetColumns(Enumerable.Range(0, 12).Select(i => new EditColumn("C" + i, Width: 90)).Append(new EditColumn("Fill")).ToList());
        g.SetRows([Enumerable.Range(0, 13).Select(i => "v" + i).ToList()]);
        var w = new Window { Width = 400, Height = 200, Content = g };
        w.Show();
        w.UpdateLayout();
        w.UpdateLayout();
        var last = g.CellControl(0, 11);
        Assert.Equal(90, last.Bounds.Width + 1, 0.5);
        Assert.True(last.TranslatePoint(new Point(0, 0), g)!.Value.X > 400);   // reachable only by scrolling
        var sv = g.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(sv.Extent.Width > sv.Viewport.Width);
        w.Close();
    }

    [AvaloniaFact]
    public void Read_only_is_grey_background_and_current_cell_is_highlighted()
    {
        var (g, w, _) = Make();
        g.SelectCell(1, 0);
        Assert.Equal(EditGrid.ReadOnlyBrush, g.CellBackground(0, 3));
        Assert.NotEqual(EditGrid.ReadOnlyBrush, g.CellBackground(1, 0));
        Assert.Equal(Brushes.White, g.CellBackground(2, 0));   // no whole-row highlight
        Assert.Equal(Brushes.White, g.CellBackground(1, 1));
        w.Close();
    }

    [AvaloniaFact]
    public void Select_cell_and_read_only_end_an_edit()
    {
        var (g, w, log) = Make();
        g.BeginEdit(0, 0, "kept");
        g.SelectCell(2, 0);
        Assert.False(g.IsEditing);
        Assert.Equal([(0, 0, "kept")], log);
        g.BeginEdit(1, 0, "dropped");
        g.SetCellReadOnly(1, 0, true);
        Assert.False(g.IsEditing);
        Assert.Single(log);
        w.Close();
    }

    [AvaloniaFact]
    public void Combo_shows_value_outside_items_and_typing_picks_prefix()
    {
        var (g, w, log) = Make();
        g.SetRows([["a", "Zed", "False", "f"], ["b", "A", "False", "f"]]);
        g.BeginEdit(0, 1);
        Assert.Equal("Zed", ((ComboBox)g.Editor!).SelectedItem);
        g.CancelEdit();
        g.SelectCell(1, 1);
        w.KeyTextInput("c");
        Assert.True(g.IsEditing);
        Assert.Equal("C", ((ComboBox)g.Editor!).SelectedItem);
        g.CommitEdit();
        Assert.Equal([(1, 1, "C")], log);
        w.Close();
    }

    [AvaloniaFact]
    public void Up_down_in_editor_commit_and_move_and_set_rows_refocuses()
    {
        var (g, w, log) = Make();
        g.BeginEdit(0, 0, "dn");
        Key(w, Avalonia.Input.Key.Down);
        Assert.Equal([(0, 0, "dn")], log);
        Assert.Equal((1, 0), g.CurrentCell);
        g.BeginEdit(1, 0, "x");
        g.SetRows(Rows(3));
        Assert.True(g.IsFocused);
        Assert.Single(log);
        w.Close();
    }

    [AvaloniaFact]
    public void Second_set_rows_of_same_shape_reuses_cells_and_clears_state()
    {
        var (g, w, _) = Make();
        var before = g.CellControl(0, 0);
        g.SetCellError(0, 0, "bad");
        g.SetRows(Rows(3));
        Assert.Same(before, g.CellControl(0, 0));
        Assert.Null(g.CellToolTip(0, 0));
        Assert.NotEqual(EditGrid.ErrorBrush, g.CellBackground(0, 0));
        w.Close();
    }

    static (EditGrid G, Window W, List<(int, int, string)> Log) MakeEditable()
    {
        var g = new EditGrid();
        var log = new List<(int, int, string)>();
        g.CellCommitted += (r, c, t) => log.Add((r, c, t));
        g.SetColumns([new("Name"), new("Field", EditColumnKind.EditableCombo, Items: ["Fuel", "Ammo"])]);
        g.SetRows([["a", "Fuel"], ["b", ""]]);
        var w = new Window { Width = 600, Height = 400, Content = g };
        w.Show();
        g.Focus();
        return (g, w, log);
    }

    [AvaloniaFact]
    public void Editable_combo_accepts_typed_text()
    {
        var (g, w, log) = MakeEditable();
        g.BeginEdit(1, 1, "Morale");   // free text, not in the list
        Assert.NotNull(g.EditorText);
        Assert.Equal(["Fuel", "Ammo"], g.EditorList!.Items.Cast<string>().ToList());
        Key(w, Avalonia.Input.Key.Enter);
        Assert.False(g.IsEditing);
        Assert.Equal([(1, 1, "Morale")], log);
        Assert.Equal("Morale", g.Cell(1, 1));
        g.BeginEdit(0, 1);
        Assert.Equal("Fuel", g.EditorText!.Text);
        g.EditorText.Text = "Fuel2";
        Assert.True(g.CommitEdit());
        Assert.Equal("Fuel2", g.Cell(0, 1));
        w.Close();
    }

    [AvaloniaFact]
    public void Editable_combo_choice_commits()
    {
        var (g, w, log) = MakeEditable();
        g.BeginEdit(1, 1);
        g.EditorList!.SelectedItem = "Ammo";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(g.IsEditing);
        Assert.Equal([(1, 1, "Ammo")], log);
        g.SetCellItems(0, 1, ["X"]);
        g.BeginEdit(0, 1);
        Assert.Equal(["X"], g.EditorList!.Items.Cast<string>().ToList());
        g.CancelEdit();
        w.Close();
    }

    [AvaloniaFact]
    public void Editable_combo_escape_cancels()
    {
        var (g, w, log) = MakeEditable();
        g.BeginEdit(0, 1, "zzz");
        Key(w, Avalonia.Input.Key.Escape);
        Assert.False(g.IsEditing);
        Assert.Empty(log);
        Assert.Equal("Fuel", g.Cell(0, 1));
        w.Close();
    }
}
