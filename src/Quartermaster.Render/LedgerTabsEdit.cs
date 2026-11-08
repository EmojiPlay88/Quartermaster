using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>Validation behind the Ledger's Edit tabs dialog: what stops the personal tab list from being saved.</summary>
public static class LedgerTabsEdit
{
    /// <summary>One line per problem (blank name, duplicate name ignoring case, filter that does not parse); empty when the list can be saved.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<UserLedgerTab> tabs)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var t in tabs ?? Array.Empty<UserLedgerTab>())
        {
            i++;
            var name = t?.Name?.Trim() ?? "";
            if (name.Length == 0) problems.Add($"Tab {i} needs a name.");
            else if (!seen.Add(name) && reported.Add(name)) problems.Add($"More than one tab is named \"{name}\"; tab names must be unique.");
            if (LedgerTabs.FilterError(t?.Filter) is { } err)
                problems.Add($"Tab \"{(name.Length == 0 ? i.ToString() : name)}\": the unit filter does not parse: {err}");
        }
        return problems;
    }
}
