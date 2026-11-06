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

    /// <summary>Prompt for each die value instead of rolling (Settings: physical dice).</summary>
    public bool PhysicalDice { get; set; }

    /// <summary>Whether a roll's result text is shown in the status bar (Settings).</summary>
    public bool ShowLastRoll { get; set; } = true;

    public ToolStripStatusLabel TurnLabel { get; } = new("Turn 1")
    {
        Visible = false, BorderSides = ToolStripStatusLabelBorderSides.Left, BorderStyle = Border3DStyle.Etched,
    };

    public DiceTools(Form owner, Func<ModuleSession?> session, Action<Action> guard, ToolStrip toolbar,
        ToolStripMenuItem toolsMenu, ToolStripStatusLabel message, List<ToolStripItem> needsGame)
    {
        _owner = owner; _session = session; _guard = guard; _message = message;
        _clear.Tick += (_, _) =>
        {
            _clear.Stop();
            if (_disposed || _message.IsDisposed || owner.IsDisposed) return;
            if (_message.Text == _shown) _message.Text = "";
        };

        toolbar.Items.Add(new ToolStripSeparator());
        AddButton(toolbar, "1d6", () => RollSpec(new DiceSpec(1, 6, 0), "Roll"), needsGame);
        AddButton(toolbar, "2d6", () => RollSpec(new DiceSpec(2, 6, 0), "Roll"), needsGame);
        var dd = new ToolStripDropDownButton("Dice") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        dd.DropDownOpening += (_, _) => _guard(() => BuildDiceItems(dd.DropDownItems));
        dd.DropDownItems.Add("(no game)").Enabled = false;
        toolbar.Items.Add(dd);
        needsGame.Add(dd);

        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        var dice = new ToolStripMenuItem("&Dice");
        dice.DropDownOpening += (_, _) => _guard(() => BuildDiceItems(dice.DropDownItems));
        dice.DropDownItems.Add("(no game)").Enabled = false;
        toolsMenu.DropDownItems.Add(dice);
        needsGame.Add(dice);

        var turn = new ToolStripMenuItem("&Turn");
        var next = new ToolStripMenuItem("&Next turn");
        next.Click += (_, _) => _guard(() => StepTurn(1));
        _prev.Click += (_, _) => _guard(() => StepTurn(-1));
        var set = new ToolStripMenuItem("&Set...");
        set.Click += (_, _) => _guard(SetTurnDialog);
        turn.DropDownItems.AddRange(new ToolStripItem[] { next, _prev, set });
        turn.DropDownOpening += (_, _) => _guard(() => _prev.Enabled = CurrentTurn.Turn > 1);
        toolsMenu.DropDownItems.Add(turn);
        needsGame.Add(turn);
    }

    void AddButton(ToolStrip bar, string text, Action act, List<ToolStripItem> needsGame)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        b.Click += (_, _) => _guard(act);
        bar.Items.Add(b);
        needsGame.Add(b);
    }

    TurnState CurrentTurn => _session()?.Game?.State.Turn ?? new TurnState(1, null);

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
        TurnLabel.Visible = game != null;
        if (game == null) return;
        TurnLabel.Text = TurnText(game.State.Turn);
    }

    public static string TurnText(TurnState t) =>
        "Turn " + t.Turn.ToString(CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(t.Phase) ? "" : " · " + t.Phase);

    // ---------- dice ----------

    void BuildDiceItems(ToolStripItemCollection into)
    {
        into.Clear();
        var s = _session();
        if (s == null) return;
        Add(into, "&1d6", () => RollSpec(new DiceSpec(1, 6, 0), "Roll"));
        Add(into, "&2d6", () => RollSpec(new DiceSpec(2, 6, 0), "Roll"));
        if (s.Model.Dice.Count > 0)
        {
            into.Add(new ToolStripSeparator());
            foreach (var die in s.Model.Dice)
            {
                var d = die;
                var mi = Add(into, d.Name.Replace("&", "&&"), () => RollSpecial(d));
                if (d.Faces.Count < 2) { mi.Enabled = false; mi.ToolTipText = "This die needs at least 2 faces"; }
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
        if (DiceDialog.Ask(_owner, "Custom Roll", false) is { } r) RollSpec(r.Spec, "Roll");
    }

    void MultiRoll()
    {
        if (DiceDialog.Ask(_owner, "Multi-roll", true) is not { } r) return;
        for (var k = 1; k <= r.Times; k++)
            if (!RollSpec(r.Spec, $"Multi-roll {k}/{r.Times}", quiet: k < r.Times)) break;
    }

    /// <summary>Rolls (or asks for physical results) and records one RollDice command. Returns false when cancelled or impossible.</summary>
    public bool RollSpec(DiceSpec spec, string purpose, bool quiet = false)
    {
        if (_session() is not { Game: { } game } s) return false;
        var results = Results(s, spec.Count, spec.Sides);
        if (results == null) return false;
        game.Execute(CommandLog.Stamp(new RollDice(purpose, spec.Count, spec.Sides, results, DiceExpr.Format(spec), null, spec.Modifier)));
        if (!quiet && ShowLastRoll) ShowResult(game.State.Log.LastOrDefault()?.Text);
        return true;
    }

    public bool RollSpecial(DieDef die)
    {
        if (_session() is not { Game: { } game } s || die.Faces.Count < 2) return false;
        var results = Results(s, 1, die.Faces.Count);
        if (results == null) return false;
        var face = die.Faces[results[0] - 1];
        game.Execute(CommandLog.Stamp(new RollDice(die.Name, 1, die.Faces.Count, results, null, new[] { face })));
        if (ShowLastRoll) ShowResult(game.State.Log.LastOrDefault()?.Text);
        return true;
    }

    IReadOnlyList<int>? Results(ModuleSession s, int n, int sides)
    {
        if (PhysicalDice) return AskPhysical(n, sides);
        var r = s.Roll(n, sides);
        if (r.Count != n || r.Any(v => v < 1 || v > sides))
        {
            ShowResult("The dice roller returned invalid results; nothing was recorded.");
            return null;
        }
        return r;
    }

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

    public void StepTurn(int delta)
    {
        if (_session()?.Game is not { } game) return;
        var cur = game.State.Turn;
        var n = Math.Max(1, cur.Turn + delta);
        if (n == cur.Turn) return;
        game.Execute(CommandLog.Stamp(new SetTurn(n, null)));
    }

    void SetTurnDialog()
    {
        if (_session()?.Game is not { } game) return;
        if (TurnDialog.Ask(_owner, game.State.Turn) is not { } r) return;
        game.Execute(CommandLog.Stamp(new SetTurn(r.Turn, string.IsNullOrWhiteSpace(r.Phase) ? null : r.Phase.Trim())));
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
