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
    readonly RuleRefRow _refRow = new();
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
        _refRow.Margin = new Thickness(0, 2, 0, 2);

        var rows = new Grid();
        rows.RowDefinitions.Add(new RowDefinition(64, GridUnitType.Pixel));
        for (var i = 0; i < 3; i++) rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        rows.RowDefinitions.Add(new RowDefinition(52, GridUnitType.Pixel));
        rows.RowDefinitions.Add(new RowDefinition(38, GridUnitType.Star));
        rows.RowDefinitions.Add(new RowDefinition(22, GridUnitType.Star));
        rows.RowDefinitions.Add(new RowDefinition(40, GridUnitType.Star));
        var cells = new Control[]
        {
            _text, errRow, appliesRow, _refRow,
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

    static string Norm(string s) => s.Replace("\r\n", "\n");

    void LoadDetails()
    {
        _loadingUi = true;
        try
        {
            var f = Current;
            var on = f != null;
            _text.IsEnabled = _applies.IsEnabled = _notes.IsEnabled = _refRow.LinkButton.IsEnabled = _refRow.FindButton.IsEnabled = on;
            if (_text.Text != (f?.Text ?? "") && Norm(_text.Text ?? "") != Norm(f?.Text ?? "")) _text.Text = f?.Text ?? "";
            if ((_applies.Text ?? "") != (f?.AppliesTo ?? "")) _applies.Text = f?.AppliesTo ?? "";
            _appliesErr.Text = FormulaEdit.SyntaxProblem(_applies.Text) ?? "";
            if (Norm(_notes.Text ?? "") != Norm(f?.Notes ?? "")) _notes.Text = f?.Notes ?? "";
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
        var textChanged = Norm(f.Text) != Norm(text);   // the box may hand back LF for a stored CRLF: that is no edit
        if (Norm(f.Notes ?? "") != Norm(notes)) Commit(x => x with { Notes = notes.Length == 0 ? null : notes });
        if (textChanged || (f.AppliesTo ?? "").Trim() != apply)
        {
            var analysis = FormulaEdit.Analyze(text);
            Commit(x =>
            {
                var y = x;
                if (textChanged) y = FormulaEdit.SetText(y, text);
                if ((x.AppliesTo ?? "").Trim() != apply) y = FormulaEdit.SetAppliesTo(y, apply);
                return analysis.Error != null || !textChanged ? y : FormulaEdit.Sync(y, FormulaEdit.UsedVariables(y), keepConfigured: true).Formula;
            });
            if (analysis.Error == null && textChanged) RebuildBindings();
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
    internal string ErrorLabel => _err.Text ?? "";
    internal string AppliesError => _appliesErr.Text ?? "";
    internal bool GoToErrorVisible => _goto.IsVisible;
    internal RuleRefRow RefRow => _refRow;
    internal Button NewButton => _bNew;
    internal Button RenameButton => _bRename;
    internal Button DeleteButton => _bDel;
    internal bool DebouncePending => _timer.IsEnabled;
    internal void FireDebounce() => OnTick();   // the timer tick, without waiting 400 ms (headless timers do not run under RunJobs)
    internal void ClickGoToError() => GoToError();
}
