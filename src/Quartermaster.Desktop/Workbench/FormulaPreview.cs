using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Workbench;

/// <summary>
/// The preview of the formulas tab (WinForms: a preview-only FormulaDialog embedded in the tab): "Test piece:" picker, the formula with an input
/// box per variable, the live result and working line. It only evaluates: it never applies anything, never writes to the pack or the game, and
/// logs nothing; typed sample values are kept in the control only. It follows the pack (a text or binding edit rebuilds it), the game and the
/// hot-seat view by itself while it is on screen.
/// </summary>
public sealed class FormulaPreview : UserControl
{
    readonly ModuleSession _s;
    readonly ComboBox _piece = new() { Width = 260, VerticalAlignment = VerticalAlignment.Center };
    readonly List<string?> _pieceIds = new();
    readonly Border _formulaFrame = new() { BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 8, 8) };
    readonly TextBlock _working = new()
    {
        FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"), MaxWidth = 540, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 2),
    };
    readonly WrapPanel _inline = new() { Orientation = Orientation.Horizontal, MaxWidth = 540 };
    readonly CheckBox _physical = new() { Content = "Physical dice", Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock _status = new() { Margin = new Thickness(2, 3, 2, 3), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly StackPanel _body = new() { Orientation = Orientation.Vertical };
    FormulaDialogModel? _model;
    FormulaFormView? _view;
    GameSession? _game;
    string? _formulaId;
    string? _pieceId;
    bool _loading, _attached, _physicalDefaultApplied;

    public FormulaPreview(ModuleSession s)
    {
        _s = s;
        var pick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        pick.Children.Add(new TextBlock { Text = "Test piece:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        pick.Children.Add(_piece);
        _formulaFrame.Bind(Border.BorderBrushProperty, _formulaFrame.GetResourceObservable("Classic.BorderBrush"));
        var wf = new StackPanel { Orientation = Orientation.Vertical };
        wf.Children.Add(_working);
        wf.Children.Add(_inline);
        var workingFrame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 8, 4), Child = wf };
        workingFrame.Bind(Border.BorderBrushProperty, workingFrame.GetResourceObservable("Classic.BorderBrush"));
        _body.Children.Add(_formulaFrame);
        _body.Children.Add(workingFrame);
        _body.Children.Add(_physical);
        _body.Children.Add(_status);
        var dock = new DockPanel();
        DockPanel.SetDock(pick, Dock.Top);
        dock.Children.Add(pick);
        dock.Children.Add(new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = dock;

        _piece.SelectionChanged += (_, _) => OnPieceChosen();
        _piece.DropDownOpened += (_, _) => RefreshPieces();
        _physical.IsCheckedChanged += (_, _) => { if (_model != null) { _model.PhysicalDice = _physical.IsChecked == true; Recalc(); } };
        _physical.IsVisible = !s.IsGuest;   // the host rolls a guest's dice
        _debounce.Tick += (_, _) => { _debounce.Stop(); Recalc(); };
        AttachedToVisualTree += (_, _) => Hook();
        DetachedFromVisualTree += (_, _) => Unhook();
        RefreshPieces();
    }

    public string? PieceId => _pieceId;
    public string? FormulaId => _formulaId;

    /// <summary>Previews another formula of the pack (null = none). Typed sample values of the previous one are dropped.</summary>
    public void SetFormula(string? formulaId)
    {
        if (formulaId == _formulaId && _model != null) return;
        _formulaId = formulaId;
        _model = null;
        Rebuild();
    }

    /// <summary>Runs the preview against another piece (null = no unit). Typed values are kept.</summary>
    public void SetPiece(string? pieceId)
    {
        _pieceId = pieceId;
        if (_model != null) _model.PieceId = pieceId;
        _debounce.Stop();
        Recalc();
    }

    // ---------- events ----------

    void Hook()
    {
        if (_attached) return;
        _attached = true;
        _s.RulesChanged += OnRulesChanged;
        _s.GameReplaced += OnGameReplaced;
        _s.ViewChanged += OnViewChanged;
        HookGame();
        RefreshPieces();
        ApplyPhysicalDefault();
        Rebuild();   // the pack may have changed while the tab was hidden
    }

    /// <summary>The first time the preview shows, Physical dice starts from the setting (the workbench is owned by the main window); later it is the user's.</summary>
    void ApplyPhysicalDefault()
    {
        if (_physicalDefaultApplied) return;
        if (TopLevel.GetTopLevel(this) is not Window { Owner: MainWindow host }) return;   // not owned yet: try again on the next attach
        _physicalDefaultApplied = true;
        if (host.SettingsStore.Current.PhysicalDiceDefault && !_s.IsGuest) _physical.IsChecked = true;
    }

    /// <summary>The Physical dice box (tests).</summary>
    internal CheckBox PhysicalBox => _physical;

    void Unhook()
    {
        if (!_attached) return;
        _attached = false;
        _s.RulesChanged -= OnRulesChanged;
        _s.GameReplaced -= OnGameReplaced;
        _s.ViewChanged -= OnViewChanged;
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnGameRewound; }
        _game = null;
        _debounce.Stop();
    }

    void HookGame()
    {
        if (_game != null) { _game.Changed -= OnGameChanged; _game.Rewound -= OnGameRewound; }
        _game = _s.Game;
        if (_game != null) { _game.Changed += OnGameChanged; _game.Rewound += OnGameRewound; }
    }

    void OnUi(Action a) => Dispatcher.UIThread.Post(() => { if (_attached) Safe(a); });

    void OnRulesChanged() => OnUi(Rebuild);
    void OnGameChanged(GameCommand _) => OnUi(Queue);
    void OnGameRewound() => OnUi(Queue);
    void OnGameReplaced() => OnUi(() => { HookGame(); _pieceId = null; if (_model != null) _model.PieceId = null; RefreshPieces(); Queue(); });
    void OnViewChanged() => OnUi(() => { RefreshPieces(); Queue(); });

    void Queue() { _debounce.Stop(); _debounce.Start(); }

    void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = "Could not update: " + ex.Message; }   // never logged: the preview writes nothing
    }

    // ---------- pieces ----------

    /// <summary>The units this player may see and act on (hot-seat), after "(no unit)".</summary>
    void RefreshPieces()
    {
        var game = _s.View;
        var maps = _s.Model.Maps;
        var entries = game == null ? Array.Empty<PieceEntry>() : PieceChoices.Entries(game, id => maps.FirstOrDefault(m => m.Id == id)?.Name ?? id).Where(e => _s.CanAct(e.PieceId)).ToArray();
        _loading = true;
        try
        {
            _pieceIds.Clear();
            _pieceIds.Add(null);
            var names = new List<string> { "(no unit)" };
            foreach (var e in entries) { _pieceIds.Add(e.PieceId); names.Add(e.Display); }
            _piece.ItemsSource = names;
            var i = _pieceId == null ? 0 : _pieceIds.IndexOf(_pieceId);
            if (i < 0) { i = 0; _pieceId = null; if (_model != null) _model.PieceId = null; }
            _piece.SelectedIndex = i;
        }
        finally { _loading = false; }
    }

    void OnPieceChosen()
    {
        if (_loading) return;
        var i = _piece.SelectedIndex;
        if (i < 0 || i >= _pieceIds.Count || _pieceIds[i] == _pieceId) return;
        SetPiece(_pieceIds[i]);
    }

    // ---------- evaluation ----------

    void Rebuild()
    {
        var rules = _s.Rules;
        var formula = _formulaId == null ? null : rules?.ById<FormulaDef>(_formulaId);
        if (formula == null || rules == null || _s.Runner is not { } runner)
        {
            _body.IsVisible = false;
            _piece.IsEnabled = false;
            _model = null;
            _view = null;
            return;
        }
        _body.IsVisible = true;
        _piece.IsEnabled = true;
        if (_model == null) _model = new FormulaDialogModel(runner, formula, _pieceId);
        else _model.Rebind(runner, formula);
        _model.PhysicalDice = _physical.IsChecked == true;
        _view = new FormulaFormView(FormulaLayout.Build(formula, rules), OnEdit, key => { _model!.Reroll(key); Recalc(); });
        _formulaFrame.Child = _view;
        _debounce.Stop();
        Recalc();
    }

    void OnEdit(string variable, string text)
    {
        _model?.SetBoxText(variable, text);
        Queue();
    }

    /// <summary>Re-runs the formula against the current game and shows the result. Reads only; never throws, never logs.</summary>
    void Recalc()
    {
        if (_model == null || _view == null) return;
        try
        {
            FormulaMasking.Configure(_model, _s);
            var v = _model.Evaluate(_s.Game?.State ?? new GameState());
            _view.Apply(v);
            _working.Text = v.WorkingText;
            if (v.WorkingNegative) _working.Foreground = Brushes.Red; else _working.ClearValue(TextBlock.ForegroundProperty);
            _status.Text = v.Status;
            if (v.Trace.Error != null) _status.Foreground = Brushes.Firebrick; else _status.ClearValue(TextBlock.ForegroundProperty);
            _inline.Children.Clear();
            foreach (var r in v.InlineRolls)
            {
                var key = r.Key;
                var b = new Button { Content = "↻ " + r.Text, Margin = new Thickness(0, 2, 6, 0), IsEnabled = !_s.IsGuest };
                ToolTip.SetTip(b, "Roll again");
                b.Click += (_, _) => Safe(() => { _model.Reroll(key); Recalc(); });
                _inline.Children.Add(b);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "Could not update: " + ex.Message;
        }
    }

    // test hooks
    internal ComboBox PieceBox => _piece;
    internal string ResultText => _view?.ResultBox.Text ?? "";
    internal string WorkingText => _working.Text ?? "";
    internal string StatusText => _status.Text ?? "";
    internal bool HasContent => _body.IsVisible && _view != null;
    internal void Type(string variable, string text) => _view?.Type(variable, text);
    internal string? BoxText(string variable) => _view?.BoxText(variable);
    internal void FireDebounce() { _debounce.Stop(); Recalc(); }
}
