using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Visibility editor of the rules workbench (port of WinForms VisibilityTab): which properties are private, which side owns each holder ledger and each
/// map or org display, and which formulas roll private dice. Every change commits at once through UpdateRules.
/// </summary>
public sealed class VisibilityTab : UserControl
{
    const string NoOwner = "(none)";
    const string MissingText = "(missing)";

    readonly ModuleSession _s;
    readonly EditGrid _props = new();
    readonly EditGrid _holders = new();
    readonly EditGrid _maps = new();
    readonly StackPanel _formulas = new();
    readonly TextBlock _issues = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Height = 44, Margin = new Thickness(4, 2) };
    readonly List<string> _propIds = new(), _holderIds = new(), _mapIds = new(), _formulaIds = new(), _sideIds = new();
    readonly List<List<string>> _holderNames = new(), _mapNames = new();   // per row: the owner choices (none, the sides, maybe "(missing)")
    bool _selfEdit, _loading;

    public VisibilityTab(ModuleSession s)
    {
        _s = s;
        _props.SetColumns([new EditColumn("Property", ReadOnly: true), new EditColumn("Visibility", EditColumnKind.Combo, Width: 110, Items: [nameof(Visibility.Private), nameof(Visibility.Public)])]);
        _holders.SetColumns([new EditColumn("Holder", ReadOnly: true), new EditColumn("Owner side", EditColumnKind.Combo, Width: 160)]);
        _maps.SetColumns([new EditColumn("Map or org display", ReadOnly: true), new EditColumn("Owner side", EditColumnKind.Combo, Width: 160)]);

        var table = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("*,*"), Margin = new Thickness(4) };
        void Put(Control c, int col, int row) { Grid.SetColumn(c, col); Grid.SetRow(c, row); table.Children.Add(c); }
        Put(Box("Properties (private: only the owning side's players see the value)", _props), 0, 0);
        Put(Box("Holders (owner side; none = public)", _holders), 1, 0);
        Put(Box("Maps and org displays (owner side; none = public)", _maps), 0, 1);
        Put(Box("Formulas (tick \"Private dice\" to show rolls only to the rolling side)", new ScrollViewer { Content = _formulas }), 1, 1);
        var root = new DockPanel();
        DockPanel.SetDock(_issues, Dock.Bottom);
        root.Children.Add(_issues);
        root.Children.Add(table);
        Content = root;

        _props.CellCommitted += (r, c, t) => Run(() => OnPropChanged(r, c, t));
        _holders.CellCommitted += (r, c, t) => Run(() => OnOwnerChanged(_holderIds, _holderNames, r, c, t, VisibilityEdit.SetHolderOwner));
        _maps.CellCommitted += (r, c, t) => Run(() => OnOwnerChanged(_mapIds, _mapNames, r, c, t, VisibilityEdit.SetMapOwner));

        AttachedToVisualTree += (_, _) =>
        {
            _s.RulesChanged -= OnRulesChanged; _s.RulesChanged += OnRulesChanged;
            Run(Reload);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Run(CommitPendingCore);
            _s.RulesChanged -= OnRulesChanged;
        };
        Reload();
    }

    static Control Box(string title, Control content)
    {
        var d = new DockPanel { Margin = new Thickness(2) };
        DockPanel.SetDock(d, Dock.Top);
        var head = new TextBlock { Text = title, Margin = new Thickness(2, 0, 0, 2), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(head, Dock.Top);
        d.Children.Add(head);
        d.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(2), Child = content });
        return d;
    }

    // ---------- plumbing ----------

    /// <summary>Commits an open cell edit into the pack; call before saving.</summary>
    public void CommitPending() => Run(CommitPendingCore);

    void CommitPendingCore()
    {
        _props.CommitEdit();
        _holders.CommitEdit();
        _maps.CommitEdit();
    }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed(ex); }
    }

    /// <summary>Logs the failure and redraws from the pack, so a refused edit (guest, replay) never leaves a choice the pack does not hold.</summary>
    void Failed(Exception ex)
    {
        _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Visibility edit failed: " + ex.Message, "");
        try { Reload(); }
        catch (Exception ex2) when (ex2 is not OutOfMemoryException) { _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Visibility tab reload failed: " + ex2.Message, ""); }
    }

    void OnRulesChanged()
    {
        void Go() { if (!_selfEdit) Run(Reload); }
        if (Dispatcher.UIThread.CheckAccess()) Go();
        else Dispatcher.UIThread.Post(Go);
    }

    void Commit(Func<RulesPack, RulesPack> edit)
    {
        _selfEdit = true;
        try { _s.UpdateRules(edit); }
        finally { _selfEdit = false; }
        ShowIssues();
    }

    // ---------- loading ----------

    void Reload()
    {
        _loading = true;
        try
        {
            var pack = _s.Rules;
            _sideIds.Clear();
            var sideNames = new List<string> { NoOwner };
            if (pack != null) foreach (var sd in pack.Sides) { _sideIds.Add(sd.Id); sideNames.Add(sd.Name); }

            _propIds.Clear();
            var props = pack?.Properties ?? new List<PropertyDef>();
            _props.SetRows(props.Select(p => (IReadOnlyList<string>)new[] { p.Name, p.Visibility.ToString() }).ToList());
            foreach (var p in props) _propIds.Add(p.Id);

            FillOwnerGrid(_holders, _holderIds, _holderNames, (pack?.Holders ?? new()).Select(h => (h.Id, h.Name, h.SideId)), sideNames);

            var maps = _s.Model.Maps.Select(m => (m.Id, m.IsOrgDisplay ? m.Name + " (org display)" : m.Name, pack == null ? null : VisibilityEdit.MapOwnerOf(pack, m.Id)));
            // owners naming a map the module lacks stay visible so the user can clear them
            var extra = (pack?.MapOwners ?? new()).Where(o => _s.Model.Maps.All(m => m.Id != o.MapId)).Select(o => (o.MapId, o.MapId + " (not in module)", (string?)o.SideId));
            FillOwnerGrid(_maps, _mapIds, _mapNames, maps.Concat(extra), sideNames);

            _formulas.Children.Clear();
            _formulaIds.Clear();
            foreach (var f in pack?.Formulas ?? new List<FormulaDef>())
            {
                _formulaIds.Add(f.Id);
                var id = f.Id;
                var box = new CheckBox { Content = f.Name + "  [Private dice]", IsChecked = f.PrivateRolls };
                box.IsCheckedChanged += (_, _) => Run(() => OnFormulaCheck(id, box.IsChecked == true));
                _formulas.Children.Add(box);
            }
        }
        finally { _loading = false; }
        ShowIssues();
    }

    void FillOwnerGrid(EditGrid grid, List<string> ids, List<List<string>> choices, IEnumerable<(string Id, string Name, string? SideId)> rows, List<string> sideNames)
    {
        ids.Clear();
        choices.Clear();
        var list = rows.ToList();
        var shownRows = new List<IReadOnlyList<string>>();
        foreach (var (id, name, side) in list)
        {
            var names = new List<string>(sideNames);
            var shown = NoOwner;
            if (!string.IsNullOrEmpty(side))
            {
                var k = _sideIds.IndexOf(side);
                if (k >= 0) shown = sideNames[k + 1];
                else { names.Add(MissingText); shown = MissingText; }
            }
            ids.Add(id);
            choices.Add(names);
            shownRows.Add(new[] { name, shown });
        }
        grid.SetRows(shownRows);
        for (var i = 0; i < choices.Count; i++) grid.SetCellItems(i, 1, choices[i]);
    }

    void ShowIssues()
    {
        if (_s.Rules is not { } pack) { _issues.Text = ""; return; }
        try
        {
            var ids = _s.Model.Maps.Select(m => m.Id).ToList();
            var mine = RulesValidator.Validate(pack, ids).Where(i => i.Message.StartsWith("Map owner", StringComparison.Ordinal) || (i.Message.StartsWith("Holder", StringComparison.Ordinal) && i.Message.Contains("side", StringComparison.Ordinal))).Select(i => i.Message);
            _issues.Text = string.Join("  |  ", mine);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Visibility validation failed: " + ex.Message, "");
            _issues.Text = "";
        }
    }

    // ---------- edits ----------

    void OnPropChanged(int row, int col, string text)
    {
        if (_loading || row < 0 || row >= _propIds.Count || col != 1) return;
        if (!Enum.TryParse<Visibility>(text, out var v)) return;
        var id = _propIds[row];
        Commit(p => VisibilityEdit.SetPropertyVisibility(p, id, v));
    }

    void OnOwnerChanged(List<string> ids, List<List<string>> choices, int row, int col, string shown, Func<RulesPack, string, string?, RulesPack> set)
    {
        if (_loading || row < 0 || row >= ids.Count || col != 1) return;
        if (shown == MissingText) return;   // leave a dangling owner until the user picks something real
        var id = ids[row];
        var k = choices[row].IndexOf(shown);
        string? side = null;
        if (shown != NoOwner && k > 0 && k - 1 < _sideIds.Count) side = _sideIds[k - 1];
        Commit(p => set(p, id, side));
    }

    void OnFormulaCheck(string id, bool on)
    {
        if (_loading) return;
        Commit(p => VisibilityEdit.SetPrivateRolls(p, id, on));
    }

    // test hooks
    internal EditGrid PropertyGrid => _props;
    internal EditGrid HolderGrid => _holders;
    internal EditGrid MapGrid => _maps;
    internal IReadOnlyList<CheckBox> FormulaChecks => _formulas.Children.OfType<CheckBox>().ToList();
    internal TextBlock Issues => _issues;
}
