using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// Phases editor of the rules workbench: the turn's phases in order, the sides they belong to, repeating blocks, and per-phase fields.
/// Everything is typed by the user. Edits go through UpdateRules; text fields commit 400 ms after the last keystroke, grids commit at once.
/// </summary>
sealed class PhasesTab : UserControl
{
    const string None = "(none)";
    const string MissingText = "(missing)";

    readonly ModuleSession _s;
    readonly Func<string, RuleRef?> _linkCurrentPage;
    readonly Action<RuleRef>? _showRef;

    readonly ListBox _sides = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox _sideFilter = new() { Dock = DockStyle.Fill };
    readonly Label _sideFilterErr = new() { AutoSize = true, ForeColor = Color.Firebrick };
    readonly ListView _phases = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly Panel _right = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly TextBox _name = new() { Dock = DockStyle.Fill };
    readonly ComboBox _side = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly TextBox _instr = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, WordWrap = true };
    readonly TextBox _notes = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, WordWrap = true };
    readonly Label _refLabel = new() { AutoSize = true, Padding = new Padding(0, 5, 8, 0) };
    readonly Button _link = new() { Text = "Link to current page", AutoSize = true };
    readonly Button _showRefBtn = new() { Text = "Show", AutoSize = true };
    readonly ListBox _check = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly DataGridView _start = FormulasTab.NewGrid();
    readonly DataGridView _move = FormulasTab.NewGrid();
    readonly DataGridView _warn = FormulasTab.NewGrid();
    readonly TextBox _issues = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ForeColor = Color.Firebrick, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    readonly System.Windows.Forms.Timer _cellTimer = new() { Interval = 400 };
    readonly List<string> _sideIds = new();
    readonly List<string> _phaseSideIds = new();
    string? _selectedPhase, _selectedSide;
    bool _selfEdit, _loading, _dirty, _rebuilding;

    public PhasesTab(ModuleSession s, Func<string, RuleRef?> linkCurrentPage, Action<RuleRef>? showRef = null)
    {
        _s = s;
        _linkCurrentPage = linkCurrentPage;
        _showRef = showRef;
        _showRefBtn.Visible = showRef != null;

        // ----- left: sides above phases -----
        var bSideAdd = new Button { Text = "Add…", Width = 52 };
        var bSideRename = new Button { Text = "Rename…", Width = 62 };
        var bSideDel = new Button { Text = "Remove", Width = 56 };
        var sideBtns = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, WrapContents = false };
        sideBtns.Controls.AddRange(new Control[] { bSideAdd, bSideRename, bSideDel });
        var filterRow = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 40, ColumnCount = 2, RowCount = 2 };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.Controls.Add(new Label { Text = "Units of this side:", AutoSize = true, Padding = new Padding(0, 5, 2, 0) }, 0, 0);
        filterRow.Controls.Add(_sideFilter, 1, 0);
        filterRow.Controls.Add(_sideFilterErr, 1, 1);
        var sidesBox = new GroupBox { Text = "Sides", Dock = DockStyle.Bottom, Height = 112, Padding = new Padding(4) };
        sidesBox.Controls.Add(_sides);
        sidesBox.Controls.Add(filterRow);
        sidesBox.Controls.Add(sideBtns);

        _phases.Columns.Add("Phase", 175);
        _phases.Columns.Add("Side", 55);
        var phaseBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 86, WrapContents = true };
        var bAdd = new Button { Text = "Add", Width = 50 };
        var bDup = new Button { Text = "Duplicate", Width = 62 };
        var bDel = new Button { Text = "Remove", Width = 56 };
        var bUp = new Button { Text = "Move Up", Width = 62 };
        var bDown = new Button { Text = "Move Down", Width = 74 };
        var bBlock = new Button { Text = "Make block from selection…", AutoSize = true };
        var bUnblock = new Button { Text = "Remove block", AutoSize = true };
        phaseBtns.Controls.AddRange(new Control[] { bAdd, bDup, bDel, bUp, bDown, bBlock, bUnblock });
        var phasesBox = new GroupBox { Text = "Phases, in order", Dock = DockStyle.Fill, Padding = new Padding(4) };
        phasesBox.Controls.Add(_phases);
        phasesBox.Controls.Add(phaseBtns);

        var left = new Panel { Dock = DockStyle.Left, Width = 270 };
        left.Controls.Add(phasesBox);
        left.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
        left.Controls.Add(sidesBox);

        // ----- right: fields of the selected phase -----
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(4) };
        rows.MinimumSize = new Size(440, 0);
        _right.Controls.Add(rows);
        void AddRow(Control c, int height)
        {
            c.Dock = DockStyle.Top;
            c.Height = height;
            c.Margin = new Padding(0, 0, 0, 4);
            rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rows.Controls.Add(c, 0, rows.RowCount++);
        }
        rows.RowCount = 0;
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var nameRow = new TableLayoutPanel { ColumnCount = 4, RowCount = 1 };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nameRow.Controls.Add(new Label { Text = "Name:", AutoSize = true, Padding = new Padding(0, 5, 2, 0) }, 0, 0);
        nameRow.Controls.Add(_name, 1, 0);
        nameRow.Controls.Add(new Label { Text = "Side:", AutoSize = true, Padding = new Padding(8, 5, 2, 0) }, 2, 0);
        nameRow.Controls.Add(_side, 3, 0);
        AddRow(nameRow, 28);

        var instrBox = new GroupBox { Text = "Instructions (shown while this phase is current)", Padding = new Padding(4) };
        instrBox.Controls.Add(_instr);
        AddRow(instrBox, 110);

        var notesBox = new GroupBox { Text = "Notes (shown as a tooltip on the phase)", Padding = new Padding(4) };
        notesBox.Controls.Add(_notes);
        AddRow(notesBox, 60);

        var refRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        refRow.Controls.Add(new Label { Text = "Rule ref:", AutoSize = true, Padding = new Padding(0, 5, 2, 0) });
        refRow.Controls.AddRange(new Control[] { _refLabel, _link, _showRefBtn });
        AddRow(refRow, 30);

        var bCheckAdd = new Button { Text = "Add…", Width = 52 };
        var bCheckDel = new Button { Text = "Remove", Width = 56 };
        var checkBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 28, WrapContents = false };
        checkBtns.Controls.AddRange(new Control[] { bCheckAdd, bCheckDel });
        var checkBox = new GroupBox { Text = "Checklist (double-click to edit)", Padding = new Padding(4) };
        checkBox.Controls.Add(_check);
        checkBox.Controls.Add(checkBtns);
        AddRow(checkBox, 120);

        _start.Columns.AddRange(FormulasTab.Col("Formula", weight: 90), FormulasTab.Col("For each", weight: 40), FormulasTab.Col("Filter (units)", weight: 90));
        AddRow(GridBox("On start: run formulas", _start, out var startAdd, out var startDel), 130);
        _move.Columns.AddRange(FormulasTab.Col("Formula", weight: 90), FormulasTab.Col("Filter (units)", weight: 90));
        AddRow(GridBox("On move: run formulas for moved units", _move, out var moveAdd, out var moveDel), 120);
        _warn.Columns.AddRange(FormulasTab.Col("Condition", weight: 90), FormulasTab.Col("Message ({unit} = unit name)", weight: 90),
            new DataGridViewCheckBoxColumn { HeaderText = "Decision", FillWeight = 30, MinimumWidth = 50 });
        AddRow(GridBox("Warnings", _warn, out var warnAdd, out var warnDel), 130);

        var issuesBox = new GroupBox { Text = "Problems with this phase", Padding = new Padding(4) };
        issuesBox.Controls.Add(_issues);
        AddRow(issuesBox, 64);

        Controls.Add(_right);
        Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
        Controls.Add(left);

        // ----- events -----
        bSideAdd.Click += (_, _) => Run(AddSide);
        bSideRename.Click += (_, _) => Run(RenameSide);
        bSideDel.Click += (_, _) => Run(RemoveSide);
        _sides.SelectedIndexChanged += (_, _) => Run(OnSideSelected);
        _sideFilter.TextChanged += (_, _) => { if (_loading) return; _sideFilterErr.Text = FormulaEdit.SyntaxProblem(_sideFilter.Text) ?? ""; Kick(); };
        _sideFilter.Leave += (_, _) => Run(() => Flush());

        bAdd.Click += (_, _) => Run(AddPhase);
        bDup.Click += (_, _) => Run(() => Structural(p => PhaseEdit.Duplicate(p, SelectedIndex), nextIndex: SelectedIndex + 1));
        bDel.Click += (_, _) => Run(RemovePhase);
        bUp.Click += (_, _) => Run(() => Structural(p => PhaseEdit.Move(p, SelectedIndex, -1), keepSelected: true));
        bDown.Click += (_, _) => Run(() => Structural(p => PhaseEdit.Move(p, SelectedIndex, 1), keepSelected: true));
        bBlock.Click += (_, _) => Run(MakeBlock);
        bUnblock.Click += (_, _) => Run(RemoveBlock);
        _phases.SelectedIndexChanged += (_, _) => Run(OnPhaseSelected);

        _name.TextChanged += (_, _) => { if (!_loading) Kick(); };
        _instr.TextChanged += (_, _) => { if (!_loading) Kick(); };
        _notes.TextChanged += (_, _) => { if (!_loading) Kick(); };
        _notes.Leave += (_, _) => Run(() => Flush());
        _name.Leave += (_, _) => Run(() => Flush());
        _instr.Leave += (_, _) => Run(() => Flush());
        _timer.Tick += (_, _) => Run(() => Flush());
        _cellTimer.Tick += (_, _) => Run(CommitDirtyCells);
        _side.SelectionChangeCommitted += (_, _) => Run(OnSideChosen);
        _link.Click += (_, _) => Run(LinkPage);
        _showRefBtn.Click += (_, _) => Run(ShowRef);

        bCheckAdd.Click += (_, _) => Run(AddCheck);
        bCheckDel.Click += (_, _) => Run(RemoveCheck);
        _check.DoubleClick += (_, _) => Run(EditCheck);

        foreach (var g in new[] { _start, _move, _warn })
        {
            // Combo and checkbox cells commit at once; text cells commit when the edit ends or ~400 ms after the last keystroke (P3-R4).
            g.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewComboBoxCell or DataGridViewCheckBoxCell) g.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            g.EditingControlShowing += (_, e) =>
            {
                if (e.Control is not TextBox tb) return;
                tb.TextChanged -= OnCellTextChanged;
                tb.TextChanged += OnCellTextChanged;
            };
            g.DataError += (_, e) => e.ThrowException = false;
        }
        _start.CellValueChanged += (_, e) => Run(() => OnStartCell(e.RowIndex, e.ColumnIndex));
        _move.CellValueChanged += (_, e) => Run(() => OnMoveCell(e.RowIndex, e.ColumnIndex));
        _warn.CellValueChanged += (_, e) => Run(() => OnWarnCell(e.RowIndex, e.ColumnIndex));
        startAdd.Click += (_, _) => Run(() => CommitPhase(p => p with { OnStart = p.OnStart.Append(new StartEntry { FormulaId = FirstFormulaId() }).ToList() }, rebuild: true));
        startDel.Click += (_, _) => Run(() => { if (_start.CurrentCell is { } c) CommitPhase(p => p with { OnStart = Without(p.OnStart, c.RowIndex) }, rebuild: true); });
        moveAdd.Click += (_, _) => Run(() => CommitPhase(p => p with { OnMove = p.OnMove.Append(new MoveEntry { FormulaId = FirstFormulaId() }).ToList() }, rebuild: true));
        moveDel.Click += (_, _) => Run(() => { if (_move.CurrentCell is { } c) CommitPhase(p => p with { OnMove = Without(p.OnMove, c.RowIndex) }, rebuild: true); });
        warnAdd.Click += (_, _) => Run(() => CommitPhase(p => p with { Warnings = p.Warnings.Append(new PhaseWarning()).ToList() }, rebuild: true));
        warnDel.Click += (_, _) => Run(() => { if (_warn.CurrentCell is { } c) CommitPhase(p => p with { Warnings = Without(p.Warnings, c.RowIndex) }, rebuild: true); });

        VisibleChanged += (_, _) =>
        {
            if (!Visible) Run(() => Flush());
            else if (_dirty) Run(Reload);
        };
        s.RulesChanged += OnRulesChanged;
        Reload();
    }

    static GroupBox GridBox(string title, DataGridView grid, out Button add, out Button del)
    {
        add = new Button { Text = "Add", Width = 50 };
        del = new Button { Text = "Remove", Width = 56 };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 28, WrapContents = false };
        bar.Controls.AddRange(new Control[] { add, del });
        var g = new GroupBox { Text = title, Padding = new Padding(4) };
        g.Controls.Add(grid);
        g.Controls.Add(bar);
        return g;
    }

    static List<T> Without<T>(List<T> list, int index) => list.Where((_, i) => i != index).ToList();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _s.RulesChanged -= OnRulesChanged;
            if (_timer.Enabled || _cellTimer.Enabled) { try { Flush(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("phases tab flush on close: " + ex.Message); } }
            _timer.Dispose();
            _cellTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- plumbing ----------

    public string? SelectedPhaseId => _selectedPhase;

    public int PhaseCountForSmoke => _phases.Items.Count;

    public void SelectPhase(string id)
    {
        foreach (ListViewItem it in _phases.Items)
            if ((string?)it.Tag == id) { _phases.SelectedItems.Clear(); it.Selected = true; it.EnsureVisible(); return; }
    }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("phases tab: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Phase edit failed: " + ex.Message, "");
        }
    }

    void Warn(string text)
    {
        if (Program.Smoke) { Program.AppLog("phases: " + text); return; }
        MessageBox.Show(this, text, "Phases", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    void Kick() { _timer.Stop(); _timer.Start(); }

    /// <summary>A keystroke in a grid's text editor: the cell is committed (and so validated) only after the typing pauses.</summary>
    void OnCellTextChanged(object? sender, EventArgs e)
    {
        if (_loading || _rebuilding) return;
        _cellTimer.Stop();
        _cellTimer.Start();
    }

    /// <summary>Commits a text cell that is still being edited in any of the three grids (CellValueChanged then validates and stores it).</summary>
    void CommitDirtyCells()
    {
        _cellTimer.Stop();
        foreach (var g in new[] { _start, _move, _warn })
            if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit);
    }

    PhaseDef? Current => _selectedPhase == null ? null : _s.Rules?.Phases.FirstOrDefault(p => p.Id == _selectedPhase);

    int SelectedIndex => _selectedPhase == null || _s.Rules is not { } p ? -1 : p.Phases.FindIndex(x => x.Id == _selectedPhase);

    string FirstFormulaId() => _s.Rules?.Formulas.FirstOrDefault()?.Id ?? "";

    void OnRulesChanged() => OnUi(() => { if (!_selfEdit) Reload(); });

    void OnUi(Action a)
    {
        if (IsDisposed) return;
        if (!InvokeRequired) { Run(a); return; }
        if (!IsHandleCreated) { _dirty = true; return; }
        BeginInvoke(new Action(() => { if (!IsDisposed) Run(a); }));
    }

    void CommitPack(Func<RulesPack, RulesPack> edit)
    {
        _selfEdit = true;
        try { _s.UpdateRules(edit); }
        finally { _selfEdit = false; }
    }

    void CommitPhase(Func<PhaseDef, PhaseDef> edit, bool rebuild = false)
    {
        if (_selectedPhase is not { } id) return;
        CommitPack(p => PhaseEdit.UpdatePhase(p, id, edit));
        RefreshList();
        if (rebuild) RebuildGrids();
        ShowIssues();
    }

    /// <summary>Runs a structural edit. A refused edit shows its message and changes nothing.</summary>
    void Structural(Func<RulesPack, (RulesPack Pack, string? Error)> op, int? nextIndex = null, bool keepSelected = false)
    {
        Flush();
        if (_s.Rules is not { } pack) return;
        var (next, err) = op(pack);
        if (err != null) { Warn(err); return; }
        _selfEdit = true;
        try { _s.UpdateRules(_ => next); }
        finally { _selfEdit = false; }
        if (!keepSelected && nextIndex is { } ni && ni >= 0 && ni < next.Phases.Count) _selectedPhase = next.Phases[ni].Id;
        else if (_selectedPhase != null && next.Phases.All(p => p.Id != _selectedPhase)) _selectedPhase = null;
        Reload();
    }

    // ---------- loading ----------

    void Reload()
    {
        _dirty = false;
        if (_timer.Enabled) Flush();
        var pack = _s.Rules;
        _loading = true;
        try
        {
            _sides.Items.Clear();
            _sideIds.Clear();
            foreach (var sd in pack?.Sides ?? new List<SideDef>()) { _sideIds.Add(sd.Id); _sides.Items.Add(sd.Name); }
            var si = _selectedSide == null ? -1 : _sideIds.IndexOf(_selectedSide);
            if (si < 0 && _sideIds.Count > 0) si = 0;
            _sides.SelectedIndex = si;
            _selectedSide = si >= 0 ? _sideIds[si] : null;
        }
        finally { _loading = false; }
        LoadSide();
        if (pack == null || pack.Phases.All(p => p.Id != _selectedPhase)) _selectedPhase = pack?.Phases.FirstOrDefault()?.Id;
        RefreshList();
        LoadPhase();
    }

    void RefreshList()
    {
        var pack = _s.Rules;
        var prevLoading = _loading;
        _loading = true;
        var keepSel = _phases.SelectedItems.Cast<ListViewItem>().Select(i => (string?)i.Tag).ToHashSet();
        _phases.BeginUpdate();
        try
        {
            _phases.Items.Clear();
            if (pack == null) return;
            var prefix = new string?[pack.Phases.Count];
            foreach (var b in pack.Blocks)
            {
                var f = pack.Phases.FindIndex(p => p.Id == b.FirstPhaseId);
                var l = pack.Phases.FindIndex(p => p.Id == b.LastPhaseId);
                if (f < 0 || l < 0 || f > l) continue;
                var label = string.IsNullOrWhiteSpace(b.Label) ? "block" : b.Label;
                for (var i = f; i <= l; i++) prefix[i] = $"┃ {label} ×{b.Count}  ";
            }
            for (var i = 0; i < pack.Phases.Count; i++)
            {
                var ph = pack.Phases[i];
                var side = ph.SideId == null ? "" : pack.Sides.FirstOrDefault(s => s.Id == ph.SideId)?.Name ?? MissingText;
                var it = new ListViewItem(new[] { (prefix[i] ?? "") + ph.Name, side }) { Tag = ph.Id, Selected = keepSel.Count > 1 ? keepSel.Contains(ph.Id) : ph.Id == _selectedPhase };
                _phases.Items.Add(it);
            }
        }
        finally { _phases.EndUpdate(); _loading = prevLoading; }
    }

    void LoadSide()
    {
        _loading = true;
        try
        {
            var sd = _selectedSide == null ? null : _s.Rules?.Sides.FirstOrDefault(x => x.Id == _selectedSide);
            _sideFilter.Enabled = sd != null;
            if (_sideFilter.Text != (sd?.UnitFilter ?? "")) _sideFilter.Text = sd?.UnitFilter ?? "";
            _sideFilterErr.Text = FormulaEdit.SyntaxProblem(_sideFilter.Text) ?? "";
        }
        finally { _loading = false; }
    }

    void LoadPhase()
    {
        var pack = _s.Rules;
        var ph = Current;
        _loading = true;
        try
        {
            _right.Enabled = ph != null;
            _name.Text = ph?.Name ?? "";
            _instr.Text = ph?.Instructions ?? "";
            _notes.Text = ph?.Notes ?? "";
            _side.Items.Clear();
            _phaseSideIds.Clear();
            _side.Items.Add(None);
            _phaseSideIds.Add("");
            if (pack != null) foreach (var sd in pack.Sides) { _side.Items.Add(sd.Name); _phaseSideIds.Add(sd.Id); }
            var idx = 0;
            if (ph?.SideId is { } sid)
            {
                idx = _phaseSideIds.IndexOf(sid);
                if (idx < 0) { _side.Items.Add(MissingText); _phaseSideIds.Add(sid); idx = _phaseSideIds.Count - 1; }
            }
            _side.SelectedIndex = idx;
            _check.Items.Clear();
            foreach (var c in ph?.Checklist ?? new List<string>()) _check.Items.Add(c);
            ShowRefLabel(ph);
        }
        finally { _loading = false; }
        RebuildGrids();
        ShowIssues();
    }

    void ShowIssues()
    {
        if (_s.Rules is not { } pack || Current is not { } ph) { _issues.Text = ""; return; }
        try
        {
            var mine = RulesValidator.Validate(pack).Where(i => i.EntityId == ph.Id).Select(i => i.Message).ToList();
            _issues.Text = mine.Count == 0 ? "" : string.Join(Environment.NewLine, mine);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("phases validation failed: " + ex.Message);
            _issues.Text = "";
        }
    }

    // ---------- selection ----------

    void OnPhaseSelected()
    {
        if (_loading) return;
        Flush();
        var it = _phases.SelectedItems.Count > 0 ? _phases.SelectedItems[0] : null;
        var id = it?.Tag as string;
        if (id == null) return; // multi-select clears and reselects; keep the last phase shown
        if (id == _selectedPhase) return;
        _selectedPhase = id;
        LoadPhase();
    }

    void OnSideSelected()
    {
        if (_loading) return;
        Flush();
        _selectedSide = _sides.SelectedIndex >= 0 && _sides.SelectedIndex < _sideIds.Count ? _sideIds[_sides.SelectedIndex] : null;
        LoadSide();
    }

    // ---------- text fields ----------

    void Flush()
    {
        _timer.Stop();
        if (_cellTimer.Enabled) CommitDirtyCells();
        if (_loading || _s.Rules is not { } pack) return;
        if (Current is { } ph)
        {
            var name = _name.Text;
            if (ph.Name != name || ph.Instructions != _instr.Text || (ph.Notes ?? "") != _notes.Text)
            {
                var text = _instr.Text;
                var notes = _notes.Text;
                CommitPack(p => PhaseEdit.UpdatePhase(p, ph.Id, x => x with { Name = name, Instructions = text, Notes = notes.Length == 0 ? null : notes }));
                RefreshList();
                ShowIssues();
            }
        }
        if (_selectedSide is { } sid && pack.Sides.FirstOrDefault(x => x.Id == sid) is { } sd && (sd.UnitFilter ?? "") != _sideFilter.Text.Trim())
        {
            var f = _sideFilter.Text;
            CommitPack(p => PhaseEdit.SetSideFilter(p, sid, f).Pack);
            ShowIssues();
        }
    }

    void OnSideChosen()
    {
        if (_loading || Current == null) return;
        var i = _side.SelectedIndex;
        if (i < 0 || i >= _phaseSideIds.Count) return;
        var id = _phaseSideIds[i];
        CommitPhase(p => p with { SideId = id.Length == 0 ? null : id });
    }

    // ---------- sides ----------

    void AddSide()
    {
        Flush();
        var name = InputDialog.Ask(this, "New side", "Name of the new side:", "");
        if (string.IsNullOrWhiteSpace(name) || _s.Rules is not { } pack) return;
        var (next, err) = PhaseEdit.AddSide(pack, name);
        if (err != null) { Warn(err); return; }
        _selectedSide = next.Sides[^1].Id;
        _s.UpdateRules(_ => next);
    }

    void RenameSide()
    {
        if (_selectedSide is not { } id || _s.Rules is not { } pack || pack.Sides.FirstOrDefault(x => x.Id == id) is not { } sd) return;
        var name = InputDialog.Ask(this, "Rename side", "New name:", sd.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var (next, err) = PhaseEdit.RenameSide(pack, id, name);
        if (err != null) { Warn(err); return; }
        _s.UpdateRules(_ => next);
    }

    void RemoveSide()
    {
        if (_selectedSide is not { } id || _s.Rules is not { } pack || pack.Sides.FirstOrDefault(x => x.Id == id) is not { } sd) return;
        var used = pack.Phases.Count(p => p.SideId == id);
        var extra = used > 0 ? $" {used} phase(s) will have no side." : "";
        if (MessageBox.Show(this, $"Remove the side \"{sd.Name}\"?{extra}", "Remove side", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _timer.Stop();
        _selectedSide = null;
        _s.UpdateRules(p => PhaseEdit.RemoveSide(p, id).Pack);
    }

    // ---------- phases ----------

    void AddPhase()
    {
        Flush();
        if (_s.Rules is not { } pack) return;
        var name = InputDialog.Ask(this, "New phase", "Name of the new phase:", "Phase " + (pack.Phases.Count + 1));
        if (string.IsNullOrWhiteSpace(name)) return;
        var at = SelectedIndex < 0 ? pack.Phases.Count : SelectedIndex + 1;
        Structural(p => PhaseEdit.Add(p, name, at), nextIndex: at);
    }

    void RemovePhase()
    {
        if (Current is not { } ph) return;
        if (MessageBox.Show(this, $"Remove the phase \"{ph.Name}\"?", "Remove phase", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var idx = SelectedIndex;
        _timer.Stop();
        Structural(p => PhaseEdit.Remove(p, idx), nextIndex: Math.Max(0, idx - 1));
    }

    void MakeBlock()
    {
        Flush();
        var sel = _phases.SelectedIndices.Cast<int>().OrderBy(i => i).ToList();
        if (sel.Count == 0) { Warn("Select the phases that repeat together first."); return; }
        if (sel[^1] - sel[0] + 1 != sel.Count) { Warn("Select neighbouring phases"); return; }
        var ask = BlockDialog.Ask(this);
        if (ask is not { } a) return;
        Structural(p => PhaseEdit.MakeBlock(p, sel[0], sel[^1], a.Count, a.Label), keepSelected: true);
    }

    void RemoveBlock()
    {
        Flush();
        if (_s.Rules is not { } pack || SelectedIndex < 0) { Warn("Select a phase inside a block first."); return; }
        var bi = pack.Blocks.FindIndex(b =>
        {
            var f = pack.Phases.FindIndex(p => p.Id == b.FirstPhaseId);
            var l = pack.Phases.FindIndex(p => p.Id == b.LastPhaseId);
            return f >= 0 && l >= f && SelectedIndex >= f && SelectedIndex <= l;
        });
        if (bi < 0) { Warn("Select a phase inside a block first."); return; }
        Structural(p => PhaseEdit.RemoveBlock(p, bi), keepSelected: true);
    }

    // ---------- rule ref ----------

    void ShowRefLabel(PhaseDef? ph)
    {
        var r = ph?.RuleRefId == null ? null : _s.Rules?.ById<RuleRef>(ph.RuleRefId);
        _refLabel.Text = r == null ? None : $"{r.Label}, page {r.Page}";
        _showRefBtn.Enabled = r != null;
        _link.Enabled = ph != null;
    }

    void LinkPage()
    {
        if (Current is not { } ph) return;
        var r = _linkCurrentPage(ph.Name);
        if (r == null) return;
        CommitPhase(x => x with { RuleRefId = r.Id });
        ShowRefLabel(Current);
    }

    void ShowRef()
    {
        if (Current?.RuleRefId is { } id && _s.Rules?.ById<RuleRef>(id) is { } r) _showRef?.Invoke(r);
    }

    // ---------- checklist ----------

    void AddCheck()
    {
        var text = InputDialog.Ask(this, "Checklist item", "Text of the item:", "");
        if (string.IsNullOrWhiteSpace(text)) return;
        CommitPhase(p => p with { Checklist = p.Checklist.Append(text.Trim()).ToList() });
        LoadChecklist();
    }

    void RemoveCheck()
    {
        var i = _check.SelectedIndex;
        if (i < 0) return;
        CommitPhase(p => p with { Checklist = Without(p.Checklist, i) });
        LoadChecklist();
    }

    void EditCheck()
    {
        var i = _check.SelectedIndex;
        if (i < 0 || Current is not { } ph || i >= ph.Checklist.Count) return;
        var text = InputDialog.Ask(this, "Checklist item", "Text of the item:", ph.Checklist[i]);
        if (string.IsNullOrWhiteSpace(text)) return;
        CommitPhase(p => p with { Checklist = p.Checklist.Select((c, k) => k == i ? text.Trim() : c).ToList() });
        LoadChecklist();
    }

    void LoadChecklist()
    {
        _check.Items.Clear();
        foreach (var c in Current?.Checklist ?? new List<string>()) _check.Items.Add(c);
    }

    // ---------- grids ----------

    string FormulaName(string id) => id.Length == 0 ? "" : _s.Rules?.ById<FormulaDef>(id)?.Name ?? MissingText;

    string? FormulaIdByName(string name) => _s.Rules?.Formulas.FirstOrDefault(f => f.Name == name)?.Id;

    void RebuildGrids()
    {
        _rebuilding = true;
        try
        {
            var pack = _s.Rules;
            var ph = Current;
            var formulas = pack?.Formulas.Select(f => f.Name).ToList() ?? new List<string>();
            foreach (var g in new[] { _start, _move, _warn }) { g.SuspendLayout(); g.Rows.Clear(); }
            if (ph != null)
            {
                foreach (var e in ph.OnStart)
                {
                    var i = _start.Rows.Add("", "", e.UnitFilter ?? "");
                    _start.Rows[i].Cells[0] = FormulasTab.ComboCell(formulas, FormulaName(e.FormulaId));
                    _start.Rows[i].Cells[1] = FormulasTab.ComboCell(Enum.GetNames<ForEachMode>(), e.ForEach.ToString());
                    SetExprError(_start[2, i]);
                }
                foreach (var e in ph.OnMove)
                {
                    var i = _move.Rows.Add("", e.UnitFilter ?? "");
                    _move.Rows[i].Cells[0] = FormulasTab.ComboCell(formulas, FormulaName(e.FormulaId));
                    SetExprError(_move[1, i]);
                }
                foreach (var w in ph.Warnings)
                {
                    var i = _warn.Rows.Add(w.Condition, w.Message, w.IsDecision);
                    SetExprError(_warn[0, i]);
                }
            }
        }
        finally
        {
            foreach (var g in new[] { _start, _move, _warn }) g.ResumeLayout();
            _rebuilding = false;
        }
    }

    static void SetExprError(DataGridViewCell c) =>
        c.ErrorText = FormulaEdit.SyntaxProblem(Convert.ToString(c.Value)) ?? "";

    static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    void OnStartCell(int row, int col)
    {
        if (_rebuilding || row < 0 || Current is not { } ph || row >= ph.OnStart.Count) return;
        var text = Convert.ToString(_start[col, row].Value) ?? "";
        Func<StartEntry, StartEntry>? edit = null;
        switch (col)
        {
            case 0:
                if (text == MissingText || FormulaIdByName(text) is not { } fid) return;
                edit = e => e with { FormulaId = fid };
                break;
            case 1:
                if (!Enum.TryParse<ForEachMode>(text, out var mode)) return;
                edit = e => e with { ForEach = mode };
                break;
            case 2:
                SetExprError(_start[col, row]);
                var filter = NullIfBlank(text);
                edit = e => e with { UnitFilter = filter };
                break;
        }
        if (edit != null) CommitPhase(p => p with { OnStart = p.OnStart.Select((e, i) => i == row ? edit(e) : e).ToList() });
    }

    void OnMoveCell(int row, int col)
    {
        if (_rebuilding || row < 0 || Current is not { } ph || row >= ph.OnMove.Count) return;
        var text = Convert.ToString(_move[col, row].Value) ?? "";
        Func<MoveEntry, MoveEntry>? edit = null;
        switch (col)
        {
            case 0:
                if (text == MissingText || FormulaIdByName(text) is not { } fid) return;
                edit = e => e with { FormulaId = fid };
                break;
            case 1:
                SetExprError(_move[col, row]);
                var filter = NullIfBlank(text);
                edit = e => e with { UnitFilter = filter };
                break;
        }
        if (edit != null) CommitPhase(p => p with { OnMove = p.OnMove.Select((e, i) => i == row ? edit(e) : e).ToList() });
    }

    void OnWarnCell(int row, int col)
    {
        if (_rebuilding || row < 0 || Current is not { } ph || row >= ph.Warnings.Count) return;
        var v = _warn[col, row].Value;
        var text = Convert.ToString(v) ?? "";
        Func<PhaseWarning, PhaseWarning> edit;
        switch (col)
        {
            case 0:
                SetExprError(_warn[col, row]);
                edit = w => w with { Condition = text };
                break;
            case 1:
                edit = w => w with { Message = text };
                break;
            case 2:
                var on = v is true;
                edit = w => w with { IsDecision = on };
                break;
            default:
                return;
        }
        CommitPhase(p => p with { Warnings = p.Warnings.Select((w, i) => i == row ? edit(w) : w).ToList() });
    }

    // ---------- block dialog ----------

    sealed class BlockDialog : Form
    {
        readonly NumericUpDown _count = new() { Minimum = 1, Maximum = 99, Value = 2, Width = 60 };
        readonly TextBox _label = new() { Width = 200 };

        BlockDialog()
        {
            Text = "Make block";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = ShowInTaskbar = false;
            ClientSize = new Size(300, 110);
            Controls.Add(new Label { Text = "Repeat count:", AutoSize = true, Left = 12, Top = 16 });
            _count.Left = 100; _count.Top = 12;
            Controls.Add(_count);
            Controls.Add(new Label { Text = "Label:", AutoSize = true, Left = 12, Top = 46 });
            _label.Left = 100; _label.Top = 42;
            Controls.Add(_label);
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 130, Top = 76 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 215, Top = 76 };
            Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        public static (int Count, string Label)? Ask(IWin32Window owner)
        {
            using var d = new BlockDialog();
            return d.ShowDialog(owner) == DialogResult.OK ? ((int)d._count.Value, d._label.Text.Trim()) : null;
        }
    }
}
