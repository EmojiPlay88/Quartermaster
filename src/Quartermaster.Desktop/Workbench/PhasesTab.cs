using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Vassal;
using Quartermaster.Render;
using Quartermaster.Render.Search;
using Quartermaster.Rules;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Phases editor of the rules workbench (port of WinForms PhasesTab): the turn's phases in order, the sides they belong to, repeating blocks,
/// and per-phase fields. Everything is typed by the user. Edits go through UpdateRules; text fields commit 400 ms after the last keystroke
/// (or when they lose focus), grid cells when their edit ends.
/// </summary>
public sealed class PhasesTab : UserControl, IRuleRefEditor
{
    const string None = "(none)";
    const string MissingText = "(missing)";
    const string PhasesTitle = "Phases";

    internal sealed record PhaseRow(string Text, string Side);

    readonly ModuleSession _s;
    readonly IDialogs _dialogs;
    readonly Func<string, RuleRef?> _linkCurrentPage;
    readonly Action<RuleRef>? _showRef;

    readonly ListBox _sides = new();
    readonly TextBox _sideFilter = new();
    readonly TextBlock _sideFilterErr = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    readonly ListBox _phases = new() { SelectionMode = SelectionMode.Multiple };
    readonly Control _right;
    readonly TextBox _name = new();
    readonly ComboBox _side = new() { Width = 220 };
    readonly TextBox _instr = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90 };
    readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 44 };
    readonly RuleRefRow _refRow = new();
    TextBox? _lastEdit;
    readonly ListBox _check = new() { Height = 90 };
    readonly EditGrid _start = new() { Height = 110 };
    readonly EditGrid _move = new() { Height = 100 };
    readonly EditGrid _warn = new() { Height = 110 };
    readonly TextBox _issues = new() { IsReadOnly = true, Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Height = 52, BorderThickness = new Thickness(0) };
    readonly Button _bSideAdd = new() { Content = "Add…", Width = 52 }, _bSideRename = new() { Content = "Rename…", Width = 62 }, _bSideDel = new() { Content = "Remove", Width = 56 };
    readonly Button _bAdd = new() { Content = "Add", Width = 50 }, _bDup = new() { Content = "Duplicate", Width = 62 }, _bDel = new() { Content = "Remove", Width = 56 },
        _bUp = new() { Content = "Move Up", Width = 62 }, _bDown = new() { Content = "Move Down", Width = 74 },
        _bBlock = new() { Content = "Make block from selection…" }, _bUnblock = new() { Content = "Remove block" };
    readonly Button _bCheckAdd = new() { Content = "Add…", Width = 52 }, _bCheckDel = new() { Content = "Remove", Width = 56 };
    readonly Button _startAdd = new() { Content = "Add", Width = 50 }, _startDel = new() { Content = "Remove", Width = 56 };
    readonly Button _moveAdd = new() { Content = "Add", Width = 50 }, _moveDel = new() { Content = "Remove", Width = 56 };
    readonly Button _warnAdd = new() { Content = "Add", Width = 50 }, _warnDel = new() { Content = "Remove", Width = 56 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly List<string> _sideIds = new();
    readonly List<string> _phaseIds = new();
    readonly List<string> _phaseSideIds = new();
    string? _selectedPhase, _selectedSide;
    bool _selfEdit, _loading;

    public PhasesTab(ModuleSession s, IDialogs dialogs, Func<string, RuleRef?> linkCurrentPage, Action<RuleRef>? showRef = null)
    {
        _s = s;
        _dialogs = dialogs;
        _linkCurrentPage = linkCurrentPage;
        _showRef = showRef;

        _phases.ItemTemplate = new FuncDataTemplate<PhaseRow>((row, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("175,55") };
            g.Children.Add(new TextBlock { Text = row?.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            var side = new TextBlock { Text = row?.Side, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(side, 1);
            g.Children.Add(side);
            return g;
        });

        // ----- left: phases above sides -----
        var sideBtns = Row(_bSideAdd, _bSideRename, _bSideDel);
        var filterRow = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        filterRow.Children.Add(new TextBlock { Text = "Units of this side:" });
        filterRow.Children.Add(_sideFilter);
        filterRow.Children.Add(_sideFilterErr);
        var sidesInner = new DockPanel();
        Dock(sidesInner, sideBtns, Avalonia.Controls.Dock.Top);
        Dock(sidesInner, filterRow, Avalonia.Controls.Dock.Bottom);
        sidesInner.Children.Add(_sides);
        var sidesBox = Box("Sides", sidesInner);
        sidesBox.Height = 190;

        var phaseBtns = new WrapPanel();
        foreach (var b in new[] { _bAdd, _bDup, _bDel, _bUp, _bDown, _bBlock, _bUnblock }) { b.Margin = new Thickness(0, 0, 2, 2); phaseBtns.Children.Add(b); }
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("175,55"), Margin = new Thickness(4, 0, 0, 2) };
        header.Children.Add(new TextBlock { Text = "Phase", FontWeight = FontWeight.SemiBold });
        var sideHead = new TextBlock { Text = "Side", FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(sideHead, 1);
        header.Children.Add(sideHead);
        var phasesInner = new DockPanel();
        Dock(phasesInner, header, Avalonia.Controls.Dock.Top);
        Dock(phasesInner, phaseBtns, Avalonia.Controls.Dock.Bottom);
        phasesInner.Children.Add(_phases);
        var phasesBox = Box("Phases, in order", phasesInner);

        var left = new DockPanel { Width = 270 };
        Dock(left, sidesBox, Avalonia.Controls.Dock.Bottom);
        left.Children.Add(phasesBox);

        // ----- right: fields of the selected phase -----
        var rows = new StackPanel { Margin = new Thickness(4, 0, 4, 0), Spacing = 4 };
        var nameRow = new DockPanel();
        var nameLabel = new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var sideLabel = new TextBlock { Text = "Side:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        Dock(nameRow, nameLabel, Avalonia.Controls.Dock.Left);
        Dock(nameRow, _side, Avalonia.Controls.Dock.Right);
        Dock(nameRow, sideLabel, Avalonia.Controls.Dock.Right);
        nameRow.Children.Add(_name);
        rows.Children.Add(nameRow);
        rows.Children.Add(Box("Instructions (shown while this phase is current)", _instr));
        rows.Children.Add(Box("Notes (shown as a tooltip on the phase)", _notes));
        rows.Children.Add(_refRow);

        var checkInner = new DockPanel();
        Dock(checkInner, Row(_bCheckAdd, _bCheckDel), Avalonia.Controls.Dock.Bottom);
        checkInner.Children.Add(_check);
        rows.Children.Add(Box("Checklist (double-click to edit)", checkInner));

        _start.SetColumns([new EditColumn("Formula", EditColumnKind.Combo, Weight: 90), new EditColumn("For each", EditColumnKind.Combo, Weight: 40), new EditColumn("Filter (units)", Weight: 90)]);
        _move.SetColumns([new EditColumn("Formula", EditColumnKind.Combo, Weight: 90), new EditColumn("Filter (units)", Weight: 90)]);
        _warn.SetColumns([new EditColumn("Condition", Weight: 90), new EditColumn("Message ({unit} = unit name)", Weight: 90), new EditColumn("Decision", EditColumnKind.Check, Weight: 30)]);
        rows.Children.Add(GridBox("On start: run formulas", _start, _startAdd, _startDel));
        rows.Children.Add(GridBox("On move: run formulas for moved units", _move, _moveAdd, _moveDel));
        rows.Children.Add(GridBox("Warnings", _warn, _warnAdd, _warnDel));
        rows.Children.Add(Box("Problems with this phase", _issues));
        _right = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var dock = new DockPanel { Margin = new Thickness(4) };
        Dock(dock, left, Avalonia.Controls.Dock.Left);
        dock.Children.Add(_right);
        Content = dock;

        // ----- events -----
        _bSideAdd.Click += (_, _) => Run(AddSideAsync);
        _bSideRename.Click += (_, _) => Run(RenameSideAsync);
        _bSideDel.Click += (_, _) => Run(RemoveSideAsync);
        _sides.SelectionChanged += (_, _) => Run(OnSideSelected);
        _sideFilter.TextChanged += (_, _) => { if (_loading) return; _sideFilterErr.Text = FormulaEdit.SyntaxProblem(_sideFilter.Text) ?? ""; Kick(); };
        _sideFilter.LostFocus += (_, _) => Run(Flush);

        _bAdd.Click += (_, _) => Run(AddPhaseAsync);
        _bDup.Click += (_, _) => Run(() => Structural(p => PhaseEdit.Duplicate(p, SelectedIndex), nextIndex: SelectedIndex + 1));
        _bDel.Click += (_, _) => Run(RemovePhaseAsync);
        _bUp.Click += (_, _) => Run(() => Structural(p => PhaseEdit.Move(p, SelectedIndex, -1), keepSelected: true));
        _bDown.Click += (_, _) => Run(() => Structural(p => PhaseEdit.Move(p, SelectedIndex, 1), keepSelected: true));
        _bBlock.Click += (_, _) => Run(MakeBlockAsync);
        _bUnblock.Click += (_, _) => Run(RemoveBlockAsync);
        _phases.SelectionChanged += (_, _) => Run(OnPhaseSelected);

        _name.TextChanged += (_, _) => { if (!_loading) Kick(); };
        _instr.TextChanged += (_, _) => { if (!_loading) Kick(); };
        _notes.TextChanged += (_, _) => { if (!_loading) Kick(); };
        _notes.LostFocus += (_, _) => Run(Flush);
        _name.LostFocus += (_, _) => Run(Flush);
        _instr.LostFocus += (_, _) => Run(Flush);
        _timer.Tick += (_, _) => Run(Flush);
        _instr.GotFocus += (_, _) => _lastEdit = _instr;
        _notes.GotFocus += (_, _) => _lastEdit = _notes;
        _side.SelectionChanged += (_, _) => Run(OnSideChosen);
        _refRow.LinkButton.Click += (_, _) => Run(LinkPage);
        _refRow.LinkClicked += () => Run(ShowRef);
        _refRow.FindButton.Click += (_, _) => Run(Find);

        _bCheckAdd.Click += (_, _) => Run(AddCheckAsync);
        _bCheckDel.Click += (_, _) => Run(RemoveCheck);
        _check.DoubleTapped += (_, _) => Run(EditCheckAsync);

        _start.CellCommitted += (r, c, t) => Run(() => OnStartCell(r, c, t));
        _move.CellCommitted += (r, c, t) => Run(() => OnMoveCell(r, c, t));
        _warn.CellCommitted += (r, c, t) => Run(() => OnWarnCell(r, c, t));
        _startAdd.Click += (_, _) => Run(() => CommitPhase(p => p with { OnStart = p.OnStart.Append(new StartEntry { FormulaId = FirstFormulaId() }).ToList() }, rebuild: true));
        _startDel.Click += (_, _) => Run(() => { if (_start.CurrentCell is { } c) CommitPhase(p => p with { OnStart = Without(p.OnStart, c.Row) }, rebuild: true); });
        _moveAdd.Click += (_, _) => Run(() => CommitPhase(p => p with { OnMove = p.OnMove.Append(new MoveEntry { FormulaId = FirstFormulaId() }).ToList() }, rebuild: true));
        _moveDel.Click += (_, _) => Run(() => { if (_move.CurrentCell is { } c) CommitPhase(p => p with { OnMove = Without(p.OnMove, c.Row) }, rebuild: true); });
        _warnAdd.Click += (_, _) => Run(() => CommitPhase(p => p with { Warnings = p.Warnings.Append(new PhaseWarning()).ToList() }, rebuild: true));
        _warnDel.Click += (_, _) => Run(() => { if (_warn.CurrentCell is { } c) CommitPhase(p => p with { Warnings = Without(p.Warnings, c.Row) }, rebuild: true); });

        AttachedToVisualTree += (_, _) => { _s.RulesChanged += OnRulesChanged; Reload(); };
        DetachedFromVisualTree += (_, _) =>
        {
            Run(CommitPendingCore);   // a hidden tab is detached: what was typed must reach the pack first
            _s.RulesChanged -= OnRulesChanged;
            _timer.Stop();
        };
        Reload();
    }

    // ---------- layout helpers ----------

    static void Dock(DockPanel p, Control c, Dock d) { DockPanel.SetDock(c, d); p.Children.Add(c); }

    static WrapPanel Row(params Button[] buttons)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var b in buttons) { b.Margin = new Thickness(0, 0, 2, 2); w.Children.Add(b); }
        return w;
    }

    static Control Box(string title, Control content)
    {
        var d = new DockPanel();
        Dock(d, new TextBlock { Text = title, Margin = new Thickness(2, 0, 0, 2) }, Avalonia.Controls.Dock.Top);
        d.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(4), Child = content });
        return d;
    }

    static Control GridBox(string title, EditGrid grid, Button add, Button del)
    {
        var inner = new DockPanel();
        Dock(inner, Row(add, del), Avalonia.Controls.Dock.Bottom);
        inner.Children.Add(grid);
        inner.Height = grid.Height + 30;
        return Box(title, inner);
    }

    static List<T> Without<T>(List<T> list, int index) => list.Where((_, i) => i != index).ToList();

    // ---------- plumbing ----------

    public string? SelectedPhaseId => _selectedPhase;

    public int PhaseCount => _phaseIds.Count;

    /// <summary>Selects a phase by id (no-op when unknown).</summary>
    public void SelectPhase(string id)
    {
        var i = _phaseIds.IndexOf(id);
        if (i < 0) return;
        _phases.SelectedIndex = i;
        _phases.ScrollIntoView(i);
    }

    /// <summary>Commits what is being typed (the text fields, an open cell edit) into the pack; call before saving.</summary>
    public void CommitPending() => Run(CommitPendingCore);

    void CommitPendingCore()
    {
        Flush();
        _start.CommitEdit();
        _move.CommitEdit();
        _warn.CommitEdit();
    }

    Window? Owner => TopLevel.GetTopLevel(this) as Window;

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
        _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Phase edit failed: " + ex.Message, "");
        try { Reload(); }
        catch (Exception ex2) when (ex2 is not OutOfMemoryException) { _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Phases tab reload failed: " + ex2.Message, ""); }
    }

    Task Warn(string text) => Owner is { } w ? _dialogs.ShowWarningAsync(w, text, PhasesTitle) : Task.CompletedTask;

    void Kick() { _timer.Stop(); _timer.Start(); }

    PhaseDef? Current => _selectedPhase == null ? null : _s.Rules?.Phases.FirstOrDefault(p => p.Id == _selectedPhase);

    /// <summary>The phase <paramref name="id"/> when it is still the selected one and still in the pack (re-check after an await).</summary>
    PhaseDef? StillCurrent(string id) => _selectedPhase == id ? Current : null;

    int SelectedIndex => _selectedPhase == null || _s.Rules is not { } p ? -1 : p.Phases.FindIndex(x => x.Id == _selectedPhase);

    string FirstFormulaId() => _s.Rules?.Formulas.FirstOrDefault()?.Id ?? "";

    void OnRulesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) OnRulesChangedUi();
        else Dispatcher.UIThread.Post(OnRulesChangedUi);
    }

    // An external change reloads everything: unflushed text and a cell being edited are dropped as stale (as in the Tables tab).
    void OnRulesChangedUi()
    {
        if (_selfEdit) return;
        Run(Reload);
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
    async Task Structural(Func<RulesPack, (RulesPack Pack, string? Error)> op, int? nextIndex = null, bool keepSelected = false)
    {
        Flush();
        _start.CommitEdit(); _move.CommitEdit(); _warn.CommitEdit();
        if (_s.Rules is not { } pack) return;
        var (next, err) = op(pack);
        if (err != null) { await Warn(err); return; }
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
        _timer.Stop();
        var pack = _s.Rules;
        _loading = true;
        try
        {
            _sideIds.Clear();
            var names = new List<string>();
            foreach (var sd in pack?.Sides ?? new List<SideDef>()) { _sideIds.Add(sd.Id); names.Add(sd.Name); }
            _sides.ItemsSource = names;
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
        try
        {
            var keepSel = SelectedIds().ToHashSet();
            var rows = new List<PhaseRow>();
            _phaseIds.Clear();
            if (pack != null)
            {
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
                    var side = ph.SideId == null ? "" : pack.Sides.FirstOrDefault(x => x.Id == ph.SideId)?.Name ?? MissingText;
                    rows.Add(new PhaseRow((prefix[i] ?? "") + ph.Name, side));
                    _phaseIds.Add(ph.Id);
                }
            }
            _phases.ItemsSource = rows;
            var sel = _phases.Selection;
            sel.Clear();
            for (var i = 0; i < _phaseIds.Count; i++)
                if (keepSel.Count > 1 ? keepSel.Contains(_phaseIds[i]) : _phaseIds[i] == _selectedPhase) sel.Select(i);
        }
        finally { _loading = prevLoading; }
    }

    /// <summary>Ids of the selected phases, in list order.</summary>
    List<string> SelectedIds() =>
        _phases.Selection.SelectedIndexes.Where(i => i >= 0 && i < _phaseIds.Count).OrderBy(i => i).Select(i => _phaseIds[i]).ToList();

    void LoadSide()
    {
        var prev = _loading;
        _loading = true;
        try
        {
            var sd = _selectedSide == null ? null : _s.Rules?.Sides.FirstOrDefault(x => x.Id == _selectedSide);
            _sideFilter.IsEnabled = sd != null;
            if ((_sideFilter.Text ?? "") != (sd?.UnitFilter ?? "")) _sideFilter.Text = sd?.UnitFilter ?? "";
            _sideFilterErr.Text = FormulaEdit.SyntaxProblem(_sideFilter.Text) ?? "";
        }
        finally { _loading = prev; }
    }

    void LoadPhase()
    {
        var pack = _s.Rules;
        var ph = Current;
        var prev = _loading;
        _loading = true;
        try
        {
            _right.IsEnabled = ph != null;
            _name.Text = ph?.Name ?? "";
            _instr.Text = ph?.Instructions ?? "";
            _notes.Text = ph?.Notes ?? "";
            var names = new List<string> { None };
            _phaseSideIds.Clear();
            _phaseSideIds.Add("");
            if (pack != null) foreach (var sd in pack.Sides) { names.Add(sd.Name); _phaseSideIds.Add(sd.Id); }
            var idx = 0;
            if (ph?.SideId is { } sid)
            {
                idx = _phaseSideIds.IndexOf(sid);
                if (idx < 0) { names.Add(MissingText); _phaseSideIds.Add(sid); idx = _phaseSideIds.Count - 1; }
            }
            _side.ItemsSource = names;
            _side.SelectedIndex = idx;
            LoadChecklist();
            ShowRefLabel(ph);
        }
        finally { _loading = prev; }
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
            _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Phases validation failed: " + ex.Message, "");
            _issues.Text = "";
        }
    }

    // ---------- selection ----------

    void OnPhaseSelected()
    {
        if (_loading) return;
        var id = SelectedIds().FirstOrDefault();   // read first: flushing refreshes the list, which re-selects the old phase
        Flush();
        _start.CommitEdit(); _move.CommitEdit(); _warn.CommitEdit();
        if (id == null) return;   // multi-select clears and reselects; keep the last phase shown
        if (id == _selectedPhase) return;
        if (_s.Rules?.Phases.All(p => p.Id != id) != false) return;
        _selectedPhase = id;
        RefreshList();
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

    static string Norm(string s) => s.Replace("\r\n", "\n");

    void Flush()
    {
        _timer.Stop();
        if (_loading || _s.Rules is not { } pack) return;
        if (Current is { } ph)
        {
            var name = _name.Text ?? "";
            var instr = _instr.Text ?? "";
            var notes = _notes.Text ?? "";
            // a box may hand back LF for a stored CRLF: that is no edit
            var nameChanged = ph.Name != name;
            var instrChanged = Norm(ph.Instructions ?? "") != Norm(instr);
            var notesChanged = Norm(ph.Notes ?? "") != Norm(notes);
            if (nameChanged || instrChanged || notesChanged)
            {
                CommitPack(p => PhaseEdit.UpdatePhase(p, ph.Id, x => x with
                {
                    Name = nameChanged ? name : x.Name,
                    Instructions = instrChanged ? instr : x.Instructions,
                    Notes = notesChanged ? (notes.Length == 0 ? null : notes) : x.Notes,
                }));
                RefreshList();
                ShowIssues();
            }
        }
        if (_selectedSide is { } sid && pack.Sides.FirstOrDefault(x => x.Id == sid) is { } sd && (sd.UnitFilter ?? "").Trim() != (_sideFilter.Text ?? "").Trim())
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
        if ((Current.SideId ?? "") == id) return;
        CommitPhase(p => p with { SideId = id.Length == 0 ? null : id });
    }

    // ---------- sides ----------

    async Task AddSideAsync()
    {
        Flush();
        if (Owner is not { } w) return;
        var name = await _dialogs.AskTextAsync(w, "New side", "Name of the new side:", "");
        if (string.IsNullOrWhiteSpace(name) || _s.Rules is not { } pack) return;
        var (next, err) = PhaseEdit.AddSide(pack, name);
        if (err != null) { await Warn(err); return; }
        _selectedSide = next.Sides[^1].Id;
        _s.UpdateRules(_ => next);
    }

    async Task RenameSideAsync()
    {
        if (_selectedSide is not { } id || _s.Rules is not { } pack || pack.Sides.FirstOrDefault(x => x.Id == id) is not { } sd || Owner is not { } w) return;
        var name = await _dialogs.AskTextAsync(w, "Rename side", "New name:", sd.Name);
        if (string.IsNullOrWhiteSpace(name) || _selectedSide != id || _s.Rules is not { } pack2) return;
        var (next, err) = PhaseEdit.RenameSide(pack2, id, name);
        if (err != null) { await Warn(err); return; }
        _s.UpdateRules(_ => next);
    }

    async Task RemoveSideAsync()
    {
        if (_selectedSide is not { } id || _s.Rules is not { } pack || pack.Sides.FirstOrDefault(x => x.Id == id) is not { } sd || Owner is not { } w) return;
        var used = pack.Phases.Count(p => p.SideId == id);
        var extra = used > 0 ? $" {used} phase(s) will have no side." : "";
        if (!await _dialogs.AskYesNoAsync(w, $"Remove the side \"{sd.Name}\"?{extra}", "Remove side", defaultNo: true)) return;
        if (_selectedSide != id || _s.Rules?.Sides.All(x => x.Id != id) != false) return;
        _timer.Stop();
        _s.UpdateRules(p => PhaseEdit.RemoveSide(p, id).Pack);
    }

    // ---------- phases ----------

    async Task AddPhaseAsync()
    {
        Flush();
        if (_s.Rules is not { } pack || Owner is not { } w) return;
        var after = _selectedPhase;
        var name = await _dialogs.AskTextAsync(w, "New phase", "Name of the new phase:", "Phase " + (pack.Phases.Count + 1));
        if (string.IsNullOrWhiteSpace(name) || _s.Rules is not { } now) return;
        var i = after == null ? -1 : now.Phases.FindIndex(x => x.Id == after);
        var at = i < 0 ? now.Phases.Count : i + 1;
        await Structural(p => PhaseEdit.Add(p, name, at), nextIndex: at);
    }

    async Task RemovePhaseAsync()
    {
        if (Current is not { } ph || Owner is not { } w) return;
        var id = ph.Id;
        if (!await _dialogs.AskYesNoAsync(w, $"Remove the phase \"{ph.Name}\"?", "Remove phase", defaultNo: true)) return;
        if (StillCurrent(id) == null) return;
        var idx = SelectedIndex;
        _timer.Stop();
        await Structural(p => PhaseEdit.Remove(p, idx), nextIndex: Math.Max(0, idx - 1));
    }

    async Task MakeBlockAsync()
    {
        Flush();
        var ids = SelectedIds();
        if (ids.Count == 0) { await Warn("Select the phases that repeat together first."); return; }
        var sel = ids.Select(id => _phaseIds.IndexOf(id)).ToList();
        if (sel[^1] - sel[0] + 1 != sel.Count) { await Warn("Select neighbouring phases"); return; }
        if (Owner is not { } w) return;
        var ask = await _dialogs.AskBlockAsync(w);
        if (ask is not { } a) return;
        if (!SelectedIds().SequenceEqual(ids)) return;   // the selection changed while the dialog was open
        await Structural(p =>
        {
            var f = p.Phases.FindIndex(x => x.Id == ids[0]);
            var l = p.Phases.FindIndex(x => x.Id == ids[^1]);
            return f < 0 || l < 0 ? (p, "Select neighbouring phases") : PhaseEdit.MakeBlock(p, f, l, a.Count, a.Label);
        }, keepSelected: true);
    }

    async Task RemoveBlockAsync()
    {
        Flush();
        if (_s.Rules is not { } pack || SelectedIndex < 0) { await Warn("Select a phase inside a block first."); return; }
        var sel = SelectedIndex;
        var bi = pack.Blocks.FindIndex(b =>
        {
            var f = pack.Phases.FindIndex(p => p.Id == b.FirstPhaseId);
            var l = pack.Phases.FindIndex(p => p.Id == b.LastPhaseId);
            return f >= 0 && l >= f && sel >= f && sel <= l;
        });
        if (bi < 0) { await Warn("Select a phase inside a block first."); return; }
        await Structural(p => PhaseEdit.RemoveBlock(p, bi), keepSelected: true);
    }

    // ---------- rule ref ----------

    void ShowRefLabel(PhaseDef? ph)
    {
        var r = ph?.RuleRefId == null ? null : _s.Rules?.ById<RuleRef>(ph.RuleRefId);
        _refRow.Show(r, DescribeRef);
        _refRow.LinkButton.IsEnabled = _refRow.FindButton.IsEnabled = ph != null;
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
        if (!CanUseRuleRef || Current is not { } ph) return false;
        Flush();
        var r = make(ph.Name);
        if (r == null) return false;
        CommitPhase(x => x with { RuleRefId = r.Id });
        ShowRefLabel(Current);
        return true;
    }

    public void RefreshRuleRef() => ShowRefLabel(Current);

    /// <summary>"Find in rulebook": the text selected in the instructions or notes, else the phase's name.</summary>
    void Find()
    {
        if (Current is not { } ph || _findInRulebook == null) return;
        static bool Has(TextBox t) => t.SelectionEnd != t.SelectionStart;
        var box = _lastEdit is { } le && Has(le) ? le : Has(_instr) ? _instr : Has(_notes) ? _notes : null;
        var q = SearchPanelModel.FindText(box?.SelectedText, ph.Name);
        if (q.Length > 0) _findInRulebook(q);
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

    async Task AddCheckAsync()
    {
        if (Current is not { } ph || Owner is not { } w) return;
        var id = ph.Id;
        var text = await _dialogs.AskTextAsync(w, "Checklist item", "Text of the item:", "");
        if (string.IsNullOrWhiteSpace(text) || StillCurrent(id) == null) return;
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

    async Task EditCheckAsync()
    {
        var i = _check.SelectedIndex;
        if (i < 0 || Current is not { } ph || i >= ph.Checklist.Count || Owner is not { } w) return;
        var id = ph.Id;
        var old = ph.Checklist[i];
        var text = await _dialogs.AskTextAsync(w, "Checklist item", "Text of the item:", old);
        if (string.IsNullOrWhiteSpace(text) || StillCurrent(id) is not { } cur || i >= cur.Checklist.Count || cur.Checklist[i] != old) return;
        CommitPhase(p => p with { Checklist = p.Checklist.Select((c, k) => k == i ? text.Trim() : c).ToList() });
        LoadChecklist();
    }

    void LoadChecklist() => _check.ItemsSource = (Current?.Checklist ?? new List<string>()).ToList();

    // ---------- grids ----------

    string FormulaName(string id) => id.Length == 0 ? "" : _s.Rules?.ById<FormulaDef>(id)?.Name ?? MissingText;

    string? FormulaIdByName(string name) => _s.Rules?.Formulas.FirstOrDefault(f => f.Name == name)?.Id;

    void RebuildGrids()
    {
        var ph = Current;
        var formulas = _s.Rules?.Formulas.Select(f => f.Name).ToList() ?? new List<string>();
        var modes = Enum.GetNames<ForEachMode>();
        var start = ph?.OnStart ?? new List<StartEntry>();
        var move = ph?.OnMove ?? new List<MoveEntry>();
        var warn = ph?.Warnings ?? new List<PhaseWarning>();
        _start.SetRows(start.Select(e => (IReadOnlyList<string>)new[] { FormulaName(e.FormulaId), e.ForEach.ToString(), e.UnitFilter ?? "" }).ToList());
        _move.SetRows(move.Select(e => (IReadOnlyList<string>)new[] { FormulaName(e.FormulaId), e.UnitFilter ?? "" }).ToList());
        _warn.SetRows(warn.Select(w => (IReadOnlyList<string>)new[] { w.Condition, w.Message, w.IsDecision ? "True" : "False" }).ToList());
        // SetRows resets per-cell state: items and errors go on afterwards
        for (var i = 0; i < start.Count; i++)
        {
            _start.SetCellItems(i, 0, formulas);
            _start.SetCellItems(i, 1, modes);
            SetExprError(_start, i, 2);
        }
        for (var i = 0; i < move.Count; i++)
        {
            _move.SetCellItems(i, 0, formulas);
            SetExprError(_move, i, 1);
        }
        for (var i = 0; i < warn.Count; i++) SetExprError(_warn, i, 0);
    }

    static void SetExprError(EditGrid g, int row, int col) => g.SetCellError(row, col, FormulaEdit.SyntaxProblem(g.Cell(row, col)));

    static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    void OnStartCell(int row, int col, string text)
    {
        if (row < 0 || Current is not { } ph || row >= ph.OnStart.Count) return;
        Func<StartEntry, StartEntry> edit;
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
                SetExprError(_start, row, col);
                var filter = NullIfBlank(text);
                edit = e => e with { UnitFilter = filter };
                break;
            default: return;
        }
        CommitPhase(p => p with { OnStart = p.OnStart.Select((e, i) => i == row ? edit(e) : e).ToList() });
    }

    void OnMoveCell(int row, int col, string text)
    {
        if (row < 0 || Current is not { } ph || row >= ph.OnMove.Count) return;
        Func<MoveEntry, MoveEntry> edit;
        switch (col)
        {
            case 0:
                if (text == MissingText || FormulaIdByName(text) is not { } fid) return;
                edit = e => e with { FormulaId = fid };
                break;
            case 1:
                SetExprError(_move, row, col);
                var filter = NullIfBlank(text);
                edit = e => e with { UnitFilter = filter };
                break;
            default: return;
        }
        CommitPhase(p => p with { OnMove = p.OnMove.Select((e, i) => i == row ? edit(e) : e).ToList() });
    }

    void OnWarnCell(int row, int col, string text)
    {
        if (row < 0 || Current is not { } ph || row >= ph.Warnings.Count) return;
        Func<PhaseWarning, PhaseWarning> edit;
        switch (col)
        {
            case 0:
                SetExprError(_warn, row, col);
                edit = w => w with { Condition = text };
                break;
            case 1:
                edit = w => w with { Message = text };
                break;
            case 2:
                var on = string.Equals(text, "True", StringComparison.OrdinalIgnoreCase);
                edit = w => w with { IsDecision = on };
                break;
            default:
                return;
        }
        CommitPhase(p => p with { Warnings = p.Warnings.Select((w, i) => i == row ? edit(w) : w).ToList() });
    }

    // test hooks
    internal Task EditChecklistItem() => EditCheckAsync();
    internal ListBox PhaseList => _phases;
    internal ListBox SideList => _sides;
    internal IReadOnlyList<PhaseRow> PhaseRows => ((IEnumerable<PhaseRow>?)_phases.ItemsSource)?.ToList() ?? new List<PhaseRow>();
    internal TextBox NameBox => _name;
    internal ComboBox SideBox => _side;
    internal TextBox Instructions => _instr;
    internal TextBox Notes => _notes;
    internal TextBox SideFilter => _sideFilter;
    internal TextBlock SideFilterError => _sideFilterErr;
    internal TextBox Issues => _issues;
    internal ListBox Checklist => _check;
    internal EditGrid StartGrid => _start;
    internal EditGrid MoveGrid => _move;
    internal EditGrid WarnGrid => _warn;
    internal RuleRefRow RefRow => _refRow;
    internal Button AddButton => _bAdd;
    internal Button RemoveButton => _bDel;
    internal Button DuplicateButton => _bDup;
    internal Button UpButton => _bUp;
    internal Button DownButton => _bDown;
    internal Button BlockButton => _bBlock;
    internal Button UnblockButton => _bUnblock;
    internal Button SideAddButton => _bSideAdd;
    internal Button SideRenameButton => _bSideRename;
    internal Button SideRemoveButton => _bSideDel;
    internal Button StartAddButton => _startAdd;
    internal Button StartRemoveButton => _startDel;
    internal Button MoveAddButton => _moveAdd;
    internal Button WarnAddButton => _warnAdd;
    internal Button CheckAddButton => _bCheckAdd;
    internal Button CheckRemoveButton => _bCheckDel;
}
