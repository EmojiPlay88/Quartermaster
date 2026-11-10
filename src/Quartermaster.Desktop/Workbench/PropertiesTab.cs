using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Properties and holders of the rules workbench (port of WinForms PropertiesTab). Ids are fixed at creation; formulas refer to them, so renaming
/// never breaks a formula. Cells commit through UpdateRules when their edit ends; a refused edit (guest, replay) redraws from the pack.
/// </summary>
public sealed class PropertiesTab : UserControl
{
    const string SheetTip = "A property-sheet row name from the module's pieces; seeds the property from that row.";
    const string OutlinerTip = "How the outliner totals this property per group: None hides it; Sum, Min or Average show a column.";

    readonly ModuleSession _s;
    readonly IDialogs _dialogs;
    readonly EditGrid _props = new();
    readonly EditGrid _holders = new();
    readonly TextBlock _status = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2) };
    readonly Button _bAddProp = new() { Content = "Add property" }, _bDelProp = new() { Content = "Delete property" };
    readonly Button _bAddHolder = new() { Content = "Add holder" }, _bDelHolder = new() { Content = "Delete holder" };
    readonly List<string> _propIds = new(), _holderIds = new();
    IReadOnlyList<string>? _fields;
    bool _selfEdit;

    public PropertiesTab(ModuleSession s, IDialogs dialogs)
    {
        _s = s;
        _dialogs = dialogs;
        _props.SetColumns(
        [
            new EditColumn("Name", Weight: 90),
            new EditColumn("Scope", EditColumnKind.Combo, Weight: 60, Items: Enum.GetNames<PropertyScope>()),
            new EditColumn("Default", Weight: 50),
            new EditColumn("SheetField", EditColumnKind.Combo, Weight: 80),
            new EditColumn("AppliesTo", Weight: 120),
            new EditColumn("Outliner", EditColumnKind.Combo, Weight: 60, Items: Enum.GetNames<OutlinerAggregate>()),
            new EditColumn("Notes", Weight: 160),
        ]);
        _holders.SetColumns([new EditColumn("Name", Weight: 80), new EditColumn("Notes", Weight: 200)]);
        _props.SetHeaderToolTip(3, SheetTip);
        _props.SetHeaderToolTip(5, OutlinerTip);

        var propsBox = Box("Properties", _props, Bar(_bAddProp, _bDelProp));
        var holdersBox = Box("Holders (the built-in \"global\" holder always exists)", _holders, Bar(_bAddHolder, _bDelHolder));
        var body = new Grid { RowDefinitions = new RowDefinitions("6*,4*") };
        body.Children.Add(propsBox);
        Grid.SetRow(holdersBox, 1);
        body.Children.Add(holdersBox);
        var root = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        _props.CellCommitted += (r, c, t) => Run(() => OnPropertyCell(r, c, t));
        _holders.CellCommitted += (r, c, t) => Run(() => OnHolderCell(r, c, t));
        _bAddProp.Click += (_, _) => Run(AddProperty);
        _bAddHolder.Click += (_, _) => Run(AddHolder);
        _bDelProp.Click += (_, _) => Run(DeletePropertyAsync);
        _bDelHolder.Click += (_, _) => Run(DeleteHolderAsync);

        AttachedToVisualTree += (_, _) =>
        {
            _s.RulesChanged -= OnRulesChanged; _s.RulesChanged += OnRulesChanged;
            _s.GameReplaced -= OnGameReplaced; _s.GameReplaced += OnGameReplaced;
            _s.ViewChanged -= OnViewChanged; _s.ViewChanged += OnViewChanged;
            _fields = null;
            Run(Rebuild);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            Run(CommitPendingCore);   // a hidden tab is detached: an open cell edit must reach the pack first
            _s.RulesChanged -= OnRulesChanged;
            _s.GameReplaced -= OnGameReplaced;
            _s.ViewChanged -= OnViewChanged;
        };
        Rebuild();
    }

    // ---------- layout helpers ----------

    static Control Bar(params Button[] buttons)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var b in buttons) { b.Margin = new Thickness(0, 0, 2, 2); w.Children.Add(b); }
        return w;
    }

    static Control Box(string title, Control grid, Control bar)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = title, Margin = new Thickness(2, 0, 0, 2) });
        head.Children.Add(bar);
        DockPanel.SetDock(head, Dock.Top);
        d.Children.Add(head);
        d.Children.Add(grid);
        return d;
    }

    // ---------- plumbing ----------

    Window? Owner => TopLevel.GetTopLevel(this) as Window;

    public void SelectProperty(string id)
    {
        var i = _propIds.IndexOf(id);
        if (i >= 0) _props.SelectCell(i, 0);
    }

    public void SelectHolder(string id)
    {
        var i = _holderIds.IndexOf(id);
        if (i >= 0) _holders.SelectCell(i + 1, 0);
    }

    /// <summary>Commits an open cell edit into the pack; call before saving.</summary>
    public void CommitPending() => Run(CommitPendingCore);

    void CommitPendingCore()
    {
        _props.CommitEdit();
        _holders.CommitEdit();
    }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed(ex); }
    }

    async void Run(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed(ex); }
    }

    /// <summary>Logs the failure and redraws from the pack, so a refused edit never leaves text the pack does not hold.</summary>
    void Failed(Exception ex)
    {
        _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Property edit failed: " + ex.Message, "");
        try { Rebuild(); }
        catch (Exception ex2) when (ex2 is not OutOfMemoryException) { _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Properties tab reload failed: " + ex2.Message, ""); }
    }

    void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) Run(a);
        else Dispatcher.UIThread.Post(() => Run(a));
    }

    void OnRulesChanged() => OnUi(() => { if (!_selfEdit) Rebuild(); });

    void OnGameReplaced() => OnUi(() => { _fields = null; Rebuild(); });

    // Hot-seat: the sheet fields come from the new player's view (a view change also follows every own edit; those are skipped).
    void OnViewChanged() => OnUi(() =>
    {
        if (_selfEdit) return;
        _fields = null;
        ApplySheetItems();
    });

    void Commit(Func<RulesPack, RulesPack> edit)
    {
        _selfEdit = true;
        try { _s.UpdateRules(edit); }
        finally { _selfEdit = false; }
    }

    // ---------- build ----------

    void Rebuild()
    {
        var pack = _s.Rules;
        _fields ??= SafeFields();
        _propIds.Clear(); _holderIds.Clear();
        _props.IsEnabled = _holders.IsEnabled = pack != null;
        foreach (var b in new[] { _bAddProp, _bDelProp, _bAddHolder, _bDelHolder }) b.IsEnabled = pack != null;
        var props = pack?.Properties ?? new List<PropertyDef>();
        _props.SetRows(props.Select(p => (IReadOnlyList<string>)new[]
        {
            p.Name, p.Scope.ToString(), p.Default.ToString("R", CultureInfo.InvariantCulture), p.SheetField ?? "", p.AppliesTo ?? "", p.Outliner.ToString(), p.Notes ?? "",
        }).ToList());
        for (var i = 0; i < props.Count; i++)
        {
            _propIds.Add(props[i].Id);
            _props.SetCellToolTip(i, 3, SheetTip);
            _props.SetCellToolTip(i, 5, OutlinerTip);
            _props.SetCellError(i, 4, FormulaEdit.SyntaxProblem(props[i].AppliesTo));
        }
        ApplySheetItems();
        var rows = new List<IReadOnlyList<string>> { new[] { HolderDef.GlobalId, "Built in; always exists." } };
        foreach (var h in pack?.Holders ?? new List<HolderDef>())
        {
            rows.Add(new[] { h.Name, h.Notes ?? "" });
            _holderIds.Add(h.Id);
        }
        _holders.SetRows(pack == null ? [] : rows);
        if (pack != null) { _holders.SetCellReadOnly(0, 0, true); _holders.SetCellReadOnly(0, 1, true); }
    }

    IReadOnlyList<string> SafeFields()
    {
        try { return SheetFieldCatalog.Collect(_s.Model, _s.View); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Sheet field scan failed: " + ex.Message, "");
            return Array.Empty<string>();
        }
    }

    void ApplySheetItems()
    {
        _fields ??= SafeFields();
        for (var i = 0; i < _props.RowCount; i++)
        {
            var value = _props.Cell(i, 3);
            var items = new List<string> { "" };
            items.AddRange(_fields);
            if (value.Length > 0 && !items.Contains(value)) items.Add(value);
            _props.SetCellItems(i, 3, items);
        }
    }

    // ---------- edits ----------

    void OnPropertyCell(int row, int col, string text)
    {
        if (row < 0 || row >= _propIds.Count || _s.Rules is not { } pack) return;
        var id = _propIds[row];
        _status.Text = "";
        switch (col)
        {
            case 0:
                if (NameRules.Problem(pack.Properties.Where(x => x.Id != id).Select(x => x.Name), text, null) is { } bad)
                {
                    _status.Text = bad + " The name was not changed.";
                    Rebuild();
                    return;
                }
                Commit(p => Edit(p, id, x => x with { Name = text.Trim() }));
                break;
            case 1:
                if (!Enum.TryParse<PropertyScope>(text, out var sc)) return;
                Commit(p => Edit(p, id, x => x with { Scope = sc }));
                break;
            case 2:
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                {
                    _status.Text = "The default must be a number (use a dot for decimals).";
                    Rebuild();
                    return;
                }
                Commit(p => Edit(p, id, x => x with { Default = d }));
                break;
            case 3: Commit(p => Edit(p, id, x => x with { SheetField = text.Trim().Length == 0 ? null : text.Trim() })); break;
            case 4:
                Commit(p => Edit(p, id, x => x with { AppliesTo = text.Trim().Length == 0 ? null : text }));
                var problem = FormulaEdit.SyntaxProblem(text);
                if (problem != null) _status.Text = "Applies to: " + problem;
                _props.SetCellError(row, 4, problem);
                break;
            case 5:
                if (!Enum.TryParse<OutlinerAggregate>(text, out var oa)) return;
                Commit(p => Edit(p, id, x => x with { Outliner = oa }));
                break;
            case 6: Commit(p => Edit(p, id, x => x with { Notes = text.Length == 0 ? null : text })); break;
        }
    }

    static RulesPack Edit(RulesPack p, string id, Func<PropertyDef, PropertyDef> f) =>
        p with { Properties = p.Properties.Select(x => x.Id == id ? f(x) : x).ToList() };

    void OnHolderCell(int row, int col, string text)
    {
        if (row < 1 || row - 1 >= _holderIds.Count || _s.Rules is not { } pack) return;
        var id = _holderIds[row - 1];
        _status.Text = "";
        if (col == 0 && NameRules.Problem(pack.Holders.Where(x => x.Id != id).Select(x => x.Name).Append(HolderDef.GlobalId), text, null) is { } bad)
        {
            _status.Text = bad + " The name was not changed.";
            Rebuild();
            return;
        }
        Commit(p => p with
        {
            Holders = p.Holders.Select(h => h.Id != id ? h : col == 0 ? h with { Name = text.Trim() } : h with { Notes = text.Length == 0 ? null : text }).ToList(),
        });
    }

    void AddProperty()
    {
        CommitPendingCore();
        if (_s.Rules is not { } pack) return;
        var p = new PropertyDef { Id = Guid.NewGuid().ToString("N"), Name = WorkbenchRules.UniqueName(pack.Properties.Select(x => x.Name), "Property"), Scope = PropertyScope.Pieces };
        Commit(x => x with { Properties = x.Properties.Append(p).ToList() });
        Rebuild();
        SelectProperty(p.Id);
    }

    void AddHolder()
    {
        CommitPendingCore();
        if (_s.Rules is not { } pack) return;
        var h = new HolderDef { Id = Guid.NewGuid().ToString("N"), Name = WorkbenchRules.UniqueName(pack.Holders.Select(x => x.Name), "Holder") };
        Commit(x => x with { Holders = x.Holders.Append(h).ToList() });
        Rebuild();
        SelectHolder(h.Id);
    }

    async Task DeletePropertyAsync()
    {
        CommitPendingCore();
        if (_s.Rules is not { } pack || _props.CurrentCell is not { } c || c.Row < 0 || c.Row >= _propIds.Count || Owner is not { } w) return;
        var id = _propIds[c.Row];
        var prop = pack.ById<PropertyDef>(id);
        if (prop == null) return;
        if (!await _dialogs.AskYesNoAsync(w, WorkbenchRules.DeleteQuestion("property", prop.Name, FormulaEdit.FormulasUsingProperty(pack, prop.Id)), "Delete property", defaultNo: true)) return;
        // the dialog is modal but the pack can still change (host edit, replay): act only if the property is still there and still the current row
        if (_s.Rules?.ById<PropertyDef>(id) == null || _props.CurrentCell is not { } now || now.Row >= _propIds.Count || _propIds[now.Row] != id) return;
        Commit(p => p with { Properties = p.Properties.Where(x => x.Id != id).ToList() });
        Rebuild();
    }

    async Task DeleteHolderAsync()
    {
        CommitPendingCore();
        if (_s.Rules is not { } pack || _holders.CurrentCell is not { } c || c.Row < 1 || c.Row - 1 >= _holderIds.Count || Owner is not { } w) return;
        var id = _holderIds[c.Row - 1];
        var holder = pack.ById<HolderDef>(id);
        if (holder == null) return;
        if (!await _dialogs.AskYesNoAsync(w, WorkbenchRules.DeleteQuestion("holder", holder.Name, FormulaEdit.FormulasUsingHolder(pack, holder.Id)), "Delete holder", defaultNo: true)) return;
        if (_s.Rules?.ById<HolderDef>(id) == null || _holders.CurrentCell is not { } now || now.Row < 1 || now.Row - 1 >= _holderIds.Count || _holderIds[now.Row - 1] != id) return;
        Commit(p => p with { Holders = p.Holders.Where(x => x.Id != id).ToList() });
        Rebuild();
    }

    // test hooks
    internal EditGrid PropertyGrid => _props;
    internal EditGrid HolderGrid => _holders;
    internal TextBlock Status => _status;
    internal Button AddPropertyButton => _bAddProp;
    internal Button DeletePropertyButton => _bDelProp;
    internal Button AddHolderButton => _bAddHolder;
    internal Button DeleteHolderButton => _bDelHolder;
}
