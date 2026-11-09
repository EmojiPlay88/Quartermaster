using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>Dice toolbar buttons and dropdown, Tools → Dice / Turn menu items and the status-bar turn label.</summary>
sealed class DiceTools : IDisposable
{
    bool _disposed;
    readonly Form _owner;
    readonly Func<ModuleSession?> _session;
    readonly Action<Action> _guard;
    readonly ToolStripStatusLabel _message;
    readonly System.Windows.Forms.Timer _clear = new() { Interval = 5000 };
    readonly ToolStripMenuItem _prev = new("&Previous turn");
    string? _shown;

    /// <summary>The shared roll / turn logic (Render); this class only adds the WinForms menus, buttons, dialogs and status text.</summary>
    readonly DiceTurnActions _core;

    /// <summary>Prompt for each die value instead of rolling (Settings: physical dice).</summary>
    public bool PhysicalDice { get => _core.PhysicalDice; set => _core.PhysicalDice = value; }

    /// <summary>Whether a roll's result text is shown in the status bar (Settings).</summary>
    public bool ShowLastRoll { get => _core.ShowLastRoll; set => _core.ShowLastRoll = value; }

    /// <summary>Effective shortcut text of a command id, shown on menu items.</summary>
    public Func<string, string> ShortcutText { get; set; } = _ => "";


    public ToolStripStatusLabel TurnLabel { get; } = new("Turn 1")
    {
        Visible = false, BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched,
    };

    public DiceTools(Form owner, Func<ModuleSession?> session, Action<Action> guard, ToolStrip toolbar,
        ToolStripMenuItem toolsMenu, ToolStripStatusLabel message, Func<string, ToolStripItem, ToolStripItem> gate)
    {
        _owner = owner; _session = session; _guard = guard; _message = message;
        _core = new DiceTurnActions(session, ShowResult) { AskPhysical = AskPhysical };
        _clear.Tick += (_, _) =>
        {
            _clear.Stop();
            if (_disposed || _message.IsDisposed || owner.IsDisposed) return;
            if (_message.Text == _shown) _message.Text = "";
        };

        toolbar.Items.Add(new ToolStripSeparator());
        AddButton(toolbar, "dice.1d6", "1d6", () => RollSpec(DiceTurnActions.OneD6, DiceTurnActions.RollPurpose), gate);
        AddButton(toolbar, "dice.2d6", "2d6", () => RollSpec(DiceTurnActions.TwoD6, DiceTurnActions.RollPurpose), gate);
        var dd = new ToolStripDropDownButton("Dice") { Name = "dice.menu", DisplayStyle = ToolStripItemDisplayStyle.Text };
        dd.DropDownOpening += (_, _) => _guard(() => BuildDiceItems(dd.DropDownItems));
        dd.DropDownItems.Add("(no game)").Enabled = false;
        toolbar.Items.Add(dd);
        gate("dice.menu", dd);
        _items.Add(dd);
        AddButton(toolbar, "turn.next", "Next turn", () => StepTurn(1), gate);

        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        var dice = new ToolStripMenuItem("&Dice");
        dice.DropDownOpening += (_, _) => _guard(() => BuildDiceItems(dice.DropDownItems));
        dice.DropDownItems.Add("(no game)").Enabled = false;
        toolsMenu.DropDownItems.Add(dice);
        gate("tools.dice", dice);
        _items.Add(dice);

        var turn = new ToolStripMenuItem("&Turn");
        var next = new ToolStripMenuItem("&Next turn");
        turn.DropDownOpening += (_, _) => next.ShortcutKeyDisplayString = ShortcutText("turn.next");
        next.Click += (_, _) => _guard(() => StepTurn(1));
        _prev.Click += (_, _) => _guard(() => StepTurn(-1));
        var set = new ToolStripMenuItem("&Set...");
        set.Click += (_, _) => _guard(SetTurnDialog);
        turn.DropDownItems.AddRange(new ToolStripItem[] { next, _prev, set });
        turn.DropDownOpening += (_, _) => _guard(() => _prev.Enabled = _core.CanStepBack);
        toolsMenu.DropDownItems.Add(turn);
        gate("tools.turn", turn);
        _items.Add(turn);
    }

    /// <summary>Every dice and turn item (toolbar and Tools menu): off while the game may not change.</summary>
    readonly List<ToolStripItem> _items = new();

    void AddButton(ToolStrip bar, string id, string text, Action act, Func<string, ToolStripItem, ToolStripItem> gate)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, Name = id };
        b.Click += (_, _) => _guard(act);
        bar.Items.Add(b);
        gate(id, b);
        _items.Add(b);
    }

    /// <summary>Updates the turn label from the current game.</summary>
    public void Dispose()
    {
        _disposed = true;
        _clear.Stop();
        _clear.Dispose();
    }

    public void Refresh() => _guard(RefreshCore);

    void RefreshCore()
    {
        if (_disposed) return;
        var game = _session()?.Game;
        var blocked = DiceTurnActions.BlockedText(_session());
        foreach (var i in _items)
        {
            if (blocked != null) { i.Enabled = false; i.ToolTipText = blocked; }
            else if (i.ToolTipText is ModuleSession.ReplayingText or ModuleSession.ViewerCopyText) i.ToolTipText = null;
        }
        TurnLabel.Visible = game != null;
        if (game == null) return;
        TurnLabel.Text = TurnText(game.State.Turn);
    }

    public static string TurnText(TurnState t) => DiceTurnActions.TurnText(t);

    // ---------- dice ----------

    void BuildDiceItems(ToolStripItemCollection into)
    {
        into.Clear();
        var s = _session();
        if (s == null) return;
        Add(into, "&1d6", () => RollSpec(DiceTurnActions.OneD6, DiceTurnActions.RollPurpose)).ShortcutKeyDisplayString = ShortcutText("dice.1d6");
        Add(into, "&2d6", () => RollSpec(DiceTurnActions.TwoD6, DiceTurnActions.RollPurpose)).ShortcutKeyDisplayString = ShortcutText("dice.2d6");
        if (s.Model.Dice.Count > 0)
        {
            into.Add(new ToolStripSeparator());
            foreach (var die in s.Model.Dice)
            {
                var d = die;
                var mi = Add(into, d.Name.Replace("&", "&&"), () => RollSpecial(d));
                if (d.Faces.Count < 2) { mi.Enabled = false; mi.ToolTipText = DiceTurnActions.NeedsFacesText; }
            }
        }
        into.Add(new ToolStripSeparator());
        Add(into, "&Custom...", CustomRoll);
        Add(into, "&Multi-roll...", MultiRoll);
    }

    ToolStripMenuItem Add(ToolStripItemCollection into, string text, Action act)
    {
        var mi = new ToolStripMenuItem(text);
        mi.Click += (_, _) => _guard(act);
        into.Add(mi);
        return mi;
    }

    void CustomRoll()
    {
        if (DiceDialog.Ask(_owner, "Custom Roll", false) is { } r) RollSpec(r.Spec, DiceTurnActions.RollPurpose);
    }

    void MultiRoll()
    {
        if (DiceDialog.Ask(_owner, "Multi-roll", true) is { } r) _core.MultiRoll(r.Spec, r.Times);
    }

    /// <summary>Rolls (or asks for physical results) and records one RollDice command. Returns false when cancelled or impossible.</summary>
    public bool RollSpec(DiceSpec spec, string purpose, bool quiet = false) => _core.RollSpec(spec, purpose, quiet);

    public bool RollSpecial(DieDef die) => _core.RollSpecial(die);

    /// <summary>The main window's action port; null = execute directly.</summary>
    public Func<IActionPort?>? Actions { get => _core.Actions; set => _core.Actions = value; }

    IReadOnlyList<int>? AskPhysical(int n, int sides)
    {
        var list = new List<int>();
        for (var i = 1; i <= n; i++)
        {
            while (true)
            {
                var text = InputDialog.Ask(_owner, "Physical dice", $"Value of die {i} of {n} (1–{sides}):");
                if (text == null) return null;
                if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1 && v <= sides) { list.Add(v); break; }
                MessageBox.Show(_owner, $"Enter a whole number from 1 to {sides}.", "Physical dice", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        return list;
    }

    void ShowResult(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_disposed) return;
        _shown = text;
        _message.Text = text;
        _clear.Stop();
        _clear.Start();
    }

    // ---------- turn ----------

    public void StepTurn(int delta) => _core.StepTurn(delta);

    void SetTurnDialog()
    {
        if (!_core.CanSetTurn()) return;
        if (TurnDialog.Ask(_owner, _core.CurrentTurn) is not { } r) return;
        _core.SetTurn(r.Turn, r.Phase);
    }
}

/// <summary>Dice expression prompt with live validation; optionally also asks how many times to roll.</summary>
sealed class DiceDialog : Form
{
    readonly TextBox _expr = new() { Left = 12, Top = 30, Width = 200, Text = "2d6" };
    readonly NumericUpDown _times = new() { Left = 232, Top = 30, Width = 60, Minimum = 1, Maximum = 20, Value = 3 };
    readonly Label _error = new() { Left = 12, Top = 58, Width = 290, Height = 18, ForeColor = Color.Firebrick };
    readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK, Left = 146, Top = 86, Width = 75 };

    DiceDialog(string title, bool multi)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(314, 120);
        Controls.Add(new Label { Text = "Dice (e.g. 2d6 or 3d10+2):", Left = 12, Top = 10, Width = 200 });
        Controls.Add(_expr);
        if (multi)
        {
            Controls.Add(new Label { Text = "Times:", Left = 232, Top = 10, Width = 60 });
            Controls.Add(_times);
        }
        Controls.Add(_error);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 227, Top = 86, Width = 75 };
        Controls.Add(_ok);
        Controls.Add(cancel);
        AcceptButton = _ok;
        CancelButton = cancel;
        _expr.TextChanged += (_, _) => Check();
        Check();
    }

    void Check()
    {
        try
        {
            var (spec, err) = DiceExpr.Parse(_expr.Text);
            _error.Text = err ?? "";
            _ok.Enabled = spec != null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _error.Text = DiceExpr.FormatError;
            _ok.Enabled = false;
        }
    }

    public static (DiceSpec Spec, int Times)? Ask(IWin32Window owner, string title, bool multi)
    {
        using var d = new DiceDialog(title, multi);
        if (d.ShowDialog(owner) != DialogResult.OK) return null;
        var (spec, _) = DiceExpr.Parse(d._expr.Text);
        return spec == null ? null : (spec, multi ? (int)d._times.Value : 1);
    }
}

/// <summary>Set turn number (at least 1) and an optional phase.</summary>
sealed class TurnDialog : Form
{
    readonly NumericUpDown _turn = new() { Left = 70, Top = 12, Width = 80, Minimum = 1, Maximum = 99999 };
    readonly TextBox _phase = new() { Left = 70, Top = 42, Width = 220 };

    TurnDialog(TurnState cur)
    {
        Text = "Set Turn";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(304, 112);
        _turn.Value = Math.Clamp(cur.Turn, 1, 99999);
        _phase.Text = cur.Phase ?? "";
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 130, Top = 76, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 212, Top = 76, Width = 75 };
        Controls.AddRange(new Control[]
        {
            new Label { Text = "Turn:", Left = 12, Top = 15, Width = 55 }, _turn,
            new Label { Text = "Phase:", Left = 12, Top = 45, Width = 55 }, _phase, ok, cancel,
        });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public static (int Turn, string Phase)? Ask(IWin32Window owner, TurnState cur)
    {
        using var d = new TurnDialog(cur);
        return d.ShowDialog(owner) == DialogResult.OK ? ((int)d._turn.Value, d._phase.Text) : null;
    }
}
