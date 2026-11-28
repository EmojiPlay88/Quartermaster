using Quartermaster.Rules;

namespace Quartermaster.Core;

/// <summary>One candidate of a holder picker: the holder it stands for and the label the player picks (and types).</summary>
public sealed record HolderChoice(string HolderId, string Label);

/// <summary>
/// The candidates of a holder picker (B9 #13b: a <see cref="BindingKind.Holder"/> binding with <see cref="VariableBinding.HolderChoices"/>):
/// the listed holders the chooser may act on now, in list order: those whose current owner
/// (<see cref="Ownership.SideOfHolder(string, RulesPack, GameState?)"/>) is no side or a side of the chooser's held set. The global holder and
/// ids the pack does not declare are never candidates. Label = the holder's name (blank: its id); a repeated label (case-insensitive) gets
/// " #2", " #3" ... in list order (a suffix that is another candidate's plain label is skipped). A picked holder's values are therefore
/// always visible to its chooser and every write to it is allowed. The runner resolves a typed label against the same list.
/// </summary>
public static class HolderChoices
{
    /// <summary>A typed label that is not (or no longer) one of the chooser's candidates.</summary>
    public const string Unknown = "Choose one of the listed holders";

    /// <summary>No listed holder is the chooser's to choose.</summary>
    public const string None = "No holder to choose";

    /// <summary>How many candidate lists were built on this thread (tests read it: a run builds one per picker).</summary>
    [ThreadStatic] internal static int Lists;

    /// <summary>True for a holder picker: a Holder binding with holder choices.</summary>
    public static bool IsPicker(VariableBinding? b) => b is { Kind: BindingKind.Holder, HolderChoices.Count: > 0 };

    /// <param name="s">The state the owners are read on (the full state, or a player view: owner expressions read only Public values, so
    /// both give the same owner).</param>
    /// <param name="held">The chooser's held set (<see cref="RunRequest.PickSides"/>: B8 <c>SideFacts.Held</c>, allies included). Null = the
    /// view's own sides on a player view (an online guest's state), else every listed holder (solo, a hook run).</param>
    public static IReadOnlyList<HolderChoice> For(GameState s, RulesPack pack, VariableBinding b, IReadOnlySet<string>? held)
    {
        Lists++;
        var list = new List<HolderChoice>();
        if (pack == null || b?.HolderChoices is not { Count: > 0 } ids) return list;
        held ??= s is { IsPlayerView: true } ? new HashSet<string>(s.ViewSides ?? new List<string>(), StringComparer.Ordinal) : null;
        var owners = held == null || s == null ? null : Ownership.HolderSides(s, pack);
        var plain = new List<(string Id, string Label)>();
        foreach (var id in ids)
        {
            if (id == null || id == HolderDef.GlobalId) continue;
            if ((pack.Holders ?? new List<HolderDef>()).FirstOrDefault(h => h != null && h.Id == id) is not { } h) continue;
            if (held != null && owners!(id) is { } owner && !held.Contains(owner)) continue;
            plain.Add((id, string.IsNullOrWhiteSpace(h.Name) ? id : h.Name.Trim()));
        }
        var plains = new HashSet<string>(plain.Select(c => c.Label), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, label0) in plain)
        {
            var label = label0;
            if (!used.Add(label))
            {
                var k = next.GetValueOrDefault(label0, 2);
                while (plains.Contains($"{label0} #{k}") || used.Contains($"{label0} #{k}")) k++;
                label = $"{label0} #{k}";
                next[label0] = k + 1;
                used.Add(label);
            }
            list.Add(new HolderChoice(id, label));
        }
        return list;
    }

    /// <summary>The candidate a typed label names (trimmed, case-insensitive), or null.</summary>
    public static HolderChoice? Find(IReadOnlyList<HolderChoice> list, string? typed)
    {
        var t = (typed ?? "").Trim();
        if (t.Length == 0) return null;
        return list.FirstOrDefault(c => string.Equals(c.Label, t, StringComparison.OrdinalIgnoreCase));
    }
}
