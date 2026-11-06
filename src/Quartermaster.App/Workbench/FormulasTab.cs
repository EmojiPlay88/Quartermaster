using Quartermaster.Vassal;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;

namespace Quartermaster.App;

/// <summary>
/// Formulas editor of the rules workbench. Everything is typed by the player: the formula text, what each variable means, what the result changes.
/// Edits go through UpdateRules; the text is committed 400 ms after the last keystroke. The preview is an embedded preview-only FormulaDialog.
/// </summary>
sealed partial class FormulasTab : UserControl
{
    readonly ModuleSession _s;
    readonly Func<string, RuleRef?> _linkCurrentPage;
    readonly Action<RuleRef>? _showRef;
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly List<string> _ids = new();
    readonly TextBox _text = new() { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, WordWrap = true };
    readonly Label _err = new() { AutoSize = true, ForeColor = Color.Firebrick, Padding = new Padding(0, 3, 6, 0) };
    readonly LinkLabel _goto = new() { Text = "Go to error", AutoSize = true, Visible = false, Padding = new Padding(0, 3, 0, 0) };
    readonly TextBox _applies = new() { Width = 260 };
    readonly Label _appliesErr = new() { AutoSize = true, ForeColor = Color.Firebrick, Padding = new Padding(6, 5, 0, 0) };
    readonly Label _refLabel = new() { AutoSize = true, Padding = new Padding(0, 5, 8, 0) };
    readonly Button _link = new() { Text = "Link to current page", AutoSize = true };
    readonly Button _showRefBtn = new() { Text = "Show", AutoSize = true };
    readonly ComboBox _piece = new() { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems, Width = 260 };
    readonly Panel _previewHost = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    readonly List<string?> _pieceIds = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    FormulaDialog? _preview;
    string? _selected;
    string? _pieceId;
    readonly HashSet<string> _declined = new(StringComparer.Ordinal);
    bool _selfEdit, _loadingUi;

    public FormulasTab(ModuleSession s, Func<string, RuleRef?> linkCurrentPage, Action<RuleRef>? showRef = null)
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

        var errRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        errRow.Controls.AddRange(new Control[] { _err, _goto });
        var appliesRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        appliesRow.Controls.Add(new Label { Text = "Applies to:", AutoSize = true, Padding = new Padding(0, 5, 2, 0) });
        appliesRow.Controls.Add(_applies);
        appliesRow.Controls.Add(_appliesErr);
        var refRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        refRow.Controls.Add(new Label { Text = "Rule ref:", AutoSize = true, Padding = new Padding(0, 5, 2, 0) });
        refRow.Controls.AddRange(new Control[] { _refLabel, _link, _showRefBtn });
        _showRefBtn.Visible = showRef != null;

        var pickRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false };
        pickRow.Controls.Add(new Label { Text = "Test piece:", AutoSize = true, Padding = new Padding(0, 5, 2, 0) });
        pickRow.Controls.Add(_piece);
        var previewBox = new GroupBox { Text = "Preview (sample values are not saved)", Dock = DockStyle.Fill, Padding = new Padding(4) };
        previewBox.Controls.Add(_previewHost);
        previewBox.Controls.Add(pickRow);

        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        for (var i = 0; i < 3; i++) rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        rows.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        rows.Controls.Add(_text, 0, 0);
        rows.Controls.Add(errRow, 0, 1);
        rows.Controls.Add(appliesRow, 0, 2);
        rows.Controls.Add(refRow, 0, 3);
        rows.Controls.Add(BuildBindingsBox(), 0, 4);
        rows.Controls.Add(BuildOutputsBox(), 0, 5);
        rows.Controls.Add(previewBox, 0, 6);

        var right = new Panel { Dock = DockStyle.Fill, AutoScroll = true, AutoScrollMinSize = new Size(520, 640) };
        right.Controls.Add(rows);
        Controls.Add(right);
        Controls.Add(new Splitter { Dock = DockStyle.Left, Width = 4 });
        Controls.Add(left);

        bNew.Click += (_, _) => Run(NewFormula);
        bRename.Click += (_, _) => Run(RenameFormula);
        bDel.Click += (_, _) => Run(DeleteFormula);
        _list.SelectedIndexChanged += (_, _) => Run(OnListSelection);
        _text.TextChanged += (_, _) => { if (_loadingUi) return; ShowTextErrors(); _timer.Stop(); _timer.Start(); };
        _applies.TextChanged += (_, _) => { if (_loadingUi) return; _appliesErr.Text = FormulaEdit.SyntaxProblem(_applies.Text) ?? ""; _timer.Stop(); _timer.Start(); };
        _text.Leave += (_, _) => Run(() => FlushPending(final: true));
        _applies.Leave += (_, _) => Run(() => FlushPending(final: true));
        _timer.Tick += (_, _) => Run(() => FlushPending(final: false));
        _goto.LinkClicked += (_, _) => GoToError();
        _link.Click += (_, _) => Run(LinkPage);
        _showRefBtn.Click += (_, _) => Run(ShowRef);
        _piece.SelectedIndexChanged += (_, _) => Run(OnPieceChosen);
        _piece.Leave += (_, _) => Run(OnPieceChosen);
        _piece.DropDown += (_, _) => Run(RefreshPieces);

        s.RulesChanged += OnRulesChanged;
        s.GameReplaced += OnGameReplaced;
        Reload();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _s.RulesChanged -= OnRulesChanged;
            _s.GameReplaced -= OnGameReplaced;
            _timer.Dispose();
        }
        base.Dispose(disposing); // the embedded preview is a child control, so it is disposed (and unhooked) with the tab
    }

    public string? SelectedFormulaId => _selected;

    public void SelectFormula(string id)
    {
        var i = _ids.IndexOf(id);
        if (i >= 0) _list.SelectedIndex = i;
    }

    void Run(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("formulas tab: " + ex);
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Formula edit failed: " + ex.Message, "");
        }
    }

    FormulaDef? Current => _selected == null ? null : _s.Rules?.ById<FormulaDef>(_selected);

    // ---------- model updates ----------

    void Commit(Func<FormulaDef, FormulaDef> edit)
    {
        if (_selected is not { } id) return;
        _selfEdit = true;
        try { _s.UpdateRules(p => p with { Formulas = p.Formulas.Select(f => f.Id == id ? edit(f) : f).ToList() }); }
        finally { _selfEdit = false; }
    }

    void OnRulesChanged() => OnUi(() =>
    {
        if (_selfEdit) return;
        Reload();
    });

    void OnGameReplaced() => OnUi(() => { _pieceId = null; RefreshPieces(); OnPieceChosen(); });

    void OnUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(new Action(() => { if (!IsDisposed) Run(a); }));
        else Run(a);
    }

    // ---------- list ----------

    void Reload()
    {
        var pack = _s.Rules;
        var previous = _selected;
        _loadingUi = true;
        try
        {
            _ids.Clear();
            _list.Items.Clear();
            foreach (var f in pack?.Formulas ?? new List<FormulaDef>()) { _ids.Add(f.Id); _list.Items.Add(f.Name); }
            var idx = _selected == null ? -1 : _ids.IndexOf(_selected);
            if (idx < 0 && _ids.Count > 0) idx = 0;
            _list.SelectedIndex = idx;
            _selected = idx >= 0 ? _ids[idx] : null;
        }
        finally { _loadingUi = false; }
        if (_selected != previous) _declined.Clear();
        LoadDetails();
        RebuildBindings();
        RebuildOutputs();
        RefreshPieces();
        UpdatePreview();
    }

    void OnListSelection()
    {
        if (_loadingUi) return;
        FlushPending(final: true);
        _selected = _list.SelectedIndex >= 0 && _list.SelectedIndex < _ids.Count ? _ids[_list.SelectedIndex] : null;
        _declined.Clear();
        LoadDetails();
        RebuildBindings();
        RebuildOutputs();
        UpdatePreview();
    }

    void NewFormula()
    {
        var name = InputDialog.Ask(this, "New formula", "Name of the new formula:", "Formula " + (_ids.Count + 1));
        if (string.IsNullOrWhiteSpace(name) || _s.Rules == null) return;
        var f = new FormulaDef { Id = Guid.NewGuid().ToString("N"), Name = name.Trim() };
        FlushPending(final: true);
        _s.UpdateRules(p => p with { Formulas = p.Formulas.Append(f).ToList() });
        SelectFormula(f.Id);
    }

    void RenameFormula()
    {
        if (Current is not { } f) return;
        var name = InputDialog.Ask(this, "Rename formula", "New name:", f.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        Commit(x => FormulaEdit.Rename(x, name.Trim()));
        Reload();
    }

    void DeleteFormula()
    {
        if (Current is not { } f) return;
        if (MessageBox.Show(this, $"Delete the formula \"{f.Name}\"?", "Delete formula",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _timer.Stop();
        _s.UpdateRules(p => p with { Formulas = p.Formulas.Where(x => x.Id != f.Id).ToList() });
    }

    // ---------- text and details ----------

    void LoadDetails()
    {
        _loadingUi = true;
        try
        {
            var f = Current;
            var on = f != null;
            _text.Enabled = _applies.Enabled = _link.Enabled = _piece.Enabled = on;
            if (_text.Text != (f?.Text ?? "")) _text.Text = f?.Text ?? "";
            if (_applies.Text != (f?.AppliesTo ?? "")) _applies.Text = f?.AppliesTo ?? "";
            _appliesErr.Text = FormulaEdit.SyntaxProblem(_applies.Text) ?? "";
            ShowRefLabel(f);
        }
        finally { _loadingUi = false; }
        ShowTextErrors();
    }

    ParseError? _error;

    void ShowTextErrors()
    {
        _error = _text.Text.Length == 0 ? null : FormulaEdit.Analyze(_text.Text).Error;
        _err.Text = _error == null ? "" : FormulaEdit.ErrorText(_error);
        _goto.Visible = _error != null;
    }

    void GoToError()
    {
        if (_error == null) return;
        _text.Focus();
        _text.SelectionStart = Math.Min(Math.Max(_error.Position, 0), _text.TextLength);
        _text.SelectionLength = 0;
        _text.ScrollToCaret();
    }

    /// <summary>Commits text and AppliesTo. A parse error does not block saving (the validator reports it). Dropping a configured binding is only asked on the final flush.</summary>
    void FlushPending(bool final)
    {
        _timer.Stop();
        if (_loadingUi || Current is not { } f) return;
        var text = _text.Text;
        var apply = _applies.Text;
        var textChanged = f.Text != text;
        var appliesChanged = (f.AppliesTo ?? "") != apply.Trim();
        if (!textChanged && !appliesChanged && !(final && _declined.Count > 0)) return;
        var analysis = FormulaEdit.Analyze(text);
        var keep = true;
        if (analysis.Error == null && textChanged)
        {
            var asked = FormulaEdit.Stale(f, analysis.Variables).Where(FormulaEdit.IsConfigured).Where(b => !_declined.Contains(b.Variable)).ToList();
            if (final && asked.Count > 0)
            {
                var names = string.Join(", ", asked.Select(b => b.Variable));
                keep = MessageBox.Show(this, $"The variable(s) {names} are no longer in the formula. Remove their settings (kind, target, details)?", "Remove bindings",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes;
                if (keep) foreach (var b in asked) _declined.Add(b.Variable);
            }
        }
        var rebuild = false;
        Commit(x =>
        {
            var y = FormulaEdit.SetAppliesTo(FormulaEdit.SetText(x, text), apply);
            if (analysis.Error != null || !textChanged) return y;
            rebuild = true;
            return FormulaEdit.Sync(y, analysis.Variables, keepConfigured: keep).Formula;
        });
        if (rebuild) RebuildBindings();
    }

    // ---------- rule ref ----------

    void ShowRefLabel(FormulaDef? f)
    {
        var r = f?.RuleRefId == null ? null : _s.Rules?.ById<RuleRef>(f.RuleRefId);
        _refLabel.Text = r == null ? "(none)" : $"{r.Label}, page {r.Page}";
        _showRefBtn.Enabled = r != null;
    }

    void LinkPage()
    {
        if (Current is not { } f) return;
        var r = _linkCurrentPage(f.Name);
        if (r == null) return;
        Commit(x => x with { RuleRefId = r.Id });
        ShowRefLabel(Current);
    }

    void ShowRef()
    {
        if (Current?.RuleRefId is { } id && _s.Rules?.ById<RuleRef>(id) is { } r) _showRef?.Invoke(r);
    }

    // ---------- preview ----------

    void UpdatePreview()
    {
        if (_selected == null || _s.Runner == null) { _previewHost.Visible = false; return; }
        _previewHost.Visible = true;
        if (_preview == null || _preview.IsDisposed)
        {
            _preview = new FormulaDialog(_s, _selected, _pieceId, previewOnly: true);
            _preview.Embed(_previewHost);
        }
        else _preview.SetFormula(_selected);
    }

    void RefreshPieces()
    {
        var game = _s.Game;
        var maps = _s.Model.Maps;
        var entries = game == null ? Array.Empty<PieceEntry>() : PieceChoices.Entries(game.State, id => maps.FirstOrDefault(m => m.Id == id)?.Name ?? id).ToArray();
        _loadingUi = true;
        try
        {
            _pieceIds.Clear();
            _piece.Items.Clear();
            _pieceIds.Add(null);
            _piece.Items.Add("(no unit)");
            foreach (var e in entries) { _pieceIds.Add(e.PieceId); _piece.Items.Add(e.Display); }
            var i = _pieceId == null ? 0 : _pieceIds.IndexOf(_pieceId);
            if (i < 0) { i = 0; _pieceId = null; }
            _piece.SelectedIndex = i;
        }
        finally { _loadingUi = false; }
    }

    void OnPieceChosen()
    {
        if (_loadingUi) return;
        var i = _piece.SelectedIndex >= 0 ? _piece.SelectedIndex : _piece.Items.IndexOf(_piece.Text);
        if (i < 0 || i >= _pieceIds.Count)
        {
            // typed text that matches nothing: go back to the current choice
            var cur = _pieceIds.IndexOf(_pieceId);
            _loadingUi = true;
            try { _piece.SelectedIndex = cur < 0 ? 0 : cur; }
            finally { _loadingUi = false; }
            return;
        }
        if (_pieceIds[i] == _pieceId) return;
        _pieceId = _pieceIds[i];
        _preview?.SetPiece(_pieceId);
    }
}
