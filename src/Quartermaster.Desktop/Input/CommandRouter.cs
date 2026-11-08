using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Quartermaster.Desktop.Map;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Input;

/// <summary>
/// The one key-dispatch path for commands, as WinForms MainForm.Commands: every <see cref="CommandDefaults"/> id with its effective
/// shortcut (defaults, or the settings overrides), handlers attached per id, and a KeyDown handler on a window that runs the command bound
/// to the pressed gesture. Shortcuts are parsed with <see cref="ShortcutGestures"/> at key time, so Ctrl becomes Cmd on macOS.
/// Menus can show <see cref="ShortcutText"/> and bind <see cref="Gesture"/>.
/// </summary>
public sealed class CommandRouter
{
    readonly CommandRegistry _registry = new();
    readonly Dictionary<string, Func<bool>> _handlers = new(StringComparer.Ordinal);
    IReadOnlyDictionary<string, string> _overrides = new Dictionary<string, string>();

    public CommandRouter()
    {
        foreach (var d in CommandDefaults.All) _registry.Register(d);
    }

    public CommandRegistry Registry => _registry;

    /// <summary>Runs each command (key or <see cref="Execute"/>); the main window points it at its error guard. Null = run directly.</summary>
    public Action<Action>? Guard { get; set; }

    /// <summary>The settings' shortcut overrides (id → shortcut, "" = unbound).</summary>
    public void SetOverrides(IReadOnlyDictionary<string, string> overrides) => _overrides = overrides ?? new Dictionary<string, string>();

    /// <summary>Attaches (or replaces) the handler of a command.</summary>
    public void SetHandler(string id, Action handler) => _handlers[id] = () => { handler(); return true; };

    /// <summary>Attaches a handler that reports whether it ran (false lets the key press through, e.g. a disabled command).</summary>
    public void SetHandler(string id, Func<bool> handler) => _handlers[id] = handler;

    /// <summary>The effective shortcut in canonical form ("Ctrl+Z"), "" when unbound.</summary>
    public string Shortcut(string id) => _registry.EffectiveShortcut(id, _overrides);

    /// <summary>The gesture for menus and key matching (Cmd on macOS); null when unbound or reserved.</summary>
    public KeyGesture? Gesture(string id) =>
        Shortcut(id) is { Length: > 0 } s && !CommandRegistry.IsReserved(s) ? ShortcutGestures.Parse(s) : null;

    /// <summary>Menu text for the shortcut ("Ctrl+Z", "⌘Z" on macOS), "" when unbound.</summary>
    public string ShortcutText(string id) => Shortcut(id) is { Length: > 0 } s ? ShortcutGestures.Display(s) : "";

    /// <summary>Runs the command's handler (through <see cref="Guard"/>). False when it has none or it declined.</summary>
    public bool Execute(string id)
    {
        if (!_handlers.TryGetValue(id, out var h)) return false;
        var ran = false;
        if (Guard != null) Guard(() => ran = h()); else ran = h();
        return ran;
    }

    /// <summary>The command bound to a key press, or null.</summary>
    public string? Find(Key key, KeyModifiers modifiers)
    {
        foreach (var d in _registry.All)
            if (Gesture(d.Id) is { } g && g.Key == key && g.KeyModifiers == modifiers) return d.Id;
        return null;
    }

    /// <summary>Runs the command bound to the key unless the focus forbids it (text boxes keep their editing keys; single-letter
    /// commands only fire on the map, as <see cref="ShortcutGate"/>). True when a command ran.</summary>
    public bool TryDispatch(KeyEventArgs e)
    {
        if (Find(e.Key, e.KeyModifiers) is not { } id) return false;
        var text = e.Source is TextBox;
        var map = e.Source is MapView;
        if (!ShortcutGate.ShouldDispatch(Shortcut(id), text, map)) return false;
        return Execute(id);
    }

    /// <summary>Handles key presses that reach <paramref name="root"/> (bubbled from the focused control) and were not handled on the way.</summary>
    public void Attach(InputElement root) => root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);

    public void Detach(InputElement root) => root.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (TryDispatch(e)) e.Handled = true;
    }
}
