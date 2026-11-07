namespace Quartermaster.Render;

/// <summary>Every command id with its label and default shortcut (empty = unbound). The app registers these once.</summary>
public static class CommandDefaults
{
    public static IReadOnlyList<CommandDef> All { get; } = new[]
    {
        ("file.openModule", "Open module", "Ctrl+O"), ("file.newGame", "New game", "Ctrl+N"), ("file.openGame", "Open game", "Ctrl+G"),
        ("file.saveGame", "Save game", "Ctrl+S"), ("file.saveGameAs", "Save game as", "Ctrl+Shift+S"),
        ("view.diagnostics", "Diagnostics", "Ctrl+D"), ("view.ledger", "Ledger", "Ctrl+L"), ("view.log", "Log panel", "Ctrl+Shift+L"),
        ("view.hideCounters", "Hide counters", "H"), ("tool.los", "Line of sight", "L"),
        ("tools.workbench", "Rules workbench", "Ctrl+Shift+W"), ("tools.settings", "Settings", "Ctrl+,"),
        ("dice.1d6", "Roll 1d6", "F5"), ("dice.2d6", "Roll 2d6", "F6"), ("turn.next", "Next turn", "Ctrl+Right"),
        ("edit.undo", "Undo", "Ctrl+Z"), ("edit.redo", "Redo", "Ctrl+Y"), ("edit.redoAlt", "Redo (alternative)", "Ctrl+Shift+Z"),
        ("phase.stepBack", "Step back to start of phase", "Ctrl+Left"), ("phase.stepBackTo", "Step back to...", ""),
    }.Select(t => new CommandDef(t.Item1, t.Item2, t.Item3)).ToList();
}
