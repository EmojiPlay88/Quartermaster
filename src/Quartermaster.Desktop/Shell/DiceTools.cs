using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Tools → Dice and Tools → Turn, as WinForms DiceTools: 1d6 / 2d6 (F5 / F6 shown), the module's dice, Custom... and Multi-roll...;
/// Next turn / Previous turn / Set.... The rolling and turn logic is <see cref="DiceTurnActions"/> (Render, shared with WinForms); this
/// class builds the menus, asks through <see cref="IDialogs"/> and shows a roll's log line in the status bar for five seconds.
/// While the game may not change (replay, viewer copy) every dice and turn item it tracks is off with the reason as its tooltip.
/// </summary>
public sealed class DiceTools
{
    /// <summary>The placeholder of the Dice menu without a game (WinForms).</summary>
    public const string NoGameText = "(no game)";

    readonly Window _owner;
    readonly Func<ModuleSession?> _session;
    readonly IDialogs _dialogs;
    readonly CommandRouter _router;
    readonly Action<Action> _guard;
    readonly Action<Func<Task>> _runAsync;
    readonly Action<string> _setStatus;
    readonly Func<string> _status;
    readonly DispatcherTimer _clear = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly List<Control> _items = new();
    readonly MenuItem _next, _prev, _set;
    string? _shown;
    /// <summary>The session the Dice menu items were last built for.</summary>
    ModuleSession? _builtFor;

    public DiceTools(Window owner, Func<ModuleSession?> session, Func<IActionPort?> actions, IDialogs dialogs, CommandRouter router,
        Action<Action> guard, Action<Func<Task>> runAsync, Action<string> setStatus, Func<string> status)
    {
        _owner = owner; _session = session; _dialogs = dialogs; _router = router;
        _guard = guard; _runAsync = runAsync; _setStatus = setStatus; _status = status;
        Core = new DiceTurnActions(session, ShowResult) { Actions = actions };
        _clear.Tick += (_, _) =>
        {
            _clear.Stop();
            if (_status() == _shown) _setStatus("");
        };

        DiceMenu = new MenuItem { Header = MenuText.FromWinForms("&Dice") };
        RebuildDiceMenu();
        // As WinForms DropDownOpening: the items are rebuilt when the menu opens, never under a click (a roll changes the game, and a
        // rebuild from the game-change refresh cleared the items inside the clicked item's own handler, so the menu did not close).
        DiceMenu.SubmenuOpened += (_, e) =>
        {
            if (ReferenceEquals(e.Source, DiceMenu)) _guard(RebuildDiceMenu);
        };
        Track(DiceMenu);

        TurnMenu = new MenuItem { Header = MenuText.FromWinForms("&Turn") };
        _next = Item("&Next turn", () => Core.StepTurn(1));
        _prev = Item("&Previous turn", () => Core.StepTurn(-1));
        _set = Item("&Set...", () => _runAsync(SetTurnAsync));
        TurnMenu.Items.Add(_next);
        TurnMenu.Items.Add(_prev);
        TurnMenu.Items.Add(_set);
        TurnMenu.SubmenuOpened += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, TurnMenu)) return;
            _next.InputGesture = _router.Gesture("turn.next");
            _guard(() => _prev.IsEnabled = Core.CanStepBack);
        };
        _next.InputGesture = _router.Gesture("turn.next");
        Track(TurnMenu);
    }

    /// <summary>The shared roll and turn logic.</summary>
    public DiceTurnActions Core { get; }

    /// <summary>Tools → Dice (gate tools.dice).</summary>
    public MenuItem DiceMenu { get; }

    /// <summary>Tools → Turn (gate tools.turn).</summary>
    public MenuItem TurnMenu { get; }

    /// <summary>Adds a control (a toolbar button) that goes off while the game may not change.</summary>
    public void Track(Control c) => _items.Add(c);

    /// <summary>
    /// As WinForms DiceTools.Refresh, after the gates were applied: blocked items go off with the reason; the reason tooltip is cleared once
    /// the block is over (<paramref name="baseTip"/> gives an item's own tooltip back), and Previous turn. Cheap: the Dice menu is rebuilt only
    /// when the session (module) changed; otherwise it is rebuilt when it opens.
    /// </summary>
    public void Refresh(Func<Control, object?> baseTip)
    {
        var blocked = DiceTurnActions.BlockedText(_session());
        foreach (var i in _items)
        {
            if (blocked != null)
            {
                i.IsEnabled = false;
                ToolTip.SetTip(i, blocked);
                ToolTip.SetShowOnDisabled(i, true);
            }
            else if (ToolTip.GetTip(i) is ModuleSession.ReplayingText or ModuleSession.ViewerCopyText) ToolTip.SetTip(i, baseTip(i));
        }
        if (!ReferenceEquals(_builtFor, _session())) RebuildDiceMenu();
        _prev.IsEnabled = Core.CanStepBack;
    }

    // ---------- dice ----------

    /// <summary>Rolls 1d6 or 2d6 ("Roll"), as the toolbar buttons, the menu items and F5 / F6.</summary>
    public bool Roll(DiceSpec spec)
    {
        if (!PhysicalDice) return Core.RollSpec(spec, DiceTurnActions.RollPurpose);
        if (_session()?.Game == null) return false;
        _runAsync(() => RollPhysicalAsync(new[] { (spec.Count, spec.Sides) }, () => Core.RollSpec(spec, DiceTurnActions.RollPurpose)));
        return true;
    }

    /// <summary>True while rolls ask for the dice values instead of rolling (Settings, Tools, Physical dice).</summary>
    public bool PhysicalDice { get => Core.PhysicalDice; set => Core.PhysicalDice = value; }

    /// <summary>
    /// WinForms AskPhysical: asks for each of <paramref name="n"/> dice in turn ("Value of die i of n (1-sides):"); a wrong entry says why and
    /// asks again, Cancel returns null (nothing is recorded).
    /// </summary>
    public async Task<IReadOnlyList<int>?> AskPhysicalAsync(int n, int sides)
    {
        var list = new List<int>();
        for (var i = 1; i <= n; i++)
        {
            while (true)
            {
                var text = await _dialogs.AskTextAsync(_owner, "Physical dice", $"Value of die {i} of {n} (1–{sides}):", "");
                if (text == null) return null;
                if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1 && v <= sides) { list.Add(v); break; }
                await _dialogs.ShowInfoAsync(_owner, $"Enter a whole number from 1 to {sides}.", "Physical dice");
            }
        }
        return list;
    }

    /// <summary>
    /// A dialog cannot block the shared roll code, so every value is asked first (one entry of <paramref name="asks"/> per roll); then the
    /// roll runs with a one-shot provider that hands the answers out. A cancelled question, a changed module or a refused game records nothing.
    /// </summary>
    async Task RollPhysicalAsync(IReadOnlyList<(int N, int Sides)> asks, Action roll)
    {
        var s = _session();
        var game = s?.Game;
        if (Core.Refused()) return;
        var answers = new Queue<IReadOnlyList<int>>();
        foreach (var (n, sides) in asks)
        {
            if (await AskPhysicalAsync(n, sides) is not { } r) return;
            answers.Enqueue(r);
        }
        if (!ReferenceEquals(s, _session()) || game == null || !ReferenceEquals(game, s!.Game)) return;   // module or game replaced while asking
        Core.AskPhysical = (_, _) => answers.Count > 0 ? answers.Dequeue() : null;
        try { roll(); }
        finally { Core.AskPhysical = null; }
    }

    /// <summary>Rolls a module die, asking for its face when physical dice are on.</summary>
    bool RollDie(DieDef die)
    {
        if (!PhysicalDice || die.Faces.Count < 2) return Core.RollSpecial(die);
        _runAsync(() => RollPhysicalAsync(new[] { (1, die.Faces.Count) }, () => Core.RollSpecial(die)));
        return true;
    }

    void RebuildDiceMenu()
    {
        _builtFor = _session();
        var items = BuildDiceItems().ToList();
        if (items.Count == 0) items.Add(new MenuItem { Header = NoGameText, IsEnabled = false });
        // New items first, then the old ones go: the menu never has zero items, so an open submenu stays open.
        var old = DiceMenu.Items.Count;
        foreach (var c in items) DiceMenu.Items.Add(c);
        for (var i = 0; i < old; i++) DiceMenu.Items.RemoveAt(0);
    }

    /// <summary>
    /// The Dice items as WinForms BuildDiceItems (new controls each time, for the menu and the toolbar drop-down): &amp;1d6, &amp;2d6, the module's
    /// dice (one with fewer than 2 faces is off with <see cref="DiceTurnActions.NeedsFacesText"/>), &amp;Custom..., &amp;Multi-roll...; nothing without a module.
    /// </summary>
    public IReadOnlyList<Control> BuildDiceItems()
    {
        var list = new List<Control>();
        if (_session() is not { } s) return list;
        var d1 = Item("&1d6", () => Roll(DiceTurnActions.OneD6));
        d1.InputGesture = _router.Gesture("dice.1d6");
        var d2 = Item("&2d6", () => Roll(DiceTurnActions.TwoD6));
        d2.InputGesture = _router.Gesture("dice.2d6");
        list.Add(d1);
        list.Add(d2);
        if (s.Model.Dice.Count > 0)
        {
            list.Add(new Separator());
            foreach (var die in s.Model.Dice)
            {
                var d = die;
                var mi = Item(d.Name.Replace("&", "&&", StringComparison.Ordinal), () => RollDie(d));
                if (d.Faces.Count < 2)
                {
                    mi.IsEnabled = false;
                    ToolTip.SetTip(mi, DiceTurnActions.NeedsFacesText);
                    ToolTip.SetShowOnDisabled(mi, true);
                }
                list.Add(mi);
            }
        }
        list.Add(new Separator());
        list.Add(Item("&Custom...", () => _runAsync(CustomRollAsync)));
        list.Add(Item("&Multi-roll...", () => _runAsync(MultiRollAsync)));
        return list;
    }

    MenuItem Item(string winFormsLabel, Action act)
    {
        var mi = new MenuItem { Header = MenuText.FromWinForms(winFormsLabel) };
        mi.Click += (_, _) => _guard(act);
        return mi;
    }

    async Task CustomRollAsync()
    {
        if (await _dialogs.AskDiceAsync(_owner, "Custom Roll", false) is not { } r) return;
        if (PhysicalDice) await RollPhysicalAsync(new[] { (r.Spec.Count, r.Spec.Sides) }, () => Core.RollSpec(r.Spec, DiceTurnActions.RollPurpose));
        else Core.RollSpec(r.Spec, DiceTurnActions.RollPurpose);
    }

    async Task MultiRollAsync()
    {
        if (await _dialogs.AskDiceAsync(_owner, "Multi-roll", true) is not { } r) return;
        if (PhysicalDice) await RollPhysicalAsync(Enumerable.Repeat((r.Spec.Count, r.Spec.Sides), r.Times).ToList(), () => Core.MultiRoll(r.Spec, r.Times));
        else Core.MultiRoll(r.Spec, r.Times);
    }

    void ShowResult(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _shown = text;
        _setStatus(text);
        _clear.Stop();
        _clear.Start();
    }

    // ---------- turn ----------

    async Task SetTurnAsync()
    {
        if (!Core.CanSetTurn()) return;
        var s = _session();
        if (await _dialogs.AskTurnAsync(_owner, Core.CurrentTurn) is not { } r || !ReferenceEquals(s, _session())) return;
        Core.SetTurn(r.Turn, r.Phase);
    }

    /// <summary>Stops the status timer (the window closed).</summary>
    public void Stop() => _clear.Stop();
}
