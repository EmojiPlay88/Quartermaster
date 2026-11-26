using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Schedule editor of the rules workbench (B5 #72): the rows that bring counters in, take them out or return them on set turns, one grid row each
/// (Turn, Stage, Phase, Action, Counters, Zone map, Formula, Label). Edits go through UpdateRules; the cells commit when their edit ends.
/// </summary>
public sealed class ScheduleTab : UserControl
{
    const string MissingText = "(missing)";
    const string NoneText = "(none)";
    const string AnyStage = "";
    internal static readonly string[] ActionNames = ["Arrive", "Leave", "Return"];   // in ScheduleAction order

    readonly ModuleSession _s;
    readonly EditGrid _grid = new() { MinHeight = 200 };
    readonly TextBox _issues = new() { IsReadOnly = true, Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Height = 64, BorderThickness = new Thickness(0) };
    readonly Button _bAdd = new() { Content = "Add", Width = 50 }, _bDel = new() { Content = "Remove", Width = 56 },
        _bUp = new() { Content = "Move Up", Width = 62 }, _bDown = new() { Content = "Move Down", Width = 74 };
    bool _selfEdit;

    public ScheduleTab(ModuleSession s, IDialogs dialogs)
    {
        _s = s;
        _ = dialogs;
        _grid.SetColumns([
            new EditColumn("Turn", Weight: 30), new EditColumn("Stage", Weight: 30), new EditColumn("Phase", EditColumnKind.Combo, Weight: 70),
            new EditColumn("Action", EditColumnKind.Combo, Weight: 50), new EditColumn("Counters", Weight: 110),
            new EditColumn("Zone map", EditColumnKind.Combo, Weight: 70), new EditColumn("Formula", EditColumnKind.Combo, Weight: 70), new EditColumn("Label", Weight: 80)]);
        _grid.SetHeaderToolTip(1, "Blank = any stage.");
        _grid.SetHeaderToolTip(4, "A unit filter choosing the counters, for example Fuel > 0.");

        var btns = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var b in new[] { _bAdd, _bDel, _bUp, _bDown }) { b.Margin = new Thickness(0, 0, 2, 2); btns.Children.Add(b); }
        var dock = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(_issues, Avalonia.Controls.Dock.Bottom);
        DockPanel.SetDock(btns, Avalonia.Controls.Dock.Bottom);
        dock.Children.Add(_issues);
        dock.Children.Add(btns);
        dock.Children.Add(_grid);
        Content = dock;

        _grid.CellCommitted += (r, c, t) => Run(() => OnCell(r, c, t));
        _bAdd.Click += (_, _) => Run(AddRow);
        _bDel.Click += (_, _) => Run(RemoveRow);
        _bUp.Click += (_, _) => Run(() => Move(-1));
        _bDown.Click += (_, _) => Run(() => Move(1));
        AttachedToVisualTree += (_, _) => { _s.RulesChanged -= OnRulesChanged; _s.RulesChanged += OnRulesChanged; Run(Reload); };
        DetachedFromVisualTree += (_, _) => { Run(CommitPending); _s.RulesChanged -= OnRulesChanged; };
        Reload();
    }

    // ---------- plumbing ----------

    /// <summary>Commits an open cell edit into the pack; call before saving.</summary>
    public void CommitPending() => Run(() => _grid.CommitEdit());

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Schedule edit failed: " + ex.Message, "");
            try { Reload(); }
            catch (Exception ex2) when (ex2 is not OutOfMemoryException) { _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Schedule tab reload failed: " + ex2.Message, ""); }
        }
    }

    void OnRulesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) { if (!_selfEdit) Run(Reload); }
        else Dispatcher.UIThread.Post(() => { if (!_selfEdit) Run(Reload); });
    }

    List<ScheduleRow> Rows => _s.Rules?.Schedule ?? new List<ScheduleRow>();

    void Commit(Func<RulesPack, RulesPack> edit)
    {
        _selfEdit = true;
        try { _s.UpdateRules(edit); }
        finally { _selfEdit = false; }
    }

    /// <summary>The selected grid row (0 when none).</summary>
    int Selected => _grid.CurrentCell?.Row ?? -1;

    // ---------- loading ----------

    string PhaseText(string id) => id.Length == 0 ? "" : _s.Rules?.Phases.FirstOrDefault(p => p.Id == id)?.Name ?? MissingText;

    string MapText(string? id) =>
        string.IsNullOrWhiteSpace(id) ? NoneText : _s.Model.Maps.FirstOrDefault(m => m.Id == id)?.Name ?? id;

    string FormulaText(string? id) =>
        string.IsNullOrEmpty(id) ? NoneText : _s.Rules?.Formulas.FirstOrDefault(f => f.Id == id)?.Name ?? MissingText;

    void Reload()
    {
        var keep = Selected;
        var pack = _s.Rules;
        var rows = new List<IReadOnlyList<string>>();
        foreach (var r in Rows)
            rows.Add(new[]
            {
                r.Turn.ToString(), r.Stage?.ToString() ?? AnyStage, PhaseText(r.PhaseId), ActionNames[(int)r.Action], r.Piece,
                MapText(r.ZoneMapId), FormulaText(r.FormulaId), r.Label ?? "",
            });
        _grid.SetRows(rows);
        var phases = pack?.Phases.Select(p => p.Name).ToList() ?? new List<string>();
        var maps = new List<string> { NoneText };
        maps.AddRange(_s.Model.Maps.Select(m => m.Name));
        var formulas = new List<string> { NoneText };
        formulas.AddRange(pack?.Formulas.Select(f => f.Name) ?? []);
        for (var i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            var ph = new List<string>(phases);
            if (PhaseText(r.PhaseId) == MissingText) ph.Add(MissingText);
            _grid.SetCellItems(i, 2, ph);
            _grid.SetCellItems(i, 3, ActionNames);
            var m = new List<string>(maps);
            if (!string.IsNullOrWhiteSpace(r.ZoneMapId) && !m.Contains(MapText(r.ZoneMapId))) m.Add(MapText(r.ZoneMapId));
            _grid.SetCellItems(i, 5, m);
            var f = new List<string>(formulas);
            if (FormulaText(r.FormulaId) == MissingText) f.Add(MissingText);
            _grid.SetCellItems(i, 6, f);
            _grid.SetCellError(i, 4, FormulaEdit.SyntaxProblem(r.Piece));
        }
        if (Rows.Count > 0) _grid.SelectCell(Math.Clamp(keep, 0, Rows.Count - 1), 0);
        ShowIssues();
    }

    void ShowIssues()
    {
        if (_s.Rules is not { } pack) { _issues.Text = ""; return; }
        try
        {
            var ids = Rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var mine = RulesValidator.Validate(pack, _s.Model.Maps.Select(m => m.Id).ToList())
                .Where(i => ids.Contains(i.EntityId) || (i.EntityId == pack.Id && i.Message.StartsWith("Schedule row ", StringComparison.Ordinal)))
                .Select(i => i.Message);
            _issues.Text = string.Join(Environment.NewLine, mine);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Schedule validation failed: " + ex.Message, "");
            _issues.Text = "";
        }
    }

    // ---------- edits ----------

    void OnCell(int row, int col, string text)
    {
        if (row < 0 || row >= Rows.Count || _s.Rules is not { } pack) return;
        var id = Rows[row].Id;
        Func<ScheduleRow, ScheduleRow> edit;
        switch (col)
        {
            case 0:
                if (!int.TryParse(text.Trim(), out var turn)) { Reload(); return; }
                var t = Math.Clamp(turn, 1, GameEndEdit.MaxTurn);
                edit = r => r with { Turn = t };
                break;
            case 1:
                int? stage = int.TryParse(text.Trim(), out var st) ? Math.Clamp(st, 1, GameEndEdit.MaxTurn) : null;
                edit = r => r with { Stage = stage };
                break;
            case 2:
                if (text == MissingText || pack.Phases.FirstOrDefault(p => p.Name == text) is not { } phase) return;
                edit = r => r with { PhaseId = phase.Id };
                break;
            case 3:
                var at = Array.IndexOf(ActionNames, text);
                if (at < 0) return;
                edit = r => r with { Action = (ScheduleAction)at };
                break;
            case 4:
                _grid.SetCellError(row, col, FormulaEdit.SyntaxProblem(text));
                var piece = text.Trim();
                edit = r => r with { Piece = piece };
                break;
            case 5:
                var map = text == NoneText ? null : _s.Model.Maps.FirstOrDefault(m => m.Name == text)?.Id ?? text;
                edit = r => r with { ZoneMapId = map };
                break;
            case 6:
                if (text == MissingText) return;
                var formula = text == NoneText ? null : pack.Formulas.FirstOrDefault(f => f.Name == text)?.Id;
                edit = r => r with { FormulaId = formula };
                break;
            case 7:
                edit = r => r with { Label = text };
                break;
            default: return;
        }
        Commit(p => ScheduleEdit.Update(p, id, edit));
        Reload();
    }

    void AddRow()
    {
        _grid.CommitEdit();
        if (_s.Rules == null) return;
        var at = Selected < 0 ? Rows.Count : Selected + 1;
        Commit(p => ScheduleEdit.Add(p, at));
        Reload();
        _grid.SelectCell(at, 0);
    }

    void RemoveRow()
    {
        _grid.CommitEdit();
        var i = Selected;
        if (i < 0 || i >= Rows.Count) return;
        Commit(p => ScheduleEdit.Remove(p, i));
        Reload();
    }

    void Move(int delta)
    {
        _grid.CommitEdit();
        var i = Selected;
        if (i < 0) return;
        Commit(p => ScheduleEdit.Move(p, i, delta));
        Reload();
        if (i + delta >= 0 && i + delta < Rows.Count) _grid.SelectCell(i + delta, 0);
    }

    /// <summary>Selects a row by id (no-op when unknown).</summary>
    public void SelectRow(string id)
    {
        var i = Rows.FindIndex(r => r.Id == id);
        if (i >= 0) _grid.SelectCell(i, 0);
    }

    // test hooks
    internal EditGrid Grid => _grid;
    internal TextBox IssuesText => _issues;
    internal Button AddButton => _bAdd;
    internal Button RemoveButton => _bDel;
    internal Button UpButton => _bUp;
    internal Button DownButton => _bDown;
}
