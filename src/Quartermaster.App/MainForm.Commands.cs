using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>Command registry wiring: the single registration point, effective shortcuts, and the one key-dispatch path (ProcessCmdKey).</summary>
sealed partial class MainForm
{
    readonly CommandRegistry _commands = new();
    readonly Dictionary<string, Func<bool>> _handlers = new();
    readonly Dictionary<string, ToolStripMenuItem> _cmdItems = new();
    Dictionary<Keys, string> _keyMap = new();
    Dictionary<string, string> _shortcutText = new();
    IMessageFilter? _floatKeys;

    public CommandRegistry Commands => _commands;

    /// <summary>Registers every command id with its label and default shortcut. Later features attach handlers with <see cref="SetCommandHandler"/>.</summary>
    void RegisterCommands()
    {
        foreach (var (id, label, key) in new[]
        {
            ("file.openModule", "Open module", "Ctrl+O"), ("file.newGame", "New game", "Ctrl+N"), ("file.openGame", "Open game", "Ctrl+G"),
            ("file.saveGame", "Save game", "Ctrl+S"), ("file.saveGameAs", "Save game as", "Ctrl+Shift+S"),
            ("view.diagnostics", "Diagnostics", "Ctrl+D"), ("view.ledger", "Ledger", "Ctrl+L"), ("view.log", "Log panel", "Ctrl+Shift+L"),
            ("view.hideCounters", "Hide counters", "H"), ("tool.los", "Line of sight", "L"),
            ("tools.workbench", "Rules workbench", "Ctrl+Shift+W"), ("tools.settings", "Settings", "Ctrl+,"),
            ("dice.1d6", "Roll 1d6", "F5"), ("dice.2d6", "Roll 2d6", "F6"), ("turn.next", "Next turn", "Ctrl+Right"),
        })
            _commands.Register(new CommandDef(id, label, key));
        // Features that arrive in later tasks: replaced via SetCommandHandler.
        _handlers["tool.los"] = () => false;
        _handlers["view.hideCounters"] = () => false;
    }

    /// <summary>Attaches (or replaces) the handler of a registered command.</summary>
    public void SetCommandHandler(string id, Action handler) => _handlers[id] = () => { handler(); return true; };

    /// <summary>Attaches a handler that reports whether it ran (false lets the key press through).</summary>
    public void SetCommandHandler(string id, Func<bool> handler) => _handlers[id] = handler;

    /// <summary>Text for menu items: the effective shortcut of a command, empty when unbound.</summary>
    string ShortcutText(string id) => _shortcutText.TryGetValue(id, out var s) ? s : "";

    /// <summary>Recomputes the key map and menu captions from the settings overrides.</summary>
    void ApplyShortcuts(Settings s)
    {
        var map = new Dictionary<Keys, string>();
        var text = new Dictionary<string, string>();
        foreach (var d in _commands.All)
        {
            var eff = _commands.EffectiveShortcut(d.Id, s.ShortcutOverrides);
            text[d.Id] = eff;
            if (eff.Length > 0 && !CommandRegistry.Reserved.Contains(eff) && ShortcutParser.TryParse(eff, out var k)) map.TryAdd(k, d.Id);
        }
        _keyMap = map;
        _shortcutText = text;
        foreach (var (id, mi) in _cmdItems) mi.ShortcutKeyDisplayString = ShortcutText(id);
        if (_dice != null) _dice.ShortcutText = ShortcutText;
    }

    /// <summary>
    /// Runs the command bound to the key if the focus allows it (see <see cref="ShortcutGate"/>). <paramref name="root"/> is the window the key went to:
    /// the active MDI child (or this form) for docked windows, or the floating window itself. Returns false when nothing ran, so the key passes through.
    /// </summary>
    internal bool TryDispatchShortcut(Keys keyData, Form root)
    {
        if (!_keyMap.TryGetValue(keyData, out var id)) return false;
        var leaf = FocusedLeaf(root);
        var text = leaf is TextBoxBase or ComboBox or NumericUpDown || leaf is DataGridView { IsCurrentCellInEditMode: true };
        if (!ShortcutGate.ShouldDispatch(_shortcutText.GetValueOrDefault(id, ""), text, leaf is MapCanvas)) return false;
        if (_cmdItems.TryGetValue(id, out var mi))
        {
            if (!mi.Enabled) return false;
            mi.PerformClick(); // the click handler is already guarded
            return true;
        }
        var ran = false;
        if (_handlers.TryGetValue(id, out var h)) Guard(() => ran = h());
        return ran;
    }

    static Control? FocusedLeaf(Form root)
    {
        Control? c = root;
        while (c is ContainerControl cc && cc.ActiveControl is { } next) c = next;
        return c;
    }

    /// <summary>Routes key presses of floating windows (separate top-level forms) to the registry; docked windows go through ProcessCmdKey.</summary>
    sealed class FloatKeyFilter(MainForm main) : IMessageFilter
    {
        const int WmKeyDown = 0x100, WmSysKeyDown = 0x104;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is not (WmKeyDown or WmSysKeyDown) || main.IsDisposed) return false;
            try
            {
                if (Control.FromHandle(m.HWnd)?.FindForm() is not { } root || root == main || !main._wm.IsFloating(root)) return false;
                var keys = (Keys)(int)(long)m.WParam | Control.ModifierKeys;
                if (keys is (Keys.Control | Keys.W) or Keys.F11 or (Keys.Alt | Keys.Enter)) return false; // window handling stays with WindowManager
                return main.TryDispatchShortcut(keys, root);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Program.AppLog("float shortcut: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>Smoke: pops the main map out and sends F5 through the floating window's dispatch path; a dice roll must appear in the log.</summary>
    void SmokeFloatShortcut()
    {
        if (_session?.Game is not { } game || FirstMap() is not { } map) return;
        var wasFloating = _wm.IsFloating(map);
        if (!wasFloating) _wm.PopOut(map);
        var before = game.State.Log.Count;
        var ok = _wm.IsFloating(map) && TryDispatchShortcut(Keys.F5, map) && game.State.Log.Count == before + 1;
        Program.AppLog("smoke: floating-window F5 shortcut " + (ok ? "ok (dice rolled)" : "FAILED"));
        if (!ok) Program.ExitCode = 1;
        if (!wasFloating && !map.IsDisposed) _wm.Dock(map);
    }

    /// <summary>Smoke: checks the registry defaults parse, round-trip and do not collide. Returns a problem text, or null.</summary>
    string? ShortcutSelfCheck()
    {
        if (ShortcutParser.SelfCheck(_commands) is { } p) return p;
        var c = _commands.Conflicts(new Dictionary<string, string>());
        return c.Count == 0 ? null : "default shortcuts conflict: " + string.Join(", ", c.Select(x => x.Key));
    }

    /// <summary>Smoke: defaults parse and round-trip, are conflict-free, and the key map dispatches the workbench shortcut.</summary>
    void SmokeShortcuts()
    {
        var problem = ShortcutSelfCheck();
        if (problem == null && !(ShortcutParser.TryParse("Ctrl+Shift+W", out var k) && _keyMap.GetValueOrDefault(k) == "tools.workbench")) problem = "key map lacks Ctrl+Shift+W";
        if (problem == null && _session != null)
        {
            if (!TryDispatchShortcut(Keys.Control | Keys.Shift | Keys.W, this) || !_benches.ContainsKey("workbench")) problem = "Ctrl+Shift+W did not open the workbench";
        }
        Program.AppLog("smoke: shortcuts " + (problem ?? $"ok ({_commands.All.Count} commands, defaults parse, round-trip, no conflicts)"));
        if (problem != null) Program.ExitCode = 1;
    }
}
