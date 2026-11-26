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
    const string ExpressionTip = "An expression makes the property computed: its value is worked out whenever it is read and never stored. Empty = a stored value.";
    internal const string ComputedIgnoredTip = "Ignored: the property is computed from its Expression.";
    /// <summary>Column index of Expression (after Notes, so the older columns keep their places).</summary>
    internal const int ExpressionColumn = 7;
    /// <summary>Column index of "Overrides table" and its "Column" (after Expression).</summary>
    internal const int OverridesTableColumn = 8, OverridesColumnColumn = 9;
    const string OverridesTableTip = "A table this property overrides: a formula, filter or warning reading that table at the row named like the piece reads the piece's own value instead of the printed cell. Blank = none.";
    const string OverridesColumnTip = "The column of the table it overrides. A one-dimension table has no column.";
    /// <summary>Column indexes of "Rule ref", "Labels", "Holders" and "Clone as" (after Column, so the older columns keep their places).</summary>
    internal const int RuleRefColumn = 10, LabelsColumn = 11, HoldersColumn = 12, CloneAsColumn = 13, StartingValueColumn = 14, ResetsAtColumn = 15;
    /// <summary>Column index of "Keyed by" (after Resets at).</summary>
    internal const int KeyedByColumn = 16;
    internal const string KeyedByTip = "Keep one value per key instead of one value: per row of a table, per turn (keys of past turns are dropped when a new turn starts) or per hex (place values, kept by the Global holder). Read a value with keyed(Property, key); an output's Key chooses the key it writes.";
    /// <summary>Column index of the holder grid's "Rule ref" (after Notes).</summary>
    internal const int HolderRuleRefColumn = 2;
    internal const string LabelsTip = "A one-column table whose row for a value gives the text shown next to it, e.g. \"3 (Fog)\". Values stay numbers. Blank = none.";
    internal const string HoldersTip = "Holders that keep this property, comma-separated names. Blank = every holder.";
    internal const string StartingValueTip = "An expression worked out once for each counter that gets this property and has no sheet value, e.g. lookup(CrewSizes, marker(\"Kind\")). It may read the counter, globals and tables; no dice. Blank = Default.";
    internal const string ResetsAtTip = "Phases that clear this property's values when they begin, comma-separated names. Counters then start again from their starting value. Blank = never.";
    internal const string CloneAsTip = "What a copy made with the counter's Clone command gets: Copy = this counter's current value; Default = what a new counter starts with.";

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
            new EditColumn("SheetField", EditColumnKind.EditableCombo, Weight: 80),
            new EditColumn("AppliesTo", Weight: 120),
            new EditColumn("Outliner", EditColumnKind.Combo, Weight: 60, Items: Enum.GetNames<OutlinerAggregate>()),
            new EditColumn("Notes", Weight: 160),
            new EditColumn("Expression", Weight: 120),
            new EditColumn("Overrides table", EditColumnKind.Combo, Weight: 90),
            new EditColumn("Column", EditColumnKind.Combo, Weight: 70),
            new EditColumn("Rule ref", EditColumnKind.Combo, Weight: 90),
            new EditColumn("Labels", EditColumnKind.Combo, Weight: 80),
            new EditColumn("Holders", Weight: 90),
            new EditColumn("Clone as", EditColumnKind.Combo, Weight: 60, Items: Enum.GetNames<CloneValue>()),
            new EditColumn("Starting value", Weight: 120),
            new EditColumn("Resets at", Weight: 90),
            new EditColumn("Keyed by", EditColumnKind.Combo, Weight: 90),
        ]);
        _holders.SetColumns([new EditColumn("Name", Weight: 80), new EditColumn("Notes", Weight: 200), new EditColumn("Rule ref", EditColumnKind.Combo, Weight: 100)]);
        _props.SetHeaderToolTip(3, SheetTip);
        _props.SetHeaderToolTip(5, OutlinerTip);
        _props.SetHeaderToolTip(ExpressionColumn, ExpressionTip);
        _props.SetHeaderToolTip(OverridesTableColumn, OverridesTableTip);
        _props.SetHeaderToolTip(OverridesColumnColumn, OverridesColumnTip);
        _props.SetHeaderToolTip(LabelsColumn, LabelsTip);
        _props.SetHeaderToolTip(HoldersColumn, HoldersTip);
        _props.SetHeaderToolTip(CloneAsColumn, CloneAsTip);
        _props.SetHeaderToolTip(StartingValueColumn, StartingValueTip);
        _props.SetHeaderToolTip(ResetsAtColumn, ResetsAtTip);
        _props.SetHeaderToolTip(KeyedByColumn, KeyedByTip);

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
            p.Expr ?? "",
            string.IsNullOrWhiteSpace(p.OverridesTable) ? "" : pack!.ById<TableDef>(p.OverridesTable!)?.Name ?? p.OverridesTable!,   // a deleted table shows its id
            p.OverridesColumn ?? "",
            RuleRefPicker.Text(pack, p.RuleRefId),
            string.IsNullOrWhiteSpace(p.Labels) ? "" : pack!.ById<TableDef>(p.Labels!)?.Name ?? p.Labels!,   // a deleted table shows its id
            HoldersText(pack!, p.Holders),
            p.CloneAs.ToString(),
            p.DefaultExpr ?? "",
            ResetAtText(pack!, p.ResetAt),
            KeyedFacts.Display(pack!, p.KeyedBy),
        }).ToList());
        var tableNames = new List<string> { "" };
        tableNames.AddRange((pack?.Tables ?? new List<TableDef>()).Select(t => t.Name));
        // Labels come from a one-column table (one dimension or dice).
        var labelTables = new List<string> { "" };
        labelTables.AddRange((pack?.Tables ?? new List<TableDef>()).Where(t => t.Kind != TableKind.TwoD).Select(t => t.Name));
        var keyedByItems = new List<string> { "", "Turn", "Hex (place)" };
        keyedByItems.AddRange((pack?.Tables ?? new List<TableDef>()).Select(t => "Table: " + t.Name));
        for (var i = 0; i < props.Count; i++)
        {
            _propIds.Add(props[i].Id);
            _props.SetCellToolTip(i, 3, SheetTip);
            _props.SetCellToolTip(i, 5, OutlinerTip);
            _props.SetCellError(i, 4, FormulaEdit.SyntaxProblem(props[i].AppliesTo));
            _props.SetCellToolTip(i, ExpressionColumn, ExpressionTip);
            _props.SetCellError(i, ExpressionColumn, FormulaEdit.SyntaxProblem(props[i].Expr));
            ShowComputed(i, PropertyFacts.IsComputed(props[i]));
            _props.SetCellItems(i, OverridesTableColumn, tableNames);
            _props.SetCellToolTip(i, OverridesTableColumn, OverridesTableTip);
            ShowOverrideColumn(i, pack!, props[i]);
            _props.SetCellItems(i, RuleRefColumn, RuleRefPicker.Items(pack, props[i].RuleRefId).Items);
            var labels = labelTables;
            if (_props.Cell(i, LabelsColumn) is { Length: > 0 } shownLabels && !labels.Contains(shownLabels)) labels = labels.Append(shownLabels).ToList();
            _props.SetCellItems(i, LabelsColumn, labels);
            _props.SetCellToolTip(i, LabelsColumn, LabelsTip);
            _props.SetCellToolTip(i, HoldersColumn, HoldersTip);
            _props.SetCellItems(i, CloneAsColumn, Enum.GetNames<CloneValue>());
            _props.SetCellToolTip(i, CloneAsColumn, CloneAsTip);
            _props.SetCellToolTip(i, StartingValueColumn, StartingValueTip);
            _props.SetCellError(i, StartingValueColumn, FormulaEdit.SyntaxProblem(props[i].DefaultExpr));
            _props.SetCellToolTip(i, ResetsAtColumn, ResetsAtTip);
            var keyedBy = keyedByItems;
            if (_props.Cell(i, KeyedByColumn) is { Length: > 0 } shownKey && !keyedBy.Contains(shownKey)) keyedBy = keyedBy.Append(shownKey).ToList();   // a malformed value shows as written
            _props.SetCellItems(i, KeyedByColumn, keyedBy);
            _props.SetCellToolTip(i, KeyedByColumn, KeyedByTip);
        }
        ApplySheetItems();
        var rows = new List<IReadOnlyList<string>> { new[] { HolderDef.GlobalId, "Built in; always exists.", "" } };
        foreach (var h in pack?.Holders ?? new List<HolderDef>())
        {
            rows.Add(new[] { h.Name, h.Notes ?? "", RuleRefPicker.Text(pack, h.RuleRefId) });
            _holderIds.Add(h.Id);
        }
        _holders.SetRows(pack == null ? [] : rows);
        if (pack != null)
        {
            for (var c = 0; c <= HolderRuleRefColumn; c++) _holders.SetCellReadOnly(0, c, true);   // the built-in holder is never stored in a pack
            for (var i = 0; i < pack.Holders.Count; i++) _holders.SetCellItems(i + 1, HolderRuleRefColumn, RuleRefPicker.Items(pack, pack.Holders[i].RuleRefId).Items);
        }
    }

    /// <summary>The Holders cell: holder names, comma-separated ("global" for the built-in holder; an id that no longer resolves shows as it is).</summary>
    static string HoldersText(RulesPack pack, List<string>? ids) =>
        ids is not { Count: > 0 } ? "" : FormulaEdit.FormatNameList(ids.Select(id => id == HolderDef.GlobalId ? HolderDef.GlobalId : pack.ById<HolderDef>(id)?.Name ?? id));

    /// <summary>The Resets at cell: phase names, comma-separated (an id that no longer resolves shows as it is).</summary>
    static string ResetAtText(RulesPack pack, List<string>? ids) =>
        ids is not { Count: > 0 } ? "" : FormulaEdit.FormatNameList(ids.Select(id => pack.ById<PhaseDef>(id)?.Name ?? id));

    /// <summary>Phase ids from the Resets at cell text (case-insensitive names); an unknown name gives its status text instead. Blank = null.</summary>
    static (List<string>? Ids, string? Error) ParseResetAt(RulesPack pack, string text, List<string>? current)
    {
        var (names, err) = FormulaEdit.ParseNameList(text);
        if (err != null) return (null, err);
        var ids = new List<string>();
        foreach (var raw in names!)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var id = pack.Phases.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Id
                ?? (current?.Contains(name) == true ? name : null);   // a stale id shown as it is may stay
            if (id == null) return (null, $"No phase named \"{name}\"");
            if (!ids.Contains(id)) ids.Add(id);
        }
        return (ids.Count == 0 ? null : ids, null);
    }

    /// <summary>Holder ids from the Holders cell text; an unknown name gives its status text instead. Blank = null (every holder).</summary>
    static (List<string>? Ids, string? Error) ParseHolders(RulesPack pack, string text, List<string>? current)
    {
        var (names, err) = FormulaEdit.ParseNameList(text);
        if (err != null) return (null, err);
        var ids = new List<string>();
        foreach (var raw in names!)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            string? id = string.Equals(name, HolderDef.GlobalId, StringComparison.OrdinalIgnoreCase) ? HolderDef.GlobalId
                : pack.HolderByName(name)?.Id ?? (current?.Contains(name) == true ? name : null);   // a stale id shown as it is may stay
            if (id == null) return (null, $"No holder named \"{name}\"");
            if (!ids.Contains(id)) ids.Add(id);
        }
        return (ids.Count == 0 ? null : ids, null);
    }

    /// <summary>A computed property's Default and Sheet field are ignored: those cells are greyed and closed to editing.</summary>
    void ShowComputed(int row, bool computed)
    {
        _props.SetCellReadOnly(row, 2, computed);
        _props.SetCellReadOnly(row, 3, computed);
        _props.SetCellToolTip(row, 2, computed ? ComputedIgnoredTip : null);
        _props.SetCellToolTip(row, 3, computed ? ComputedIgnoredTip : SheetTip);
    }

    /// <summary>The Column picker lists the overridden table's columns; it is closed when there is no table or a one-dimension one.</summary>
    void ShowOverrideColumn(int row, RulesPack pack, PropertyDef p)
    {
        var t = string.IsNullOrWhiteSpace(p.OverridesTable) ? null : pack.ById<TableDef>(p.OverridesTable!);
        var twoD = t is { Kind: TableKind.TwoD };
        var items = new List<string> { "" };
        if (twoD) items.AddRange(t!.ColumnKeys);
        _props.SetCellItems(row, OverridesColumnColumn, items);
        _props.SetCellReadOnly(row, OverridesColumnColumn, !twoD && string.IsNullOrEmpty(p.OverridesColumn));   // a stale column can still be cleared
        _props.SetCellToolTip(row, OverridesColumnColumn, OverridesColumnTip);
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
            case ExpressionColumn:
                var expr = text.Trim().Length == 0 ? null : text.Trim();
                Commit(p => Edit(p, id, x => x with { Expr = expr }));
                var exprProblem = FormulaEdit.SyntaxProblem(expr);
                if (exprProblem != null) _status.Text = "Expression: " + exprProblem;
                _props.SetCellError(row, ExpressionColumn, exprProblem);
                ShowComputed(row, expr != null);
                break;
            case OverridesTableColumn:
            {
                var name = text.Trim();
                var t = name.Length == 0 ? null : pack.Tables.FirstOrDefault(x => x.Name == name);
                if (name.Length > 0 && t == null) return;   // the shown id of a deleted table, picked again: nothing to change
                Commit(p => Edit(p, id, x => x with
                {
                    OverridesTable = t?.Id,
                    // Keep the column only when the new table has it (two dimensions); a one-dimension table has none.
                    OverridesColumn = t is { Kind: TableKind.TwoD } && x.OverridesColumn is { } c
                        && t.ColumnKeys.Any(k => Rules.Tables.TableLookup.Normalise(k) == Rules.Tables.TableLookup.Normalise(c)) ? c : null,
                }));
                Rebuild();   // the Column picker follows the table
                _props.SelectCell(row, col);
                break;
            }
            case OverridesColumnColumn:
                Commit(p => Edit(p, id, x => x with { OverridesColumn = text.Trim().Length == 0 ? null : text.Trim() }));
                if (_s.Rules is { } after && after.Properties.FirstOrDefault(x => x.Id == id) is { } edited) ShowOverrideColumn(row, after, edited);
                break;
            case RuleRefColumn:
            {
                var current = pack.ById<PropertyDef>(id)?.RuleRefId;
                if (RuleRefPicker.IdOf(pack, text, current) is not { } refId) return;
                Commit(p => Edit(p, id, x => x with { RuleRefId = refId.Length == 0 ? null : refId }));
                break;
            }
            case LabelsColumn:
            {
                var name = text.Trim();
                var t = name.Length == 0 ? null : pack.Tables.FirstOrDefault(x => x.Name == name && x.Kind != TableKind.TwoD);
                if (name.Length > 0 && t == null) return;   // the shown id of a deleted table, picked again: nothing to change
                Commit(p => Edit(p, id, x => x with { Labels = t?.Id }));
                break;
            }
            case HoldersColumn:
            {
                var (ids, error) = ParseHolders(pack, text, pack.ById<PropertyDef>(id)?.Holders);
                if (error != null)
                {
                    Rebuild();
                    _status.Text = error;
                    return;
                }
                Commit(p => Edit(p, id, x => x with { Holders = ids }));
                Rebuild();   // the cell shows the names as stored
                break;
            }
            case CloneAsColumn:
                if (!Enum.TryParse<CloneValue>(text, out var cv)) return;
                Commit(p => Edit(p, id, x => x with { CloneAs = cv }));
                break;
            case StartingValueColumn:
            {
                var sv = text.Trim().Length == 0 ? null : text.Trim();
                Commit(p => Edit(p, id, x => x with { DefaultExpr = sv }));
                var svProblem = FormulaEdit.SyntaxProblem(sv);
                if (svProblem != null) _status.Text = "Starting value: " + svProblem;
                _props.SetCellError(row, StartingValueColumn, svProblem);
                break;
            }
            case KeyedByColumn:
            {
                var kb = KeyedFacts.FromDisplay(pack, text);
                Commit(p => Edit(p, id, x => PropertyEdit.WithKeyedBy(x, kb)));
                break;
            }
            case ResetsAtColumn:
            {
                var (rids, rerror) = ParseResetAt(pack, text, pack.ById<PropertyDef>(id)?.ResetAt);
                if (rerror != null)
                {
                    Rebuild();
                    _status.Text = rerror;
                    return;
                }
                Commit(p => Edit(p, id, x => x with { ResetAt = rids }));
                Rebuild();   // the cell shows the names as stored
                break;
            }
        }
    }

    static RulesPack Edit(RulesPack p, string id, Func<PropertyDef, PropertyDef> f) =>
        p with { Properties = p.Properties.Select(x => x.Id == id ? f(x) : x).ToList() };

    void OnHolderCell(int row, int col, string text)
    {
        if (row < 1 || row - 1 >= _holderIds.Count || _s.Rules is not { } pack) return;
        var id = _holderIds[row - 1];
        _status.Text = "";
        if (col == HolderRuleRefColumn)
        {
            if (RuleRefPicker.IdOf(pack, text, pack.ById<HolderDef>(id)?.RuleRefId) is not { } refId) return;
            Commit(p => p with { Holders = p.Holders.Select(h => h.Id != id ? h : h with { RuleRefId = refId.Length == 0 ? null : refId }).ToList() });
            return;
        }
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
