namespace Quartermaster.Render;

/// <summary>The Keyboard settings page without UI: every command with its pending shortcut. Edits are held until collected; conflicts block OK/Apply.</summary>
public sealed class KeyboardEditModel(CommandRegistry registry)
{
    readonly Dictionary<string, string> _eff = new();

    public IReadOnlyList<CommandDef> Commands => registry.All;

    static string Normal(string s) => CommandRegistry.TryNormalize(s, out var n) ? n : "";

    /// <summary>Starts from the shortcuts in effect for these settings.</summary>
    public void Load(Settings s)
    {
        _eff.Clear();
        foreach (var d in registry.All) _eff[d.Id] = registry.EffectiveShortcut(d.Id, s.ShortcutOverrides);
    }

    /// <summary>The pending shortcut of a command; "" = none (or unknown id).</summary>
    public string Effective(string id) => _eff.GetValueOrDefault(id, "");

    /// <summary>Sets a command's shortcut ("" clears it).</summary>
    public void Assign(string id, string shortcut) => _eff[id] = shortcut.Length == 0 ? "" : CommandRegistry.TryNormalize(shortcut, out var n) ? n : shortcut;

    public void Reset(string id)
    {
        if (registry.Find(id) is { } d) _eff[id] = Normal(d.DefaultShortcut);
    }

    public void ResetAll()
    {
        foreach (var d in registry.All) _eff[d.Id] = Normal(d.DefaultShortcut);
    }

    /// <summary>Ids that share a key with another command, or hold a reserved key.</summary>
    public IReadOnlySet<string> Flagged() => registry.Conflicts(_eff).SelectMany(c => c.Ids).ToHashSet();

    /// <summary>Conflict text joined with "; ", or null when there is none.</summary>
    public string? Problem()
    {
        var cs = registry.Conflicts(_eff);
        if (cs.Count == 0) return null;
        string Names(IReadOnlyList<string> ids) => string.Join(", ", ids.Select(i => registry.Find(i)?.Label ?? i));
        return string.Join("; ", cs.Select(c => CommandRegistry.IsReserved(c.Key)
            ? $"{c.Key} is reserved by the window ({Names(c.Ids)})"
            : $"{c.Key} is used by {Names(c.Ids)}"));
    }

    /// <summary>The refusal text shown by OK/Apply, or null when the shortcuts are fine.</summary>
    public string? ValidateText() => Problem() is { } p ? "These shortcuts need fixing before the settings can be applied:\n" + p.Replace("; ", "\n") : null;

    /// <summary>Writes overrides only for commands that differ from their default; overrides of unknown ids are kept.</summary>
    public Settings Collect(Settings s)
    {
        var map = s.ShortcutOverrides.Where(kv => registry.Find(kv.Key) == null).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var d in registry.All)
            if (_eff.TryGetValue(d.Id, out var e) && e != Normal(d.DefaultShortcut)) map[d.Id] = e;
        return s with { ShortcutOverrides = map };
    }
}
