using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Links a formula dialog to one unit of a pending run: Apply marks that unit done. Moved is the measured distance for OnMove runs.</summary>
sealed record RunLink(string RunId, double? Moved);

/// <summary>Layout-C formula dialog: the formula written out with an input box per variable, a live result and working line, and Apply to the ledger.</summary>
sealed class FormulaDialog : Form
{
    readonly ModuleSession _s;
    readonly FormulaDialogModel _model;
    readonly bool _previewOnly;
    FormulaFormView _view;
    readonly Label _working = new() { AutoSize = true, Font = new Font("Consolas", 9f), MaximumSize = new Size(540, 0), Margin = new Padding(0, 2, 0, 2) };
    readonly FlowLayoutPanel _inline = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, MaximumSize = new Size(540, 0), Margin = Padding.Empty };
    readonly Button _apply = new() { Text = "&Apply to ledger", Width = 110, Height = 25, Enabled = false };
    readonly Button _calc = new() { Text = "&Calculate", Width = 85, Height = 25 };
    readonly Button _cancel = new() { Text = "Cancel", Width = 75, Height = 25 };
    readonly Button _ruleRef = new() { Text = "&Rule ref…", Width = 85, Height = 25 };
    readonly CheckBox _physical = new() { Text = "Physical dice", AutoSize = true, Margin = new Padding(12, 5, 0, 0) };
    readonly ToolTip _notesTip = new() { ShowAlways = true, UseAnimation = false, UseFading = false, AutoPopDelay = 30000 };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    readonly TableLayoutPanel _body = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(8, 8, 8, 0) };
    readonly GroupBox _formulaGroup;
    string _formulaId;
    string? _pieceId;
    bool _embedded;
    DialogView? _last;
    GameSession? _game;
    bool _closing;
    readonly RunLink? _link;
    CheatSheetWindow? _cheat;

    static readonly List<FormulaDialog> Open = new();

    /// <summary>Shows a modeless dialog for the formula and piece, or activates the one already open.</summary>
    public static FormulaDialog? Show(ModuleSession s, string formulaId, string? pieceId, Form owner, MainForm? host, RunLink? link = null)
    {
        Open.RemoveAll(d => d.IsDisposed);
        if (!s.CanAct(pieceId))
        {
            var msg = "That unit " + ModuleSession.OtherSideText + ".";
            if (host != null) host.SetStatus(msg);
            else Program.AppLog("formula dialog refused: " + msg);
            return null;
        }
        var existing = Open.FirstOrDefault(d => d._s == s && d._formulaId == formulaId && d._pieceId == pieceId && d._link?.RunId == link?.RunId);
        if (existing != null)
        {
            if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            return existing;
        }
        var dlg = new FormulaDialog(s, formulaId, pieceId, false, link) { Host = host };
        Open.Add(dlg);
        dlg.Show(owner);
        return dlg;
    }

    /// <summary>Closes every open (non-preview) formula dialog of the session, e.g. after a difficulty switch.</summary>
    public static void CloseAllFor(ModuleSession s)
    {
        foreach (var d in Open.Where(d => d._s == s && !d.IsDisposed).ToList())
        {
            try { d.Close(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Program.AppLog("closing formula dialog: " + ex.Message); }
        }
    }

    /// <summary>Set before showing so Rule ref can open the document; falls back to the owner chain.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public MainForm? Host { get; set; }

    public FormulaDialog(ModuleSession s, string formulaId, string? pieceId, bool previewOnly = false, RunLink? link = null)
    {
        _s = s;
        _formulaId = formulaId;
        _pieceId = pieceId;
        _previewOnly = previewOnly;
        _link = link;
        var rules = s.Rules ?? throw new InvalidOperationException("No rules pack is loaded.");
        var runner = s.Runner ?? throw new InvalidOperationException("No rules pack is loaded.");
        var formula = rules.ById<FormulaDef>(formulaId) ?? new FormulaDef { Id = formulaId, Name = "(missing formula)" };
        _model = new FormulaDialogModel(runner, formula, pieceId) { Moved = link?.Moved };
        // Hardcore and semi-automatic: the user types the values and the dialog only checks them. Fully automatic keeps the prefilled dialog.
        var mode = s.Game?.State.Difficulty;
        if (!previewOnly && mode is Difficulty.Hardcore or Difficulty.SemiAuto) _model.Checked = true;
        if (!previewOnly && mode == Difficulty.SemiAuto) _cheat = new CheatSheetWindow();

        string? piece = null;
        if (pieceId != null && s.Game?.State.Pieces.TryGetValue(pieceId, out var pi) == true) piece = PieceInfo.Name(pi);
        Text = piece == null ? formula.Name : $"{formula.Name}: {piece}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _view = new FormulaFormView(FormulaLayout.Build(formula, rules), OnEdit, key => { _model.Reroll(key); Recalc(); });
        _view.MinimumSize = new Size(540, 0);
        _formulaGroup = Group("Formula", _view);
        _body.Controls.Add(_formulaGroup);
        SetNotesTip(formula.Notes);
        var wf = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        wf.Controls.Add(_working);
        wf.Controls.Add(_inline);
        _body.Controls.Add(Group("Working", wf));
        _body.Controls.Add(Buttons());
        var strip = new StatusStrip { SizingGrip = false, RenderMode = ToolStripRenderMode.System, Dock = DockStyle.Fill };
        strip.Items.Add(_status);
        _body.Controls.Add(strip);
        Controls.Add(_body);

        _debounce.Tick += (_, _) => { _debounce.Stop(); Recalc(); };
        _apply.Click += (_, _) => Guarded(ApplyToLedger);
        _calc.Click += (_, _) => Guarded(CalculateAll);
        _calc.Visible = _model.Checked;
        _cancel.Click += (_, _) => { if (!_embedded) Close(); };
        _ruleRef.Click += (_, _) => Guarded(OpenRuleRef);
        _physical.CheckedChanged += (_, _) => { _model.PhysicalDice = _physical.Checked; Recalc(); };
        _ruleRef.Enabled = formula.RuleRefId != null && rules.ById<RuleRef>(formula.RuleRefId) != null;
        _apply.Visible = !previewOnly;
        AcceptButton = _apply;
        CancelButton = _cancel;
        s.RulesChanged += OnRulesChanged;
        s.GameReplaced += OnGameReplaced;
        s.ViewChanged += OnViewChanged;
        HookGame();
        Recalc();
    }

    void HookGame()
    {
        if (_game != null) _game.Changed -= OnGameChanged;
        _game = _s.Game;
        if (_game != null) _game.Changed += OnGameChanged;
    }

    void OnUi(Action a)
    {
        if (IsDisposed || _closing || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed && !_closing) a(); }); else a();
    }

    void OnGameChanged(GameCommand _) => OnUi(CheckPieceThenRefresh);

    void OnGameReplaced() => OnUi(() => { HookGame(); CheckPieceThenRefresh(); });

    /// <summary>Hot-seat: once the unit belongs to a side the new viewer does not hold, its dialog closes (nothing of it stays on screen).</summary>
    void OnViewChanged()
    {
        if (_previewOnly || _embedded || _s.ViewSides == null || _viewQueued || IsDisposed || _closing || !IsHandleCreated) return;   // solo: nothing to do
        _viewQueued = true;   // coalesced: one check per burst of changes
        try
        {
            BeginInvoke(() =>
            {
                _viewQueued = false;
                if (IsDisposed || _closing) return;
                Guarded(() => { if (!_s.CanAct(_pieceId)) Close(); });
            });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { _viewQueued = false; }
    }

    bool _viewQueued;

    void CheckPieceThenRefresh()
    {
        if (_link != null && HostForm?.Phases is { } ph && !ph.IsPending(_link.RunId, _pieceId))
        {
            Close();   // its run was cleared (phase change) or the game was replaced: applying now would write into the wrong place
            return;
        }
        if (_pieceId != null && _s.Game?.State.Pieces.ContainsKey(_pieceId) != true)
        {
            if (_embedded) { SetPiece(null); return; }
            Close();
            return;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>The pack changed while open: rebuild the layout from the new pack, or close if the formula is gone.</summary>
    void OnRulesChanged() => OnUi(ApplyPack);

    void ApplyPack()
    {
        try
        {
            if (_s.Rules?.ById<FormulaDef>(_formulaId) is not { } f || _s.Runner is not { } runner)
            {
                if (_embedded) { _body.Visible = false; return; } // the editor keeps the window; it just has nothing to show
                Close();
                return;
            }
            _body.Visible = true;
            _model.Rebind(runner, f);
            ReplaceView(f);
            SetNotesTip(f.Notes);
            _ruleRef.Enabled = f.RuleRefId != null && _s.Rules.ById<RuleRef>(f.RuleRefId) != null;
            _debounce.Stop();
            Recalc();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _status.Text = "Could not update: " + ex.Message;
        }
    }

    /// <summary>The formula's notes as a tooltip over the whole Formula group: its caption and border and the view's rows and text (not the boxes).</summary>
    void SetNotesTip(string? notes)
    {
        _notesTip.SetToolTip(_formulaGroup, notes ?? "");
        foreach (var c in _view.NoteTargets()) _notesTip.SetToolTip(c, notes ?? "");
    }

    /// <summary>Builds the form view for the formula and swaps it in; a view made after the window was scaled is scaled to match.</summary>
    void ReplaceView(FormulaDef f)
    {
        var old = _view;
        _view = new FormulaFormView(FormulaLayout.Build(f, _s.Rules!), OnEdit, key => { _model.Reroll(key); Recalc(); }) { MinimumSize = new Size(540, 0) };
        _view.Margin = Padding.Empty;
        var host = old.Parent;
        if (host != null)
        {
            host.Controls.Remove(old);
            host.Controls.Add(_view);
            var factor = DeviceDpi / 96f;
            if (Math.Abs(factor - 1f) > 0.01f) _view.Scale(new SizeF(factor, factor));
        }
        old.Dispose();
    }

    void Unhook()
    {
        _closing = true;
        _s.RulesChanged -= OnRulesChanged;
        _s.GameReplaced -= OnGameReplaced;
        _s.ViewChanged -= OnViewChanged;
        if (_game != null) _game.Changed -= OnGameChanged;
        _game = null;
        _debounce.Stop();
        Open.Remove(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ShowCheatSheet();
    }

    /// <summary>Opens the cheat sheet beside the dialog (right side, or left when there is no room), owned by it.</summary>
    void ShowCheatSheet()
    {
        if (_cheat == null || _cheat.IsDisposed || _cheat.Visible) return;
        var area = Screen.FromControl(this).WorkingArea;
        var x = Right + 8;
        if (x + _cheat.Width > area.Right) x = Math.Max(area.Left, Left - 8 - _cheat.Width);
        _cheat.Location = new Point(x, Math.Max(area.Top, Math.Min(Top, area.Bottom - _cheat.Height)));
        RenderCheat();
        _cheat.Show(this);
    }

    void RenderCheat()
    {
        if (_cheat == null || _cheat.IsDisposed || _model.LastExpected is not { } exp || _s.Rules is not { } rules) return;
        _cheat.Render(CheatSheetModel.Build(exp, rules, _model.Hidden));
    }

    /// <summary>The Calculate button: fills every box with the engine's value (rolling any dice); the boxes stay editable.</summary>
    void CalculateAll()
    {
        _debounce.Stop();
        if (_s.IsGuest) { SendToHost(calculate: true); return; }   // the host calculates: a guest has no hidden data
        _model.Calculate(_s.Game?.State ?? new GameState());
        Recalc();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Unhook();
        base.OnFormClosed(e);
    }

    static GroupBox Group(string title, Control inner)
    {
        var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 8), Margin = new Padding(0, 0, 0, 6), Dock = DockStyle.Fill };
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill };
        inner.Margin = Padding.Empty;
        t.Controls.Add(inner);
        g.Controls.Add(t);
        return g;
    }

    Control Buttons()
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 0, 8) };
        row.Controls.AddRange(new Control[] { _apply, _calc, _cancel, _ruleRef, _physical });
        return row;
    }

    /// <summary>
    /// Shows this (preview-only) dialog as a child of another control, e.g. the workbench preview. The dialog keeps owning its body, timer and
    /// event hooks and releases them when disposed (disposing the host disposes it); it no longer closes itself when the piece or formula goes away.
    /// </summary>
    public void Embed(Control host)
    {
        _embedded = true;
        _cancel.Visible = false;
        TopLevel = false;
        FormBorderStyle = FormBorderStyle.None;
        AutoSize = false;
        AutoScroll = true;
        Dock = DockStyle.Fill;
        host.Controls.Add(this);
        Show();
    }

    /// <summary>Embedded preview: another formula (by id) of the same pack.</summary>
    public void SetFormula(string formulaId)
    {
        if (formulaId == _formulaId) return;
        _formulaId = formulaId;
        _model.PieceId = _pieceId;
        ApplyPack(); // direct: must not depend on the handle
    }

    /// <summary>Embedded preview: run against another piece (null = no unit). Typed values are kept.</summary>
    public void SetPiece(string? pieceId)
    {
        _pieceId = pieceId;
        _model.PieceId = pieceId;
        _debounce.Stop();
        Recalc();
    }

    void OnEdit(string variable, string text)
    {
        _model.SetBoxText(variable, text);
        _debounce.Stop();
        _debounce.Start();
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
            _working.ForeColor = v.WorkingNegative ? Color.Red : SystemColors.ControlText;
            _status.Text = v.Status;
            _status.ForeColor = v.Trace.Error != null ? Color.Firebrick : SystemColors.ControlText;
            // A guest's Apply asks the host, which checks the boxes with the full game; the local check can't (hidden values are absent).
            _apply.Enabled = (_s.IsGuest ? !_sending : v.CanApply) && !_previewOnly && _s.Game != null;
            _calc.Enabled = !_sending;
            RebuildInline(v);
            RenderCheat();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _last = null;   // never leave the previous (applicable) view behind
            _status.Text = "Could not update: " + ex.Message;
            _apply.Enabled = false;
            _s.Log.Add(DiagnosticSeverity.Error, "formula", "Formula dialog failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Defence in depth: the checking mode follows the game's current difficulty on every evaluation (the host also closes the dialog on a switch),
    /// so a typed override made while fully automatic can never feed an Apply in Hardcore or Semi-auto.
    /// </summary>
    void SyncMode()
    {
        if (_previewOnly || _s.Game is not { } g) return;
        var manual = g.State.Difficulty != Difficulty.FullyAuto;
        _model.Checked = manual;
        _calc.Visible = manual;
    }

    void RebuildInline(DialogView v)
    {
        _inline.SuspendLayout();
        foreach (var c in _inline.Controls.Cast<Control>().ToList()) { _inline.Controls.Remove(c); c.Dispose(); }
        foreach (var r in v.InlineRolls)
        {
            var key = r.Key;
            var b = new Button { Text = "↻ " + r.Text, AutoSize = true, Margin = new Padding(0, 2, 6, 0) };
            b.Click += (_, _) => { _model.Reroll(key); Recalc(); };
            _inline.Controls.Add(b);
        }
        _inline.ResumeLayout();
    }

    void ApplyToLedger()
    {
        _debounce.Stop();
        if (_s.IsGuest) { SendToHost(calculate: false); return; }
        Recalc();   // always re-evaluate against the current game and mode before writing anything
        if (_last is not { CanApply: true } v || _s.Game is not { } game || _s.Runner is not { } runner) return;
        // Hot-seat: the viewer may have changed while the dialog was open; another side's unit is refused.
        if (!_s.CanAct(_pieceId)) { _status.Text = "This unit " + ModuleSession.OtherSideText + "."; _apply.Enabled = false; return; }
        // v.Trace is the engine's own (expected) run: typed values only gate Apply, so a wrong formula can never reach the ledger.
        if (HostForm?.Actions is { } port)
        {
            if (!port.ApplyTrace(_link?.RunId, _pieceId, v.Trace, _model.AutoCalculated))
            {
                _status.Text = "This calculation is no longer waiting.";
                _apply.Enabled = false;
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
        if (!_embedded) Close();
    }

    bool _sending;

    /// <summary>
    /// Online guest: Calculate / Apply go to the host as a formula intent. Awaited on the UI thread without blocking it; the boxes then
    /// show the host's verdict (states only). Closes when the host applied it; a host that doesn't answer is reported in the status line.
    /// </summary>
    async void SendToHost(bool calculate)
    {
        if (_sending || _previewOnly || IsDisposed || _closing) return;
        if (HostForm?.Actions is not { IsRemote: true } port) { _status.Text = RemoteActions.LocalOnlyText; return; }
        _sending = true;
        _apply.Enabled = _calc.Enabled = false;
        _status.Text = "Asking the host…";
        FormulaVerdict v;
        try { v = await FormulaMasking.SendAsync(port, _model, _link?.RunId, calculate); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("formula intent failed: " + ex.Message);
            v = FormulaVerdict.Refused(GuestSession.NoAnswerText);
            _model.ApplyVerdict(v);
        }
        finally { _sending = false; }
        if (IsDisposed || _closing) return;
        if (v.Applied && !_embedded) { Close(); return; }
        Recalc();
    }

    MainForm? HostForm => Host ?? Owner as MainForm ?? (Owner as MapWindow)?.Host;

    void OpenRuleRef()
    {
        var rr = _model.Formula.RuleRefId is { } id ? _s.Rules?.ById<RuleRef>(id) : null;
        var host = HostForm;
        if (rr == null || host == null) return;
        host.OpenDocumentAtPage(rr.DocumentEntry, rr.Page);
    }

    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("formula dialog action failed: " + ex);
            _s.Log.Add(DiagnosticSeverity.Error, "formula", "A formula action failed: " + ex.Message);
            if (!Program.Smoke) MessageBox.Show(this, ex.Message, "Quartermaster", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Smoke only: types "1" into every empty typed box so the screenshot shows a worked example.</summary>
    public void SmokeFillInputs()
    {
        foreach (var b in _last?.Boxes ?? [])
            if (b.Background == BoxBackground.Typed && b.Text.Length == 0) _view.Type(b.Variable, "1");
        _debounce.Stop();
        Recalc();
    }

    /// <summary>Smoke: fills the inputs, clicks Apply and checks that the dialog closed and exactly the previewed commands were recorded. Null when fine, else the problem.</summary>
    public string? SmokeApply()
    {
        if (_model.Checked) _calc.PerformClick(); else SmokeFillInputs();
        if (_last is not { CanApply: true } v || _s.Game is not { } game) return "Apply is not available for this formula with filled inputs";
        var ledger = game.State.LedgerHistory.Count;
        var dice = game.State.DiceHistory.Count;
        _apply.PerformClick();
        if (!IsDisposed && Visible) return "dialog stayed open after Apply";
        if (game.State.LedgerHistory.Count != ledger + v.Trace.Outputs.Count) return $"expected {v.Trace.Outputs.Count} ledger entries, got {game.State.LedgerHistory.Count - ledger}";
        if (game.State.DiceHistory.Count != dice + v.Trace.Rolls.Count) return $"expected {v.Trace.Rolls.Count} dice entries, got {game.State.DiceHistory.Count - dice}";
        return null;
    }

    // ---- smoke hooks for the checked-mode flow ----
    public bool IsChecked => _model.Checked;
    public bool ApplyEnabled => _apply.Enabled;
    public CheatSheetWindow? CheatSheet => _cheat;
    public RunLink? Link => _link;
    public string? SmokeBoxText(string variable) => _last?.Boxes.FirstOrDefault(b => b.Variable == variable)?.Text;
    public bool SmokeBoxWrong(string variable) => _last?.Boxes.FirstOrDefault(b => b.Variable == variable)?.Wrong == true;
    public void SmokeType(string variable, string text) { _view.Type(variable, text); _debounce.Stop(); Recalc(); }
    public void SmokeCalculate() => _calc.PerformClick();
    public void SmokeClickApply() => _apply.PerformClick();

    /// <summary>Draws the dialog to a PNG (smoke screenshots).</summary>
    public void SaveScreenshot(string path)
    {
        PerformLayout();
        Recalc();
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: formula screenshot saved to " + path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Unhook();
            _debounce.Dispose();
            _notesTip.Dispose();
            _cheat?.Dispose();
        }
        base.Dispose(disposing);
    }
}
