using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Rules values for display (Outliner, inventory, counter popups): <see cref="PropertyValues.Get"/> that never throws.</summary>
public static class DisplayValues
{
    /// <summary>
    /// The value at <paramref name="key"/> as the rules read it (a computed property evaluated now); null when there is none. A failed read is
    /// no value, with its message in <paramref name="error"/> (null otherwise).
    /// </summary>
    public static double? Get(GameState s, RulesPack pack, ModuleModel? model, string key, PropertyDef def, out string? error)
    {
        error = null;
        try { return PropertyValues.Get(s, pack, model, key, def); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>As <see cref="Get(GameState, RulesPack, ModuleModel?, string, PropertyDef, out string?)"/> without the error message.</summary>
    public static double? Get(GameState s, RulesPack pack, ModuleModel? model, string key, PropertyDef def) => Get(s, pack, model, key, def, out _);
}
