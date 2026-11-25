using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Vassal;
using Quartermaster.Render;
using Quartermaster.Render.Search;
using Quartermaster.Render.Workbench;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// Formulas editor of the rules workbench (port of WinForms FormulasTab). Everything is typed by the player: the formula text, what each variable
/// means, what the result changes. Edits go through UpdateRules; the text, applies-to and notes are committed 400 ms after the last keystroke.
/// The preview ("Test piece" and the sample-value box) is <see cref="FormulaPreview"/>.
/// </summary>
public sealed partial class FormulasTab : UserControl, IRuleRefEditor
{
    static readonly IBrush ErrorText = new SolidColorBrush(Color.FromRgb(0xB2, 0x22, 0x22));
    static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x66, 0xCC));

    readonly ModuleSession _s;
    readonly IDialogs _dialogs;
    readonly Func<string, RuleRef?> _linkCurrentPage;
    readonly Action<RuleRef>? _showRef;
    readonly ListBox _list = new();
    readonly List<string> _ids = new();
    readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 13, Height = 64 };
    readonly TextBlock _err = new() { Foreground = ErrorText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 6, 0) };
    readonly TextBlock _goto = new() { Text = "Go to error", Foreground = LinkBrush, TextDecorations = TextDecorations.Underline, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(0, 3, 0, 0) };
    readonly TextBox _applies = new() { Width = 260 };
    readonly TextBlock _appliesErr = new() { Foreground = ErrorText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    readonly TextBox _precond = new() { Width = 360, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 13 };
    readonly TextBlock _precondErr = new() { Foreground = ErrorText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBox _refusal = new() { Width = 360 };
    readonly RuleRefRow _refRow = new();
    readonly WrapPanel _phaseChecks = new();
    readonly CheckBox _groupRoll = new() { Content = "Roll once for the group", Margin = new Thickness(0, 0, 0, 2) };
    readonly FormulaPreview _preview;
    readonly Button _bNew = new() { Content = "New…", Width = 50 }, _bRename = new() { Content = "Rename…", Width = 62 }, _bDel = new() { Content = "Delete", Width = 50 };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly AskGate _askGate = new();
    readonly HashSet<string> _declined = new(StringComparer.Ordinal);
    TextBox? _lastEdit;
    ParseError? _error;
    string? _selected;
    bool _selfEdit, _loadingUi;

    public FormulasTab(ModuleSession s, IDialogs dialogs, Func<string, RuleRef?> linkCurrentPage, Action<RuleRef>? showRef = null)
    {
        _s = s;
        _dialogs = dialogs;
        _preview = new FormulaPreview(s);
        _linkCurrentPage = linkCurrentPage;
        _showRef = showRef;

        var leftBtns = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var b in new[] { _bNew, _bRename, _bDel }) { b.Margin = new Thickness(0, 0, 2, 2); leftBtns.Children.Add(b); }
        var left = new DockPanel { Width = 130 };
        DockPanel.SetDock(leftBtns, Dock.Bottom);
        left.Children.Add(leftBtns);
        left.Children.Add(_list);

        var errRow = new StackPanel { Orientation = Orientation.Horizontal };
        errRow.Children.Add(_err);
        errRow.Children.Add(_goto);
        var appliesRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        appliesRow.Children.Add(new TextBlock { Text = "Applies to:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        appliesRow.Children.Add(_applies);
        appliesRow.Children.Add(_appliesErr);
        var precondRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        precondRow.Children.Add(new TextBlock { Text = "Only runs when", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Width = 100 });
        precondRow.Children.Add(_precond);
        precondRow.Children.Add(_precondErr);
        var refusalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        refusalRow.Children.Add(new TextBlock { Text = "Refusal message", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Width = 100 });
        refusalRow.Children.Add(_refusal);
        ToolTip.SetTip(_precond, "A condition that must be true for the formula to run, for example Charge > 0. When it is false the run is refused and nothing is rolled or written. It cannot use a Roll variable or dice().");
        ToolTip.SetTip(_refusal, "Shown when the condition is false. Empty: \"<formula name> cannot be run now\".");
        var guardRows = new StackPanel { Children = { precondRow, refusalRow } };
        _refRow.Margin = new Thickness(0, 2, 0, 2);

        var rows = new Grid();
        rows.RowDefinitions.Add(new RowDefinition(64, GridUnitType.Pixel));
        for (var i = 0; i < 5; i++) rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        rows.RowDefinitions.Add(new RowDefinition(52, GridUnitType.Pixel));
        rows.RowDefinitions.Add(new RowDefinition(38, GridUnitType.Star));
        rows.RowDefinitions.Add(new RowDefinition(22, GridUnitType.Star));
        rows.RowDefinitions.Add(new RowDefinition(40, GridUnitType.Star));
        var cells = new Control[]
        {
            _text, errRow, appliesRow, guardRows, BuildPhasesBox(), BuildGroupRollBox(), _refRow,
            Group("Notes (shown as a tooltip on the formula title)", _notes),
            BuildBindingsBox(), BuildOutputsBox(),
            Group("Preview (sample values are not saved)", _preview),
        };
        for (var i = 0; i < cells.Length; i++) { Grid.SetRow(cells[i], i); rows.Children.Add(cells[i]); }
        rows.Margin = new Thickness(4, 0, 0, 0);

        var dock = new DockPanel { Margin = new Thickness(4) };
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);
        dock.Children.Add(rows);
        Content = dock;

        _bNew.Click += (_, _) => Run(NewFormulaAsync);
        _bRename.Click += (_, _) => Run(RenameFormulaAsync);
        _bDel.Click += (_, _) => Run(DeleteFormulaAsync);
        _list.SelectionChanged += (_, _) => Run(OnListSelection);
        _text.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty) return; if (_loadingUi) return; ShowTextErrors(); Restart(); };
        _applies.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty) return; if (_loadingUi) return; _appliesErr.Text = FormulaEdit.SyntaxProblem(_applies.Text) ?? ""; Restart(); };
        _precond.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty) return; if (_loadingUi) return; _precondErr.Text = FormulaEdit.SyntaxProblem(_precond.Text) ?? ""; Restart(); };
        _refusal.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty) return; if (_loadingUi) return; Restart(); };
        _precond.LostFocus += (_, _) => Run(() => FlushPending(final: true));
        _refusal.LostFocus += (_, _) => Run(() => FlushPending(final: true));
        _notes.PropertyChanged += (_, e) => { if (e.Property != TextBox.TextProperty) return; if (_loadingUi) return; Restart(); };
        _text.LostFocus += (_, _) => Run(() => FlushPending(final: true));
        _applies.LostFocus += (_, _) => Run(() => FlushPending(final: true));
        _notes.LostFocus += (_, _) => Run(() => FlushPending(final: true));
        _text.GotFocus += (_, _) => _lastEdit = _text;
        _notes.GotFocus += (_, _) => _lastEdit = _notes;
        _timer.Tick += (_, _) => OnTick();
        _goto.PointerPressed += (_, e) => { if (e.GetCurrentPoint(_goto).Properties.IsLeftButtonPressed) { e.Handled = true; GoToError(); } };
        _refRow.LinkButton.Click += (_, _) => Run(LinkPage);
        _refRow.LinkClicked += () => Run(ShowRef);
        _refRow.FindButton.Click += (_, _) => Run(Find);

        AttachedToVisualTree += (_, _) => { _s.RulesChanged += OnRulesChanged; Reload(); };
        DetachedFromVisualTree += (_, _) =>
        {
            Run(() => { FlushPending(final: false); _bind.CommitEdit(); _out.CommitEdit(); });   // a hidden tab is detached: what was typed must reach the pack first
            _s.RulesChanged -= OnRulesChanged;
            _timer.Stop();
        };
        Reload();
    }

    static Control Group(string title, Control body, Control? bar = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var head = new TextBlock { Text = title, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(head, Dock.Top);
        d.Children.Add(head);
        if (bar != null) { DockPanel.SetDock(bar, Dock.Top); d.Children.Add(bar); }
        d.Children.Add(body);
        return d;
    }

    Control BuildGroupRollBox()
    {
        ToolTip.SetTip(_groupRoll, "Roll the dice once for the group: in a batch the first run rolls and the others reuse its dice; for a phase entry, one roll per hex and side.");
        _groupRoll.IsCheckedChanged += (_, _) => Run(() =>
        {
            if (_loadingUi || Current is not { } f) return;
            FlushPending(final: false);
            var on = _groupRoll.IsChecked == true;
            if (f.GroupRoll != on) Commit(x => FormulaEdit.WithGroupRoll(x, on));
        });
        return _groupRoll;
    }

    Control BuildPhasesBox()
    {
        var hint = new TextBlock { Text = "None checked = any phase.", FontStyle = FontStyle.Italic, Margin = new Thickness(0, 0, 0, 2) };
        DockPanel.SetDock(hint, Dock.Bottom);
        var box = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var label = new TextBlock { Text = "Only in phases:", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0) };
        DockPanel.SetDock(label, Dock.Left);
        box.Children.Add(label);
        box.Children.Add(new StackPanel { Children = { new ScrollViewer { MaxHeight = 56, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _phaseChecks }, hint } });
        return box;
    }

    /// <summary>Rebuilds the checkbox per pack phase from the formula's phase list (an id no phase has stays in the list: the validator names it).</summary>
    void LoadPhaseChecks(FormulaDef? f)
    {
        _phaseChecks.Children.Clear();
        var on = f?.Phases ?? new List<string>();
        foreach (var ph in _s.Rules?.Phases ?? new List<PhaseDef>())
        {
            var c = new CheckBox { Content = ph.Name, Tag = ph.Id, IsChecked = on.Contains(ph.Id), IsEnabled = f != null, Margin = new Thickness(0, 0, 8, 0) };
            c.IsCheckedChanged += (_, _) => Run(() => OnPhaseChecked());
            _phaseChecks.Children.Add(c);
        }
    }

    void OnPhaseChecked()
    {
        if (_loadingUi || Current is not { } f) return;
        FlushPending(final: false);
        var shown = _phaseChecks.Children.OfType<CheckBox>().ToList();
        var ids = shown.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
        ids.AddRange((f.Phases ?? new List<string>()).Where(id => shown.All(c => (string)c.Tag! != id)));   // unknown ids are kept
        Commit(x => x with { Phases = ids.Count == 0 ? null : ids });
    }

    void OnTick() => Run(() => FlushPending(final: false));

    void Restart() { _timer.Stop(); _timer.Start(); }

    public string? SelectedFormulaId => _selected;

    /// <summary>Selects a formula by id (no-op when unknown).</summary>
    public void SelectFormula(string id)
    {
        var i = _ids.IndexOf(id);
        if (i >= 0) _list.SelectedIndex = i;
    }

    /// <summary>Commits what is being typed (text, applies-to, notes, an open cell edit) into the pack; call before saving.</summary>
    public void CommitPending()
    {
        Run(() => { FlushPending(final: false); _bind.CommitEdit(); _out.CommitEdit(); });
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
        _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Formula edit failed: " + ex.Message, "");
        try { _timer.Stop(); Reload(); }
        catch (Exception ex2) when (ex2 is not OutOfMemoryException) { _s.Log.Add(DiagnosticSeverity.Warning, "rules", "Formulas tab reload failed: " + ex2.Message, ""); }
    }

    FormulaDef? Current => _selected == null ? null : _s.Rules?.ById<FormulaDef>(_selected);

    /// <summary>The formula <paramref name="id"/> when it is still the selected one and still in the pack (re-check after every await).</summary>
    FormulaDef? StillCurrent(string id) => _selected == id ? Current : null;

    // ---------- model updates ----------

    void Commit(Func<FormulaDef, FormulaDef> edit)
    {
        if (_selected is { } id) CommitFor(id, edit);
    }

    void CommitFor(string id, Func<FormulaDef, FormulaDef> edit)
    {
        _selfEdit = true;
        try { _s.UpdateRules(p => p with { Formulas = p.Formulas.Select(f => f.Id == id ? edit(f) : f).ToList() }); }
        finally { _selfEdit = false; }
    }

    void OnRulesChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) OnRulesChangedUi();
        else Dispatcher.UIThread.Post(OnRulesChangedUi);
    }

    void OnRulesChangedUi()
    {
        if (_selfEdit) return;
        Run(Reload);
    }

    // ---------- list ----------

    void Reload()
    {
        if (_timer.IsEnabled) FlushPending(final: false);   // typed text is never overwritten by an outside change
        var pack = _s.Rules;
        _loadingUi = true;
        try
        {
            _ids.Clear();
            var names = new List<string>();
            foreach (var f in pack?.Formulas ?? new List<FormulaDef>()) { _ids.Add(f.Id); names.Add(f.Name); }
            _list.ItemsSource = names;
            var idx = _selected == null ? -1 : _ids.IndexOf(_selected);
            if (idx < 0 && _ids.Count > 0) idx = 0;
            _list.SelectedIndex = idx;
            _selected = idx >= 0 ? _ids[idx] : null;
        }
        finally { _loadingUi = false; }
        LoadDetails();
        RebuildBindings();
        RebuildOutputs();
        _preview.SetFormula(_selected);
    }

    void OnListSelection()
    {
        if (_loadingUi) return;
        FlushPending(final: true);
        _bind.CommitEdit();
        _out.CommitEdit();
        _selected = _list.SelectedIndex >= 0 && _list.SelectedIndex < _ids.Count ? _ids[_list.SelectedIndex] : null;
        LoadDetails();
        RebuildBindings();
        RebuildOutputs();
        _preview.SetFormula(_selected);
    }

    async Task NewFormulaAsync()
    {
        if (Owner is not { } w) return;
        var name = await _dialogs.AskTextAsync(w, "New formula", "Name of the new formula:", WorkbenchRules.UniqueName(_s.Rules?.Formulas.Select(x => x.Name) ?? [], "Formula"));
        if (string.IsNullOrWhiteSpace(name) || _s.Rules == null) return;
        if (NameRules.Problem(_s.Rules.Formulas.Select(x => x.Name), name, null) is { } bad) { await _dialogs.ShowWarningAsync(w, bad, "New formula"); return; }
        var f = new FormulaDef { Id = Guid.NewGuid().ToString("N"), Name = name.Trim() };
        FlushPending(final: true);
        _s.UpdateRules(p => p with { Formulas = p.Formulas.Append(f).ToList() });   // RulesChanged reloads the list
        SelectFormula(f.Id);
    }

    async Task RenameFormulaAsync()
    {
        if (Current is not { } f || Owner is not { } w) return;
        var id = f.Id;
        var name = await _dialogs.AskTextAsync(w, "Rename formula", "New name:", f.Name);
        if (string.IsNullOrWhiteSpace(name) || StillCurrent(id) is not { } f2) return;
        if (NameRules.Problem(_s.Rules!.Formulas.Select(x => x.Name), name, f2.Name) is { } bad) { await _dialogs.ShowWarningAsync(w, bad, "Rename formula"); return; }
        if (StillCurrent(id) == null) return;
        FlushPending(final: false);
        Commit(x => FormulaEdit.Rename(x, name.Trim()));
        Reload();
    }

    async Task DeleteFormulaAsync()
    {
        if (Current is not { } f || Owner is not { } w) return;
        var id = f.Id;
        if (!await _dialogs.AskYesNoAsync(w, $"Delete the formula \"{f.Name}\"?", "Delete formula", defaultNo: true)) return;
        if (StillCurrent(id) == null) return;
        _timer.Stop();
        _s.UpdateRules(p => p with { Formulas = p.Formulas.Where(x => x.Id != id).ToList() });
    }

    // ---------- text and details ----------

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static string Norm(string s) => s.Replace("\r\n", "\n");

    void LoadDetails()
    {
        _loadingUi = true;
        try
        {
            var f = Current;
            var on = f != null;
            _text.IsEnabled = _applies.IsEnabled = _precond.IsEnabled = _refusal.IsEnabled = _notes.IsEnabled = _refRow.LinkButton.IsEnabled = _refRow.FindButton.IsEnabled = on;
            if (_text.Text != (f?.Text ?? "") && Norm(_text.Text ?? "") != Norm(f?.Text ?? "")) _text.Text = f?.Text ?? "";
            if ((_applies.Text ?? "") != (f?.AppliesTo ?? "")) _applies.Text = f?.AppliesTo ?? "";
            _appliesErr.Text = FormulaEdit.SyntaxProblem(_applies.Text) ?? "";
            if ((_precond.Text ?? "") != (f?.Precondition ?? "")) _precond.Text = f?.Precondition ?? "";
            _precondErr.Text = FormulaEdit.SyntaxProblem(_precond.Text) ?? "";
            if ((_refusal.Text ?? "") != (f?.RefusalMessage ?? "")) _refusal.Text = f?.RefusalMessage ?? "";
            if (Norm(_notes.Text ?? "") != Norm(f?.Notes ?? "")) _notes.Text = f?.Notes ?? "";
            LoadPhaseChecks(f);
            _groupRoll.IsEnabled = on;
            _groupRoll.IsChecked = f?.GroupRoll == true;
            ShowRefLabel(f);
        }
        finally { _loadingUi = false; }
        ShowTextErrors();
    }

    void ShowTextErrors()
    {
        var t = _text.Text ?? "";
        _error = t.Length == 0 ? null : FormulaEdit.Analyze(t).Error;
        _err.Text = _error == null ? "" : FormulaEdit.ErrorText(_error);
        _goto.IsVisible = _error != null;
    }

    void GoToError()
    {
        if (_error == null) return;
        _text.Focus();
        var pos = Math.Min(Math.Max(_error.Position, 0), (_text.Text ?? "").Length);
        _text.SelectionStart = pos;
        _text.SelectionEnd = pos;
        _text.CaretIndex = pos;
    }

    /// <summary>
    /// Commits text, applies-to and notes. A parse error does not block saving (the validator reports it). Bindings of variables that left the text
    /// are kept (shown as unused) unless they were never configured; the final flush asks once per variable whether to remove the configured ones.
    /// </summary>
    void FlushPending(bool final)
    {
        _timer.Stop();
        if (_loadingUi || Current is not { } f) return;
        var text = _text.Text ?? "";
        var apply = (_applies.Text ?? "").Trim();
        var notes = _notes.Text ?? "";
        var precond = Blank(_precond.Text);   // blank is null, never ""
        var refusal = Blank(_refusal.Text);
        var precondChanged = f.Precondition != precond;
        if (f.RefusalMessage != refusal) Commit(x => x with { RefusalMessage = refusal });
        var textChanged = Norm(f.Text) != Norm(text);   // the box may hand back LF for a stored CRLF: that is no edit
        if (Norm(f.Notes ?? "") != Norm(notes)) Commit(x => x with { Notes = notes.Length == 0 ? null : notes });
        if (textChanged || precondChanged || (f.AppliesTo ?? "").Trim() != apply)
        {
            var analysis = FormulaEdit.Analyze(text);
            Commit(x =>
            {
                var y = x;
                if (textChanged) y = FormulaEdit.SetText(y, text);
                if ((x.AppliesTo ?? "").Trim() != apply) y = FormulaEdit.SetAppliesTo(y, apply);
                if (precondChanged) y = y with { Precondition = precond };
                return analysis.Error != null || !(textChanged || precondChanged) ? y : FormulaEdit.Sync(y, FormulaEdit.UsedVariables(y), keepConfigured: true).Formula;
            });
            if (analysis.Error == null && (textChanged || precondChanged)) RebuildBindings();
        }
        if (final) Run(() => AskRemoveUnusedAsync(f.Id));
    }

    /// <summary>Asks once per formula in flight, and once per variable overall (a declined variable is not asked again).</summary>
    async Task AskRemoveUnusedAsync(string id)
    {
        if (Owner is not { } w || !_askGate.TryEnter(id)) return;
        try
        {
            if (_s.Rules?.ById<FormulaDef>(id) is not { } f) return;
            var asked = FormulaEdit.Unused(f).Where(FormulaEdit.IsConfigured).Where(b => !_declined.Contains(id + "|" + b.Variable)).ToList();
            if (asked.Count == 0) return;
            var names = string.Join(", ", asked.Select(b => b.Variable));
            var yes = await _dialogs.AskYesNoAsync(w, $"In \"{f.Name}\", the variable(s) {names} are no longer in the formula. Remove their settings (kind, target, details)?", "Remove unused bindings", defaultNo: true);
            if (_s.Rules?.ById<FormulaDef>(id) == null) return;   // deleted while the question was open
            if (yes)
            {
                CommitFor(id, FormulaEdit.RemoveUnused);
                if (_selected == id) RebuildBindings();
            }
            else foreach (var b in asked) _declined.Add(id + "|" + b.Variable);
        }
        finally { _askGate.Exit(id); }
    }

    // ---------- rule ref ----------

    void ShowRefLabel(FormulaDef? f)
    {
        var r = f?.RuleRefId == null ? null : _s.Rules?.ById<RuleRef>(f.RuleRefId);
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
        if (!CanUseRuleRef || Current is not { } f) return false;
        FlushPending(final: false);
        var r = make(f.Name);
        if (r == null) return false;
        Commit(x => x with { RuleRefId = r.Id });
        ShowRefLabel(Current);
        return true;
    }

    public void RefreshRuleRef() => ShowRefLabel(Current);

    /// <summary>"Find in rulebook": the text selected in the formula or notes box, else the formula's name.</summary>
    void Find()
    {
        if (Current is not { } f || _findInRulebook == null) return;
        static bool Has(TextBox? b) => b != null && b.SelectionEnd != b.SelectionStart;
        var box = Has(_lastEdit) ? _lastEdit : Has(_text) ? _text : Has(_notes) ? _notes : null;
        var q = SearchPanelModel.FindText(box?.SelectedText, f.Name);
        if (q.Length > 0) _findInRulebook(q);
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

    // test hooks
    internal FormulaPreview Preview => _preview;
    internal ListBox List => _list;
    internal TextBox Text => _text;
    internal TextBox Applies => _applies;
    internal TextBox Notes => _notes;
    internal TextBox Precondition => _precond;
    internal TextBox RefusalMessage => _refusal;
    internal string ErrorLabel => _err.Text ?? "";
    internal string AppliesError => _appliesErr.Text ?? "";
    internal bool GoToErrorVisible => _goto.IsVisible;
    internal RuleRefRow RefRow => _refRow;
    internal IReadOnlyList<CheckBox> PhaseChecks => _phaseChecks.Children.OfType<CheckBox>().ToList();
    internal CheckBox GroupRollCheck => _groupRoll;
    internal Button NewButton => _bNew;
    internal Button RenameButton => _bRename;
    internal Button DeleteButton => _bDel;
    internal bool DebouncePending => _timer.IsEnabled;
    internal void FireDebounce() => OnTick();   // the timer tick, without waiting 400 ms (headless timers do not run under RunJobs)
    internal void ClickGoToError() => GoToError();
}
