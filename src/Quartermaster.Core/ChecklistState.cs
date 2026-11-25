using Quartermaster.Rules;

namespace Quartermaster.Core;

/// <summary>Whether a checklist item is ticked: an item with an id by its id (<see cref="GameState.TickedIds"/>), else by its index
/// (<see cref="GameState.Ticked"/>). So reordering items keeps id-ticks on their items, while index-ticks stay on their positions.</summary>
public static class ChecklistState
{
    public static bool IsTicked(GameState s, ChecklistItem? item, int index) =>
        item?.Id is { Length: > 0 } id
            ? s.TickedIds?.Contains(id, StringComparer.Ordinal) == true
            : s.Ticked?.Contains(index) == true;

    /// <summary>The indexes of the ticked items of a checklist, in order (for the phase panels).</summary>
    public static IReadOnlyList<int> TickedIndexes(GameState? s, IReadOnlyList<ChecklistItem?>? items)
    {
        var res = new List<int>();
        if (s == null || items == null) return res;
        for (var i = 0; i < items.Count; i++)
            if (IsTicked(s, items[i], i)) res.Add(i);
        return res;
    }
}
