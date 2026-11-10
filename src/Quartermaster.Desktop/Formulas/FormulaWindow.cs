using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Formulas;

/// <summary>
/// The layout-C formula window, as WinForms FormulaDialog: the formula written out with an input box per variable, a live result and working
/// line, and Apply to the ledger. Non-modal and owned by the main window; open through <see cref="FormulaWindows.Show"/>. Hardcore and
/// semi-automatic check typed values (with Calculate); semi-automatic shows the cheat sheet beside it. An online guest's Calculate and Apply ask
/// the host. Closes itself when its run is no longer pending, its unit is gone, or (hot-seat) the unit belongs to a side no longer held.
/// </summary>
public sealed class FormulaWindow : Window
{
    readonly MainWindow _host;
    readonly ModuleSession _s;
    readonly FormulaDialogModel _model;
    readonly string _formulaId;
    readonly string? _pieceId;
    readonly RunLink? _link;
    FormulaFormView _view;
    readonly Border _formulaFrame = new() { BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 8, 8) };
    readonly StackPanel _formulaGroup;
    readonly TextBlock _working = new()
    {
        FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"), MaxWidth = 540, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 2),
    };
    readonly WrapPanel _inline = new() { Orientation = Orientation.Horizontal, MaxWidth = 540 };
    readonly Button _apply = Btn("&Apply to ledger", 110);
    readonly Button _calc = Btn("&Calculate", 85);
    readonly Button _cancel = Btn("Cancel", 75);
    readonly Button _ruleRef = Btn("&Rule ref…", 85);
    readonly CheckBox _physical = new() { Content = "Physical dice", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = new() { Margin = new Thickness(2, 3, 2, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly CheatSheetView? _cheat;
    DialogView? _last;
    GameSession? _game;
    bool _closing;
    bool _checkQueued;
    bool _sending;

    internal FormulaWindow(MainWindow host, ModuleSession s, string formulaId, string? pieceId, RunLink? link)
    {
        _host = host;
        _s = s;
        _formulaId = formulaId;
        _pieceId = pieceId;
        _link = link;
        var rules = s.Rules ?? throw new InvalidOperationException("No rules pack is loaded.");
        var runner = s.Runner ?? throw new InvalidOperationException("No rules pack is loaded.");
        var formula = rules.ById<FormulaDef>(formulaId) ?? new FormulaDef { Id = formulaId, Name = "(missing formula)" };
        _model = new FormulaDialogModel(runner, formula, pieceId) { Moved = link?.Moved };
        // Hardcore and semi-automatic: the user types the values and the window only checks them. Fully automatic keeps the prefilled boxes.
        if (s.Game?.State.Difficulty is { } mode)
        {
            var m = FormulaWindowRules.Mode(mode);
            if (m.Checked) _model.Checked = true;
            if (m.ShowCheatSheet) _cheat = new CheatSheetView();
        }

        string? piece = null;
        if (pieceId != null && s.Game?.State.Pieces.TryGetValue(pieceId, out var pi) == true) piece = PieceInfo.Name(pi);
        Title = FormulaWindowRules.Title(formula, piece);
        CanResize = false;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _view = NewView(formula);
        _formulaFrame.Child = _view;
        _formulaGroup = Group("Formula", _formulaFrame);
        SetNotesTip(formula.Notes);
        var wf = new StackPanel { Orientation = Orientation.Vertical };
        wf.Children.Add(_working);
        wf.Children.Add(_inline);
        var workingFrame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 8, 8), Child = wf };

        var body = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8, 8, 8, 0) };
        body.Children.Add(_formulaGroup);
        body.Children.Add(Group("Working", workingFrame));
        body.Children.Add(Buttons());
        var strip = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _status, Margin = new Thickness(-8, 0, -8, 0) };
        foreach (var b in new[] { _formulaFrame, workingFrame, strip }) b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("Classic.BorderBrush"));
        body.Children.Add(strip);
        Content = body;

        _debounce.Tick += (_, _) => { _debounce.Stop(); Recalc(); };
        _apply.Click += (_, _) => Guarded(ApplyToLedger);
        _calc.Click += (_, _) => Guarded(CalculateAll);
        _calc.IsVisible = _model.Checked;
        _cancel.Click += (_, _) => Close();
        _cancel.IsCancel = true;
        _apply.IsDefault = true;
        _ruleRef.Click += (_, _) => Guarded(OpenRuleRef);
        UpdateRuleRef(formula);
        _physical.IsCheckedChanged += (_, _) => { _model.PhysicalDice = _physical.IsChecked == true; Recalc(); };
        _physical.IsVisible = !s.IsGuest;   // the host rolls a guest's dice
        s.RulesChanged += OnRulesChanged;
        s.GameReplaced += OnGameReplaced;
        s.ViewChanged += OnViewChanged;
        HookGame();
        Opened += (_, _) => ShowCheatSheet();
        Recalc();
    }

    // ---- what the registry and tests read ----

    internal ModuleSession Session => _s;
    internal string Key => FormulaWindowRules.Key(_formulaId, _pieceId, _link?.RunId);

    /// <summary>The run unit this window applies to (null = not linked).</summary>
    public RunLink? Link => _link;

    internal string ResultText => _view.ResultBox.Text ?? "";
    internal string StatusText => _status.Text ?? "";
    internal string WorkingText => _working.Text ?? "";
    internal string? BoxText(string variable) => _view.BoxText(variable);
    internal bool BoxWrong(string variable) => _view.BoxWrong(variable);
    internal IReadOnlyList<Button> InlineRollButtons => _inline.Children.OfType<Button>().ToList();
    internal CheatSheetView? CheatSheet => _cheat;

    /// <summary>Types into a box as the user would (the result follows after the debounce).</summary>
    internal void Type(string variable, string text) => _view.Type(variable, text);

    // ---- layout ----

    static Button Btn(string winFormsText, double width) =>
        new() { Content = MenuText.FromWinForms(winFormsText), MinWidth = width, HorizontalContentAlignment = HorizontalAlignment.Center };

    static StackPanel Group(string title, Control frame)
    {
        var g = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 6) };
        g.Children.Add(new TextBlock { Text = title, Margin = new Thickness(6, 0, 0, 2) });
        g.Children.Add(frame);
        return g;
    }

    Control Buttons()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(_apply);
        row.Children.Add(_calc);
        row.Children.Add(_cancel);
        row.Children.Add(_ruleRef);
        row.Children.Add(_physical);
        return row;
    }

    FormulaFormView NewView(FormulaDef f) => new(FormulaLayout.Build(f, _s.Rules!), OnEdit, key => { _model.Reroll(key); Recalc(); });

    /// <summary>The formula's notes as a tooltip over the whole Formula group: its caption and frame and the view's rows and text (not the boxes).</summary>
    void SetNotesTip(string? notes)
    {
        var tip = string.IsNullOrEmpty(notes) ? null : notes;
        ToolTip.SetTip(_formulaGroup, tip);
        foreach (var c in _view.NoteTargets()) ToolTip.SetTip(c, tip);
    }

    // ---- session and game events ----

    void HookGame()
    {
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnGameRewound; }
        _game = _s.Game;
        if (_game != null) { _game.Changed += OnGameChanged; _game.Rewound += OnGameRewound; }
    }

    /// <summary>Runs on the UI thread after the current change (session events may come from any thread).</summary>
    void OnUi(Action a) => Dispatcher.UIThread.Post(() => { if (!_closing) Guarded(a); });

    /// <summary>One check per burst of game changes.</summary>
    void QueueCheck()
    {
        if (_checkQueued || _closing) return;
        _checkQueued = true;
        OnUi(() => { _checkQueued = false; CheckPieceThenRefresh(); });
    }

    void OnGameChanged(GameCommand _) => QueueCheck();

    void OnGameRewound() => QueueCheck();

    void OnGameReplaced() => OnUi(() => { HookGame(); CheckPieceThenRefresh(); });

    /// <summary>Hot-seat: once the unit belongs to a side the new viewer does not hold, the window closes (nothing of it stays on screen).
    /// Any view change (a step back too) also closes it when its run or unit is gone.</summary>
    void OnViewChanged() => OnUi(() =>
    {
        if (_s.ViewSides != null && !_s.CanAct(_pieceId)) { Close(); return; }
        if (FormulaWindowRules.ShouldClose(_s, _host.PhaseControl, _pieceId, _link?.RunId)) Close();
    });

    void CheckPieceThenRefresh()
    {
        // A cleared run (phase change, step back) or a replaced game closes the window: applying now would write into the wrong place.
        if (FormulaWindowRules.ShouldClose(_s, _host.PhaseControl, _pieceId, _link?.RunId)) { Close(); return; }
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>The pack changed while open: rebuild the layout from the new pack, or close if the formula is gone.</summary>
    void OnRulesChanged() => OnUi(ApplyPack);

    void ApplyPack()
    {
        try
        {
            if (_s.Rules?.ById<FormulaDef>(_formulaId) is not { } f || _s.Runner is not { } runner) { Close(); return; }
            _model.Rebind(runner, f);
            _view = NewView(f);
            _formulaFrame.Child = _view;
            SetNotesTip(f.Notes);
            UpdateRuleRef(f);
            _debounce.Stop();
            Recalc();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "Could not update: " + ex.Message;
        }
    }

    /// <summary>Rule ref is enabled while the formula's reference resolves in the pack.</summary>
    void UpdateRuleRef(FormulaDef f) => _ruleRef.IsEnabled = f.RuleRefId != null && _s.Rules?.ById<RuleRef>(f.RuleRefId) != null;

    void OpenRuleRef()
    {
        var rr = _model.Formula.RuleRefId is { } id ? _s.Rules?.ById<RuleRef>(id) : null;
        if (rr == null) return;
        _host.OpenDocumentAtPage(rr.DocumentEntry, rr.Page);
    }

    internal Button RuleRefButton => _ruleRef;

    void Unhook()
    {
        _closing = true;
        _s.RulesChanged -= OnRulesChanged;
        _s.GameReplaced -= OnGameReplaced;
        _s.ViewChanged -= OnViewChanged;
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnGameRewound; }
        _game = null;
        _debounce.Stop();
        FormulaWindows.Closed(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        Unhook();
        if (_cheat is { IsVisible: true }) _cheat.Close();
        base.OnClosed(e);
    }

    /// <summary>Opens the cheat sheet beside the window (right side, or left when there is no room), owned by it.</summary>
    void ShowCheatSheet()
    {
        if (_cheat == null || _cheat.IsVisible || _closing) return;
        var scale = RenderScaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var cheatWidth = (int)Math.Ceiling(_cheat.Width * scale);
        var x = Position.X + width + 8;
        var y = Position.Y;
        if (Screens.ScreenFromWindow(this)?.WorkingArea is { } area)
        {
            if (x + cheatWidth > area.Right) x = Math.Max(area.X, Position.X - 8 - cheatWidth);
            y = Math.Max(area.Y, Math.Min(y, area.Bottom - (int)Math.Ceiling(_cheat.Height * scale)));
        }
        _cheat.Position = new PixelPoint(x, y);
        RenderCheat();
        _cheat.Show(this);
    }

    void RenderCheat()
    {
        if (_cheat == null || _model.LastExpected is not { } exp || _s.Rules is not { } rules) return;
        _cheat.Render(CheatSheetModel.Build(exp, rules, _model.Hidden));
    }

    // ---- actions ----

    void OnEdit(string variable, string text)
    {
        _model.SetBoxText(variable, text);
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>The Calculate button: fills every box with the engine's value (rolling any dice); the boxes stay editable.</summary>
    void CalculateAll()
    {
        _debounce.Stop();
        if (_s.IsGuest) { SendToHost(calculate: true, preview: true); return; }   // a preview on the host: fills the boxes, applies nothing
        _model.Calculate(_s.Game?.State ?? new GameState());
        Recalc();
    }

    /// <summary>Re-runs the formula and pushes the result onto the controls. Never throws.</summary>
    void Recalc()
    {
        try
        {
            SyncMode();
            FormulaMasking.Configure(_model, _s);   // the viewer may have changed (hot-seat) or a snapshot arrived (guest)
            var v = _model.Evaluate(_s.Game?.State ?? new GameState());
            _last = v;
            _view.Apply(v);
            _working.Text = v.WorkingText;
            if (v.WorkingNegative) _working.Foreground = Brushes.Red; else _working.ClearValue(TextBlock.ForegroundProperty);
            _status.Text = v.Status;
            if (v.Trace.Error != null) _status.Foreground = Brushes.Firebrick; else _status.ClearValue(TextBlock.ForegroundProperty);
            // A guest's Apply asks the host, which checks the boxes with the full game; the local check can't (hidden values are absent).
            _apply.IsEnabled = (_s.IsGuest ? !_sending : v.CanApply) && _s.Game != null;
            _calc.IsEnabled = !_sending;
            RebuildInline(v);
            RenderCheat();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _last = null;   // never leave the previous (applicable) view behind
            _status.Text = "Could not update: " + ex.Message;
            _apply.IsEnabled = false;
            _s.Log.Add(DiagnosticSeverity.Error, "formula", "Formula dialog failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Defence in depth: the checking mode follows the game's current difficulty on every evaluation (the host also closes the window on a
    /// switch), so a typed override made while fully automatic can never feed an Apply in Hardcore or Semi-auto.
    /// </summary>
    void SyncMode()
    {
        if (_s.Game is not { } g) return;
        var m = FormulaWindowRules.Mode(g.State.Difficulty);
        _model.Checked = m.Checked;
        _calc.IsVisible = m.ShowCalculate;
    }

    void RebuildInline(DialogView v)
    {
        _inline.Children.Clear();
        foreach (var r in v.InlineRolls)
        {
            var key = r.Key;
            var b = new Button { Content = "↻ " + r.Text, Margin = new Thickness(0, 2, 6, 0), IsEnabled = !_s.IsGuest };   // a guest's dice are the host's
            ToolTip.SetTip(b, "Roll again");
            b.Click += (_, _) => Guarded(() => { _model.Reroll(key); Recalc(); });
            _inline.Children.Add(b);
        }
    }

    void ApplyToLedger()
    {
        _debounce.Stop();
        if (_s.IsGuest) { SendToHost(calculate: false); return; }
        Recalc();   // always re-evaluate against the current game and mode before writing anything
        if (_last is not { CanApply: true } v || _s.Game is not { } game || _s.Runner is not { } runner) return;
        // Hot-seat: the viewer may have changed while the window was open; another side's unit is refused.
        if (!_s.CanAct(_pieceId)) { _status.Text = "This unit " + ModuleSession.OtherSideText + "."; _apply.IsEnabled = false; return; }
        // v.Trace is the engine's own (expected) run: typed values only gate Apply, so a wrong formula can never reach the ledger.
        if (_host.Actions is { } port)
        {
            if (!port.ApplyTrace(_link?.RunId, _pieceId, v.Trace, _model.AutoCalculated))
            {
                _status.Text = "This calculation is no longer waiting.";
                _apply.IsEnabled = false;
                return;
            }
        }
        else
        {
            var fname = _s.Rules?.ById<FormulaDef>(_formulaId)?.Name ?? "Formula";
            var unit = _pieceId != null && game.State.Pieces.TryGetValue(_pieceId, out var up) ? PieceInfo.Name(up) : null;
            using var scope = game.BeginAction(string.IsNullOrWhiteSpace(unit) ? fname : $"{fname} on {unit}");
            foreach (var c in runner.Commands(v.Trace, null, sideId: _s.ActorSide())) game.Execute(CommandLog.Stamp(c));
        }
        Close();
    }

    /// <summary>
    /// Online guest: Calculate / Apply go to the host as a formula intent, awaited on the UI thread without blocking it; the boxes then show
    /// the host's verdict (states only). Closes when the host applied it; a host that doesn't answer is reported in the status line.
    /// </summary>
    async void SendToHost(bool calculate, bool preview = false)
    {
        if (_sending || _closing) return;
        if (_host.Actions is not { IsRemote: true } port) { _status.Text = RemoteActions.LocalOnlyText; return; }
        _sending = true;
        _apply.IsEnabled = _calc.IsEnabled = false;
        _status.Text = FormulaWindowRules.AskingHostText;
        FormulaVerdict v;
        try { v = await FormulaMasking.SendAsync(port, _model, _link?.RunId, calculate, preview); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Warning, "formula", "formula intent failed: " + ex.Message);
            v = FormulaVerdict.Refused(GuestSession.NoAnswerText);
            _model.ApplyVerdict(v);
        }
        finally { _sending = false; }
        if (_closing) return;
        if (v.Applied) { Close(); return; }
        Recalc();
    }

    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _s.Log.Add(DiagnosticSeverity.Error, "formula", "A formula action failed: " + ex.Message);
            if (!_closing && _host.MapTabs.MenuServices?.Dialogs is { } dialogs) _ = dialogs.ShowWarningAsync(this, ex.Message, "Quartermaster");
        }
    }
}
