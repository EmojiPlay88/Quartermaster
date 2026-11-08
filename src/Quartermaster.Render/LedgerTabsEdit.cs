using Quartermaster.Core;

namespace Quartermaster.Render;

/// <summary>Validation behind the Ledger's Edit tabs dialog: what stops the personal tab list from being saved.</summary>
public static class LedgerTabsEdit
{
    /// <summary>
    /// One line per problem (blank name, duplicate name ignoring case, filter that does not parse or, with <paramref name="pack"/>, names a
    /// property the rules pack does not have); empty when the list can be saved.
    /// </summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<UserLedgerTab> tabs, Quartermaster.Rules.RulesPack? pack = null)
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
            else if (pack != null && LedgerTabs.FilterError(t?.Filter, pack) is { } perr)
                problems.Add($"Tab \"{(name.Length == 0 ? i.ToString() : name)}\": the unit filter does not work with this rules pack: {perr}");
        }
        return problems;
    }
}
