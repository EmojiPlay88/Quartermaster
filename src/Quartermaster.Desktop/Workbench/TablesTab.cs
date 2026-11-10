using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Quartermaster.Vassal;
using Quartermaster.Render;
using Quartermaster.Render.Ocr;
using Quartermaster.Render.Search;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.Rules.Tables;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Tables editor of the rules workbench (port of WinForms TablesTab). Column keys are edited through the "Columns…" dialog (one key per line),
/// not in the grid; OneD and Dice tables show one value column headed "Value".
/// </summary>
public sealed class TablesTab : UserControl, IRuleRefEditor
{
    const string ImportTitle = "Import into table";

    readonly ModuleSession _s;
    readonly IDialogs _dialogs;
    readonly Func<string, RuleRef?> _linkCurrentPage;
    readonly Action<RuleRef>? _showRef;
    readonly ListBox _list = new();
    readonly List<string> _ids = new();
    readonly ComboBox _kind = new() { Width = 90, ItemsSource = new[] { "OneD", "TwoD", "Dice" }, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 44, Margin = new Thickness(0, 2) };
    readonly RuleRefRow _refRow = new();
    readonly EditGrid _grid = new();
    readonly Button _addRow = new() { Content = "Add row" }, _delRow = new() { Content = "Remove row" }, _addCol = new() { Content = "Add column" },
        _delCol = new() { Content = "Remove column" }, _cols = new() { Content = "Columns…" }, _paste = new() { Content = "Paste from clipboard…" };
    readonly Button _bNew = new() { Content = "New…", Width = 50 }, _bRename = new() { Content = "Rename…", Width = 62 }, _bDel = new() { Content = "Delete", Width = 50 };
    readonly DispatcherTimer _notesTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    string? _selected;
    bool _selfEdit, _loadingUi;

    public TablesTab(ModuleSession s, IDialogs dialogs, Func<string, RuleRef?> linkCurrentPage, Action<RuleRef>? showRef = null)
    {
        _s = s;
        _dialogs = dialogs;
        _linkCurrentPage = linkCurrentPage;
        _showRef = showRef;

        var leftBtns = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var b in new[] { _bNew, _bRename, _bDel }) { b.Margin = new Thickness(0, 0, 2, 2); leftBtns.Children.Add(b); }
        var left = new DockPanel { Width = 130 };
        DockPanel.SetDock(leftBtns, Dock.Bottom);
        left.Children.Add(leftBtns);
        left.Children.Add(_list);

        var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(new TextBlock { Text = "Kind:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        head.Children.Add(_kind);
        _refRow.Margin = new Thickness(12, 0, 0, 0);
        head.Children.Add(_refRow);

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var b in new[] { _addRow, _delRow, _addCol, _delCol, _cols, _paste }) { b.Margin = new Thickness(0, 0, 4, 0); bar.Children.Add(b); }

        var right = new DockPanel { Margin = new Thickness(4, 0, 0, 0) };
        foreach (var (c, d) in new (Control, Dock)[] { (head, Dock.Top), (_notes, Dock.Top), (bar, Dock.Top) })
        {
            DockPanel.SetDock(c, d);
            right.Children.Add(c);
        }
        right.Children.Add(_grid);

        Content = new DockPanel { Margin = new Thickness(4) };
        var dock = (DockPanel)Content;
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);
        dock.Children.Add(right);

        _bNew.Click += (_, _) => Run(NewTableAsync);
        _bRename.Click += (_, _) => Run(RenameTableAsync);
        _bDel.Click += (_, _) => Run(DeleteTableAsync);
        _list.SelectionChanged += (_, _) => Run(OnListSelection);
        _kind.SelectionChanged += (_, _) => Run(OnKindChosenAsync);
        _notes.TextChanged += (_, _) => { if (!_loadingUi) { _notesTimer.Stop(); _notesTimer.Start(); } };
        _notes.LostFocus += (_, _) => Run(FlushNotes);
        _notesTimer.Tick += (_, _) => Run(FlushNotes);
        _refRow.LinkButton.Click += (_, _) => Run(LinkPage);
        _refRow.LinkClicked += () => Run(ShowRef);
        _refRow.FindButton.Click += (_, _) => Run(Find);
        _addRow.Click += (_, _) => Run(() => Structural(t => TableEdit.AddRow(t)));
        _delRow.Click += (_, _) => Run(() => { if (_grid.CurrentCell is { } c) Structural(t => TableEdit.RemoveRow(t, c.Row)); });
        _addCol.Click += (_, _) => Run(() => Structural(t => TableEdit.AddColumn(t)));
        _delCol.Click += (_, _) => Run(() => { if (_grid.CurrentCell is { Col: > 0 } c) Structural(t => TableEdit.RemoveColumn(t, c.Col - 1)); });
        _cols.Click += (_, _) => Run(EditColumnsAsync);
        _paste.Click += (_, _) => Run(PasteTableAsync);
        _grid.CellCommitted += (r, c, text) => Run(() => OnCellCommitted(r, c, text));

        AttachedToVisualTree += (_, _) => { _s.RulesChanged += OnRulesChanged; Reload(); };
        DetachedFromVisualTree += (_, _) =>
        {
            Run(() => { FlushNotes(); _grid.CommitEdit(); });   // a hidden tab is detached: what was typed must reach the pack first
            _s.RulesChanged -= OnRulesChanged;
            _notesTimer.Stop();
        };
        Reload();
    }

    public string? SelectedTableId => _selected;

    /// <summary>Selects a table by id (no-op when unknown).</summary>
    public void SelectTable(string id)
    {
        var i = _ids.IndexOf(id);
        if (i >= 0) _list.SelectedIndex = i;
    }

    /// <summary>Commits what is being typed (the notes, an open cell edit) into the pack; call before saving.</summary>
    public void CommitPending()
    {
        Run(() => { FlushNotes(); _grid.CommitEdit(); });
    }

    /// <summary>The dialogs' owner while the tab is not in a window (a hidden workbench tab is detached): set by the workbench, so a
    /// scan import works whichever tab is shown.</summary>
    public Window? DialogOwner { get; set; }

    Window? Owner => TopLevel.GetTopLevel(this) as Window ?? DialogOwner;

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

    /// <summary>Logs the failure and redraws from the pack, so a refused edit (guest, replay) never leaves text the pack does not hold.</summary>
    void Failed(Exception ex)
    {
        _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Table edit failed: " + ex.Message, "");
        try { Reload(); }
        catch (Exception ex2) when (ex2 is not OutOfMemoryException) { _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Table tab reload failed: " + ex2.Message, ""); }
    }

    TableDef? Current => _selected == null ? null : _s.Rules?.ById<TableDef>(_selected);

    /// <summary>The table <paramref name="id"/> when it is still the selected one and still in the pack. Async handlers capture the id
    /// before awaiting a dialog and re-check it afterwards, so a change made meanwhile never lands on another table.</summary>
    TableDef? StillCurrent(string id) => _selected == id ? Current : null;

    // ---------- model updates ----------

    /// <summary>Applies an edit to the selected table through the session. Does not rebuild the grid.</summary>
    void Commit(Func<TableDef, TableDef> edit)
    {
        if (_selected is not { } id) return;
        _selfEdit = true;
        try
        {
            _s.UpdateRules(p => p with { Tables = p.Tables.Select(t => t.Id == id ? edit(t) : t).ToList() });
        }
        finally { _selfEdit = false; }
    }

    void Structural(Func<TableDef, TableDef> edit)
    {
        FlushNotes();
        _grid.CommitEdit();
        Commit(edit);
        RebuildGrid();
    }

    void OnRulesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) OnRulesChangedUi();
        else Dispatcher.UIThread.Post(OnRulesChangedUi);
    }

    // An external change reloads everything: unflushed notes and a cell being edited are dropped as stale (as in WinForms).
    void OnRulesChangedUi()
    {
        if (_selfEdit) return;
        Run(Reload);
    }

    // ---------- list ----------

    void Reload()
    {
        var pack = _s.Rules;
        _loadingUi = true;
        try
        {
            _ids.Clear();
            var names = new List<string>();
            foreach (var t in pack?.Tables ?? new List<TableDef>()) { _ids.Add(t.Id); names.Add(t.Name); }
            _list.ItemsSource = names;
            var idx = _selected == null ? -1 : _ids.IndexOf(_selected);
            if (idx < 0 && _ids.Count > 0) idx = 0;
            _list.SelectedIndex = idx;
            _selected = idx >= 0 ? _ids[idx] : null;
        }
        finally { _loadingUi = false; }
        LoadDetails();
        RebuildGrid();
    }

    void OnListSelection()
    {
        if (_loadingUi) return;
        FlushNotes();
        _grid.CommitEdit();
        _selected = _list.SelectedIndex >= 0 && _list.SelectedIndex < _ids.Count ? _ids[_list.SelectedIndex] : null;
        LoadDetails();
        RebuildGrid();
    }

    async Task NewTableAsync()
    {
        if (Owner is not { } w) return;
        var name = await _dialogs.AskTextAsync(w, "New table", "Name of the new table:", WorkbenchRules.UniqueName(_s.Rules?.Tables.Select(x => x.Name) ?? [], "Table"));
        if (string.IsNullOrWhiteSpace(name) || _s.Rules == null) return;
        if (NameRules.Problem(_s.Rules.Tables.Select(x => x.Name), name, null) is { } bad) { await _dialogs.ShowWarningAsync(w, bad, "New table"); return; }
        var t = TableEdit.New(name.Trim(), TableKind.OneD);
        _s.UpdateRules(p => p with { Tables = p.Tables.Append(t).ToList() });   // RulesChanged reloads the list
        SelectTable(t.Id);
    }

    async Task RenameTableAsync()
    {
        if (Current is not { } t || Owner is not { } w) return;
        var id = t.Id;
        var name = await _dialogs.AskTextAsync(w, "Rename table", "New name:", t.Name);
        if (string.IsNullOrWhiteSpace(name) || StillCurrent(id) is not { } t2) return;
        t = t2;
        if (NameRules.Problem(_s.Rules!.Tables.Select(x => x.Name), name, t.Name) is { } bad) { await _dialogs.ShowWarningAsync(w, bad, "Rename table"); return; }
        Commit(x => x with { Name = name.Trim() });
        Reload();
    }

    async Task DeleteTableAsync()
    {
        if (Current is not { } t || Owner is not { } w) return;
        var id = t.Id;
        if (!await _dialogs.AskYesNoAsync(w, $"Delete the table \"{t.Name}\"? Formulas that use it will report an error.", "Delete table", defaultNo: true)) return;
        if (StillCurrent(id) == null) return;
        _s.UpdateRules(p => p with { Tables = p.Tables.Where(x => x.Id != t.Id).ToList() });
    }

    // ---------- details ----------

    void LoadDetails()
    {
        _loadingUi = true;
        try
        {
            var t = Current;
            var on = t != null;
            _kind.IsEnabled = _notes.IsEnabled = _refRow.LinkButton.IsEnabled = _grid.IsEnabled = _refRow.FindButton.IsEnabled = on;
            _kind.SelectedIndex = t == null ? -1 : (int)t.Kind;
            _notes.Text = t?.Notes ?? "";
            ShowRefLabel(t);
            var two = t?.Kind == TableKind.TwoD;
            _addCol.IsEnabled = _delCol.IsEnabled = _cols.IsEnabled = two;
            _addRow.IsEnabled = _delRow.IsEnabled = _paste.IsEnabled = on;
        }
        finally { _loadingUi = false; }
    }

    void ShowRefLabel(TableDef? t)
    {
        var r = t?.RuleRefId == null ? null : _s.Rules?.ById<RuleRef>(t.RuleRefId);
        _refRow.Show(r, DescribeRef);
    }

    // ---------- rulebook search ----------

    public Action<string>? FindInRulebook
    {
        get => _findInRulebook;
        set { _findInRulebook = value; _refRow.FindButton.IsVisible = value != null; }
    }
    Action<string>? _findInRulebook;

    public Func<RuleRef?, RefDisplay>? DescribeRef { get; set; }

    public bool CanUseRuleRef => Current != null && IsEnabled && _refRow.LinkButton.IsEnabled && !_s.IsReplaying;

    public bool UseRuleRef(Func<string, RuleRef?> make)
    {
        if (!CanUseRuleRef || Current is not { } t) return false;
        FlushNotes();
        var r = make(t.Name);
        if (r == null) return false;
        Commit(x => x with { RuleRefId = r.Id });
        ShowRefLabel(Current);
        return true;
    }

    public void RefreshRuleRef() => ShowRefLabel(Current);

    /// <summary>"Find in rulebook": the text selected in the notes, else the table's name.</summary>
    void Find()
    {
        if (Current is not { } t || _findInRulebook == null) return;
        var sel = _notes.SelectionEnd != _notes.SelectionStart ? _notes.SelectedText : null;
        var q = SearchPanelModel.FindText(sel, t.Name);
        if (q.Length > 0) _findInRulebook(q);
    }

    async Task OnKindChosenAsync()
    {
        if (_loadingUi || Current is not { } t || _kind.SelectedIndex < 0 || Owner is not { } w) return;
        var kind = (TableKind)_kind.SelectedIndex;
        if (kind == t.Kind) return;
        var id = t.Id;
        if (TableEdit.ChangeLosesData(t, kind) &&
            !await _dialogs.AskYesNoAsync(w, "Only the first column is kept for this kind of table; the other columns will be removed. Continue?", "Change kind", defaultNo: true))
        {
            if (StillCurrent(id) is { } cur)
            {
                _loadingUi = true;
                try { _kind.SelectedIndex = (int)cur.Kind; }
                finally { _loadingUi = false; }
            }
            return;
        }
        if (StillCurrent(id) == null) return;
        Structural(x => TableEdit.ChangeKind(x, kind));
        LoadDetails();
    }

    void FlushNotes()
    {
        _notesTimer.Stop();
        if (_loadingUi || Current is not { } t) return;
        var text = _notes.Text ?? "";
        if (Norm(t.Notes ?? "") == Norm(text)) return;   // the box may hand back LF for a stored CRLF: that is no edit
        Commit(x => x with { Notes = text.Length == 0 ? null : text });
    }

    static string Norm(string s) => s.Replace("\r\n", "\n");

    void LinkPage()
    {
        if (Current is not { } t) return;
        var r = _linkCurrentPage(t.Name);
        if (r == null) return;
        Commit(x => x with { RuleRefId = r.Id });
        ShowRefLabel(Current);
    }

    void ShowRef()
    {
        if (Current?.RuleRefId is { } id && _s.Rules?.ById<RuleRef>(id) is { } r) _showRef?.Invoke(r);
    }

    // ---------- grid ----------

    void RebuildGrid()
    {
        if (Current is not { } t)
        {
            _grid.SetColumns([]);
            return;
        }
        var two = t.Kind == TableKind.TwoD;
        var cols = new List<EditColumn> { new("Row key", Width: 90) };
        for (var c = 0; c < t.ColumnKeys.Count; c++)
        {
            var key = t.ColumnKeys[c];
            cols.Add(new EditColumn(two ? (key.Trim().Length == 0 ? "(empty)" : key) : "Value", Width: 80));
        }
        _grid.SetColumns(cols);
        var rows = new List<IReadOnlyList<string>>();
        for (var r = 0; r < t.RowKeys.Count; r++)
        {
            var vals = new string[t.ColumnKeys.Count + 1];
            vals[0] = t.RowKeys[r];
            for (var c = 0; c < t.ColumnKeys.Count; c++)
                vals[c + 1] = r < t.Cells.Count && c < t.Cells[r].Count ? t.Cells[r][c] : "";
            rows.Add(vals);
        }
        _grid.SetRows(rows);   // resets per-cell state: tints and hints go on afterwards
        for (var r = 0; r < rows.Count; r++) DecorateRowKey(r);
        if (two)
            for (var c = 0; c < t.ColumnKeys.Count; c++)
            {
                var key = t.ColumnKeys[c];
                _grid.SetHeaderToolTip(c + 1, KeyHint.Describe(TableLookup.ParseKey(key)));
                if (key.Trim().Length == 0) _grid.SetHeaderTint(c + 1, EditGrid.ErrorBrush);
            }
    }

    /// <summary>An empty row key is pink; every row key carries its plain-language reading as a tooltip.</summary>
    void DecorateRowKey(int row)
    {
        var key = _grid.Cell(row, 0);
        _grid.SetCellTint(row, 0, string.IsNullOrWhiteSpace(key) ? EditGrid.ErrorBrush : null);
        _grid.SetCellToolTip(row, 0, KeyHint.Describe(TableLookup.ParseKey(key)));
    }

    void OnCellCommitted(int row, int col, string text)
    {
        Commit(t => col == 0 ? TableEdit.SetRowKey(t, row, text) : TableEdit.SetCell(t, row, col - 1, text));
        if (col == 0) DecorateRowKey(row);
    }

    async Task EditColumnsAsync()
    {
        if (Current is not { Kind: TableKind.TwoD } t || Owner is not { } w) return;
        _grid.CommitEdit();
        var id = t.Id;
        if (await _dialogs.AskColumnsAsync(w, t.ColumnKeys) is { } keys && StillCurrent(id) != null) Structural(x => TableEdit.SetColumnKeys(x, keys));
    }

    async Task PasteTableAsync()
    {
        if (Current is not { } t || Owner is not { } w) return;
        _grid.CommitEdit();
        var id = t.Id;
        var text = await _dialogs.GetClipboardTextAsync(w) ?? "";
        if (await _dialogs.PasteTableAsync(w, text) is { } grid && StillCurrent(id) != null) Structural(x => TableEdit.ApplyPaste(x, grid));
    }

    // ---------- import from a scan ----------

    /// <summary>Creates or reuses the rule ref for (label, document entry, page); set by the workbench (its RefFor path).</summary>
    public Func<string, string, int, RuleRef?>? MakeRuleRef { get; set; }

    /// <summary>
    /// Imports an OCR grid (TSV) through the Paste table dialog into a new table (name prompt) or, when a table is selected and the
    /// user chooses it, into that table. A new table's rule ref is the source document and page (an image has no page); an existing
    /// table keeps its rule ref and only gets one when it has none. Returns the table's id, or null when nothing was imported.
    /// </summary>
    public async Task<string?> ImportGridAsync(string tsv, string? ruleRefEntry, int? page)
    {
        try { return await DoImport(tsv, ruleRefEntry, page); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Failed(ex); return null; }
    }

    async Task<string?> DoImport(string tsv, string? ruleRefEntry, int? page)
    {
        if (Owner is not { } w) return null;
        if (_s.Rules == null) { await Tell(w, "Create or open a rules pack first; the table is stored in it."); return null; }
        if (_s.IsGuest) { await Tell(w, "The host's rules pack is read-only here."); return null; }
        if (_s.IsReplaying) { await Tell(w, ModuleSession.ReplayingText); return null; }   // a received turn replays as sent: no rules edits
        FlushNotes();
        _grid.CommitEdit();
        var existing = Current;
        var intoExisting = false;
        if (existing != null)
        {
            var choice = await _dialogs.AskImportTargetAsync(w, existing.Name);
            if (choice == null) return null;
            intoExisting = choice.Value;
            if (intoExisting && StillCurrent(existing.Id) == null) return null;
        }
        string name;
        if (intoExisting) name = existing!.Name;
        else
        {
            var suggestion = ScanSessionModel.SuggestTableName(_s.Rules.Tables.Select(t => t.Name));
            var typed = await _dialogs.AskTextAsync(w, ImportTitle, "Name of the new table:", suggestion);
            if (string.IsNullOrWhiteSpace(typed)) return null;
            if (NameRules.Problem(_s.Rules.Tables.Select(x => x.Name), typed, null) is { } bad) { await Tell(w, bad); return null; }
            name = typed.Trim();
        }
        var grid = await _dialogs.PasteTableAsync(w, tsv, ImportTitle, ScanSessionModel.TargetDescription(name, newTable: !intoExisting), tsvOnly: true);
        if (grid == null || _s.Rules == null) return null;
        if (intoExisting && StillCurrent(existing!.Id) == null) return null;

        RuleRef? MakeRef(string label) =>
            ruleRefEntry == null || MakeRuleRef == null ? null : MakeRuleRef(label, ruleRefEntry, page ?? 0);

        if (intoExisting)
        {
            Structural(x => TableEdit.ApplyPaste(x, grid));
            if (Current is { RuleRefId: null } t && MakeRef(t.Name) is { } r) { Commit(x => x with { RuleRefId = r.Id }); ShowRefLabel(Current); }
            return existing!.Id;
        }
        var made = TableEdit.ApplyPaste(TableEdit.New(name, ScanSessionModel.KindFor(grid)), grid);
        if (MakeRef(name) is { } rr) made = made with { RuleRefId = rr.Id };
        _s.UpdateRules(p => p with { Tables = p.Tables.Append(made).ToList() });   // RulesChanged reloads the list
        if (!_ids.Contains(made.Id)) Reload();
        SelectTable(made.Id);
        return made.Id;
    }

    Task Tell(Window w, string text) => _dialogs.ShowInfoAsync(w, text, ImportTitle);

    // test hooks
    internal EditGrid Grid => _grid;
    internal TextBox Notes => _notes;
    internal ComboBox KindBox => _kind;
    internal ListBox List => _list;
    internal RuleRefRow RefRow => _refRow;
    internal Button NewButton => _bNew;
    internal Button RenameButton => _bRename;
    internal Button DeleteButton => _bDel;
    internal Button AddRowButton => _addRow;
    internal Button RemoveRowButton => _delRow;
    internal Button AddColumnButton => _addCol;
    internal Button RemoveColumnButton => _delCol;
    internal Button ColumnsButton => _cols;
    internal Button PasteButton => _paste;
}
