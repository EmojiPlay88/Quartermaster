using Quartermaster.Vassal;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Rules.Tables;
using Quartermaster.Render.Search;
using Quartermaster.Render.Ocr;

namespace Quartermaster.App;

/// <summary>
/// Tables editor of the rules workbench. Column keys are edited through the "Columns…" dialog (one key per line),
/// not in the grid; OneD and Dice tables show one value column headed "Value".
/// </summary>
sealed class TablesTab : UserControl, IRuleRefEditor
{
    static readonly Color Pink = Color.FromArgb(255, 200, 210);

    readonly ModuleSession _s;
    readonly Func<string, RuleRef?> _linkCurrentPage;
    readonly Action<RuleRef>? _showRef;
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly List<string> _ids = new();
    readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    readonly TextBox _notes = new() { Multiline = true, Dock = DockStyle.Top, Height = 44, ScrollBars = ScrollBars.Vertical };
    readonly LinkLabel _refLabel = RuleRefLink.Create();
    readonly Button _link = new() { Text = "Link to current page", AutoSize = true };
    readonly Button _find = RuleRefLink.FindButton();
    readonly DataGridView _grid = new();
    readonly ToolStripButton _addRow = new("Add row"), _delRow = new("Remove row"), _addCol = new("Add column"), _delCol = new("Remove column"), _cols = new("Columns…"), _paste = new("Paste from clipboard…");
    readonly System.Windows.Forms.Timer _notesTimer = new() { Interval = 400 };
    string? _selected;
    bool _rebuilding, _selfEdit, _loadingUi;

    public TablesTab(ModuleSession s, Func<string, RuleRef?> linkCurrentPage, Action<RuleRef>? showRef = null)
    {
        _s = s;
        _linkCurrentPage = linkCurrentPage;
        _showRef = showRef;

        var left = new Panel { Dock = DockStyle.Left, Width = 130 };
        var leftBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, WrapContents = true };
        var bNew = new Button { Text = "New…", Width = 50 };
        var bRename = new Button { Text = "Rename…", Width = 58 };
        var bDel = new Button { Text = "Delete", Width = 50 };
        leftBtns.Controls.AddRange(new Control[] { bNew, bRename, bDel });
        left.Controls.Add(_list);
        left.Controls.Add(leftBtns);

        _kind.Items.AddRange(new object[] { "OneD", "TwoD", "Dice" });
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        head.Controls.Add(new Label { Text = "Kind:", AutoSize = true, Padding = new Padding(0, 5, 2, 0) });
        head.Controls.Add(_kind);
        head.Controls.Add(new Label { Text = "Rule ref:", AutoSize = true, Padding = new Padding(12, 5, 2, 0) });
        head.Controls.Add(_refLabel);
        head.Controls.Add(_link);
        head.Controls.Add(_find);

        var bar = new ToolStrip { LayoutStyle = ToolStripLayoutStyle.Flow, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
        foreach (var b in new[] { _addRow, _delRow, _addCol, _delCol, _cols, _paste }) { b.DisplayStyle = ToolStripItemDisplayStyle.Text; bar.Items.Add(b); }

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _grid.BackgroundColor = SystemColors.AppWorkspace;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ShowCellToolTips = true;
        _grid.RowHeadersWidth = 24;

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_grid);
        right.Controls.Add(bar);
        right.Controls.Add(_notes);
        right.Controls.Add(head);
        Controls.Add(right);
        Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
        Controls.Add(left);

        bNew.Click += (_, _) => Run(NewTable);
        bRename.Click += (_, _) => Run(RenameTable);
        bDel.Click += (_, _) => Run(DeleteTable);
        _list.SelectedIndexChanged += (_, _) => Run(OnListSelection);
        _kind.SelectionChangeCommitted += (_, _) => Run(OnKindChosen);
        _notes.TextChanged += (_, _) => { if (!_loadingUi) { _notesTimer.Stop(); _notesTimer.Start(); } };
        _notes.Leave += (_, _) => Run(FlushNotes);
        _notesTimer.Tick += (_, _) => Run(FlushNotes);
        _link.Click += (_, _) => Run(LinkPage);
        _refLabel.LinkClicked += (_, _) => Run(ShowRef);
        _find.Click += (_, _) => Run(Find);
        _addRow.Click += (_, _) => Run(() => Structural(t => TableEdit.AddRow(t)));
        _delRow.Click += (_, _) => Run(() => { if (_grid.CurrentCell is { } c) Structural(t => TableEdit.RemoveRow(t, c.RowIndex)); });
        _addCol.Click += (_, _) => Run(() => Structural(t => TableEdit.AddColumn(t)));
        _delCol.Click += (_, _) => Run(() => { if (_grid.CurrentCell is { ColumnIndex: > 0 } c) Structural(t => TableEdit.RemoveColumn(t, c.ColumnIndex - 1)); });
        _cols.Click += (_, _) => Run(EditColumns);
        _paste.Click += (_, _) => Run(PasteTable);
        _grid.CellValueChanged += (_, e) => Run(() => OnCellChanged(e.RowIndex, e.ColumnIndex));
        _grid.CellFormatting += OnFormatting;
        _grid.CellToolTipTextNeeded += OnToolTip;
        _grid.DataError += (_, e) => e.ThrowException = false;

        s.RulesChanged += OnRulesChanged;
        HandleCreated += (_, _) => { if (_staleBeforeHandle) { _staleBeforeHandle = false; Run(Reload); } };
        Reload();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _s.RulesChanged -= OnRulesChanged;
            _notesTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    bool _staleBeforeHandle;

    public string? SelectedTableId => _selected;

    /// <summary>Selects a table by id (no-op when unknown).</summary>
    public void SelectTable(string id)
    {
        var i = _ids.IndexOf(id);
        if (i >= 0) _list.SelectedIndex = i;
    }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("tables tab: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Table edit failed: " + ex.Message, "");
        }
    }

    TableDef? Current => _selected == null ? null : _s.Rules?.ById<TableDef>(_selected);

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
        Commit(edit);
        RebuildGrid();
    }

    void OnRulesChanged() => OnUi(() =>
    {
        if (_selfEdit) return;
        Reload();
    });

    void OnUi(Action a)
    {
        if (IsDisposed) return;
        if (!IsHandleCreated) { _staleBeforeHandle = true; return; } // reloaded when the handle appears
        if (InvokeRequired) BeginInvoke(new Action(() => { if (!IsDisposed) a(); }));
        else a();
    }

    // ---------- list ----------

    void Reload()
    {
        var pack = _s.Rules;
        _loadingUi = true;
        try
        {
            _ids.Clear();
            _list.Items.Clear();
            foreach (var t in pack?.Tables ?? new List<TableDef>()) { _ids.Add(t.Id); _list.Items.Add(t.Name); }
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
        _selected = _list.SelectedIndex >= 0 && _list.SelectedIndex < _ids.Count ? _ids[_list.SelectedIndex] : null;
        LoadDetails();
        RebuildGrid();
    }

    void NewTable()
    {
        var name = InputDialog.Ask(this, "New table", "Name of the new table:", "Table " + (_ids.Count + 1));
        if (string.IsNullOrWhiteSpace(name) || _s.Rules == null) return;
        if (NameRules.Problem(_s.Rules.Tables.Select(x => x.Name), name, null) is { } bad) { MessageBox.Show(this, bad, "New table", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var t = TableEdit.New(name.Trim(), TableKind.OneD);
        _s.UpdateRules(p => p with { Tables = p.Tables.Append(t).ToList() }); // RulesChanged reloads the list
        SelectTable(t.Id);
    }

    void RenameTable()
    {
        if (Current is not { } t) return;
        var name = InputDialog.Ask(this, "Rename table", "New name:", t.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (NameRules.Problem(_s.Rules!.Tables.Select(x => x.Name), name, t.Name) is { } bad) { MessageBox.Show(this, bad, "Rename table", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Commit(x => x with { Name = name.Trim() });
        Reload();
    }

    void DeleteTable()
    {
        if (Current is not { } t) return;
        if (MessageBox.Show(this, $"Delete the table \"{t.Name}\"? Formulas that use it will report an error.", "Delete table",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
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
            _kind.Enabled = _notes.Enabled = _link.Enabled = _grid.Enabled = _find.Enabled = on;
            _kind.SelectedIndex = t == null ? -1 : (int)t.Kind;
            _notes.Text = t?.Notes ?? "";
            ShowRefLabel(t);
            var two = t?.Kind == TableKind.TwoD;
            _addCol.Enabled = _delCol.Enabled = _cols.Enabled = two;
            _addRow.Enabled = _delRow.Enabled = _paste.Enabled = on;
        }
        finally { _loadingUi = false; }
    }

    void ShowRefLabel(TableDef? t)
    {
        var r = t?.RuleRefId == null ? null : _s.Rules?.ById<RuleRef>(t.RuleRefId);
        RuleRefLink.Show(_refLabel, r, DescribeRef);
    }

    // ---------- rulebook search ----------

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string>? FindInRulebook
    {
        get => _findInRulebook;
        set { _findInRulebook = value; _find.Visible = value != null; }
    }
    Action<string>? _findInRulebook;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<RuleRef?, RefDisplay>? DescribeRef { get; set; }

    public bool CanUseRuleRef => Current != null && Enabled && _link.Enabled && !_s.IsReplaying;

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
        var sel = _notes.SelectionLength > 0 ? _notes.SelectedText : null;
        var q = SearchPanelModel.FindText(sel, t.Name);
        if (q.Length > 0) _findInRulebook(q);
    }

    void OnKindChosen()
    {
        if (Current is not { } t || _kind.SelectedIndex < 0) return;
        var kind = (TableKind)_kind.SelectedIndex;
        if (kind == t.Kind) return;
        if (TableEdit.ChangeLosesData(t, kind) &&
            MessageBox.Show(this, "Only the first column is kept for this kind of table; the other columns will be removed. Continue?", "Change kind",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            _kind.SelectedIndex = (int)t.Kind;
            return;
        }
        Structural(x => TableEdit.ChangeKind(x, kind));
        LoadDetails();
    }

    void FlushNotes()
    {
        _notesTimer.Stop();
        if (_loadingUi || Current is not { } t) return;
        var text = _notes.Text;
        if ((t.Notes ?? "") == text) return;
        Commit(x => x with { Notes = text.Length == 0 ? null : text });
    }

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
        _rebuilding = true;
        try
        {
            _grid.SuspendLayout();
            _grid.Columns.Clear();
            _grid.Rows.Clear();
            if (Current is not { } t) return;
            var two = t.Kind == TableKind.TwoD;
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Row key", SortMode = DataGridViewColumnSortMode.NotSortable, Width = 90 });
            for (var c = 0; c < t.ColumnKeys.Count; c++)
            {
                var key = t.ColumnKeys[c];
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = two ? (key.Trim().Length == 0 ? "(empty)" : key) : "Value",
                    SortMode = DataGridViewColumnSortMode.NotSortable, Width = 80,
                };
                col.ReadOnly = false;
                _grid.Columns.Add(col);
                if (two && key.Trim().Length == 0) col.HeaderCell.Style.BackColor = Pink;
            }
            for (var r = 0; r < t.RowKeys.Count; r++)
            {
                var vals = new object[t.ColumnKeys.Count + 1];
                vals[0] = t.RowKeys[r];
                for (var c = 0; c < t.ColumnKeys.Count; c++)
                    vals[c + 1] = r < t.Cells.Count && c < t.Cells[r].Count ? t.Cells[r][c] : "";
                _grid.Rows.Add(vals);
            }
        }
        finally
        {
            _grid.ResumeLayout();
            _rebuilding = false;
        }
    }

    void OnCellChanged(int row, int col)
    {
        if (_rebuilding || row < 0 || col < 0) return;
        var text = Convert.ToString(_grid[col, row].Value) ?? "";
        Commit(t => col == 0 ? TableEdit.SetRowKey(t, row, text) : TableEdit.SetCell(t, row, col - 1, text));
    }

    void OnFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex == 0 && e.RowIndex >= 0 && string.IsNullOrWhiteSpace(Convert.ToString(e.Value)))
            e.CellStyle!.BackColor = Pink;
    }

    void OnToolTip(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        string? key = null;
        if (e.ColumnIndex == 0 && e.RowIndex >= 0) key = Convert.ToString(_grid[0, e.RowIndex].Value);
        else if (e.RowIndex == -1 && e.ColumnIndex > 0 && Current is { Kind: TableKind.TwoD } t && e.ColumnIndex - 1 < t.ColumnKeys.Count) key = t.ColumnKeys[e.ColumnIndex - 1];
        if (key != null) e.ToolTipText = KeyHint.Describe(TableLookup.ParseKey(key));
    }

    void EditColumns()
    {
        if (Current is not { Kind: TableKind.TwoD } t) return;
        if (ColumnsDialog.Ask(this, t.ColumnKeys) is { } keys) Structural(x => TableEdit.SetColumnKeys(x, keys));
    }

    // ---------- import from a scan ----------

    /// <summary>Creates or reuses the rule ref for (label, document entry, page); set by the workbench (its RefFor path).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, string, int, RuleRef?>? MakeRuleRef { get; set; }

    /// <summary>
    /// Imports an OCR grid (TSV) through the Paste table dialog into a new table (name prompt) or, when a table is selected and the
    /// user chooses it, into that table. A new table's rule ref is the source document and page (an image has no page); an existing
    /// table keeps its rule ref and only gets one when it has none. Returns the table's id, or null when nothing was imported.
    /// </summary>
    public string? ImportGrid(string tsv, string? ruleRefEntry, int? page)
    {
        string? result = null;
        Run(() => result = DoImport(tsv, ruleRefEntry, page));
        return result;
    }

    string? DoImport(string tsv, string? ruleRefEntry, int? page)
    {
        if (_s.Rules == null) { Tell("Create or open a rules pack first; the table is stored in it."); return null; }
        if (_s.IsGuest) { Tell("The host's rules pack is read-only here."); return null; }
        if (_s.IsReplaying) { Tell(ModuleSession.ReplayingText); return null; } // a received turn replays as sent: no rules edits
        FlushNotes();
        var existing = Current;
        bool intoExisting = false;
        if (existing != null)
        {
            var choice = AskTarget(existing.Name);
            if (choice == null) return null;
            intoExisting = choice.Value;
        }
        string name;
        if (intoExisting) name = existing!.Name;
        else
        {
            var suggestion = ScanSessionModel.SuggestTableName(_s.Rules.Tables.Select(t => t.Name));
            var typed = Program.Smoke ? suggestion : InputDialog.Ask(this, "Import into table", "Name of the new table:", suggestion);
            if (string.IsNullOrWhiteSpace(typed)) return null;
            if (NameRules.Problem(_s.Rules.Tables.Select(x => x.Name), typed, null) is { } bad) { Tell(bad); return null; }
            name = typed.Trim();
        }
        var grid = PasteTableDialog.Ask(this, tsv, ScanSessionModel.TargetDescription(name, newTable: !intoExisting));
        if (grid == null || _s.Rules == null) return null;

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
        _s.UpdateRules(p => p with { Tables = p.Tables.Append(made).ToList() }); // RulesChanged reloads the list
        if (!_ids.Contains(made.Id)) Reload();
        SelectTable(made.Id);
        return made.Id;
    }

    /// <summary>True = the selected table, false = a new table, null = cancelled.</summary>
    bool? AskTarget(string selected)
    {
        if (Program.Smoke) { Program.AppLog($"smoke: import target: a new table (the selected \"{selected}\" is kept)"); return false; }
        var into = new TaskDialogButton($"Replace \"{selected}\"");
        var fresh = new TaskDialogButton("New table…");
        var page = new TaskDialogPage
        {
            Caption = "Import into table",
            Heading = "Where should the scanned rows go?",
            Text = $"Into a new table, or into the selected table \"{selected}\" (its rows, columns and cells are replaced).",
            Buttons = { fresh, into, TaskDialogButton.Cancel },
            DefaultButton = fresh,
        };
        var r = TaskDialog.ShowDialog(this, page);
        return r == into ? true : r == fresh ? false : null;
    }

    void Tell(string text)
    {
        if (Program.Smoke) { Program.AppLog("tables tab: " + text); return; }
        MessageBox.Show(this, text, "Import into table", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void PasteTable()
    {
        if (Current == null) return;
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (PasteTableDialog.Ask(this, text) is { } grid) Structural(x => TableEdit.ApplyPaste(x, grid));
    }
}
