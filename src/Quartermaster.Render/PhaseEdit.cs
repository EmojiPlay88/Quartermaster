using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>Pure edit operations behind the workbench Phases tab. Each returns the new pack, or the unchanged pack with a message.</summary>
public static class PhaseEdit
{
    public const string BlockBreakMessage = "That move would put a block's first phase after its last";

    private static (RulesPack Pack, string? Error) Fail(RulesPack p, string msg) => (p, msg);

    private static int IndexOf(RulesPack p, string id) => p.Phases.FindIndex(x => x.Id == id);

    public static (RulesPack Pack, string? Error) Add(RulesPack pack, string name, int at)
    {
        var n = (name ?? "").Trim();
        var phase = new PhaseDef { Id = Guid.NewGuid().ToString("N"), Name = n.Length == 0 ? "New phase" : n };
        var list = pack.Phases.ToList();
        list.Insert(Math.Clamp(at, 0, list.Count), phase);
        return (pack with { Phases = list }, null);
    }

    public static (RulesPack Pack, string? Error) Duplicate(RulesPack pack, int index)
    {
        if (index < 0 || index >= pack.Phases.Count) return Fail(pack, "Select a phase first");
        var src = pack.Phases[index];
        var copy = src with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = src.Name + " (copy)",
            Checklist = src.Checklist.ToList(),
            OnStart = src.OnStart.ToList(),
            OnMove = src.OnMove.ToList(),
            Warnings = src.Warnings.ToList(),
        };
        var list = pack.Phases.ToList();
        list.Insert(index + 1, copy);
        return (pack with { Phases = list }, null);
    }

    public static (RulesPack Pack, string? Error) Remove(RulesPack pack, int index)
    {
        if (index < 0 || index >= pack.Phases.Count) return Fail(pack, "Select a phase first");
        var id = pack.Phases[index].Id;
        var blocks = new List<PhaseBlock>();
        foreach (var b in pack.Blocks)
        {
            var f = IndexOf(pack, b.FirstPhaseId);
            var l = IndexOf(pack, b.LastPhaseId);
            if (f < 0 || l < 0 || f > l || index < f || index > l) { blocks.Add(b); continue; }
            if (f == l) continue; // the only phase of the block
            if (index == f) blocks.Add(b with { FirstPhaseId = pack.Phases[f + 1].Id });
            else if (index == l) blocks.Add(b with { LastPhaseId = pack.Phases[l - 1].Id });
            else blocks.Add(b);
        }
        return (pack with { Phases = pack.Phases.Where(p => p.Id != id).ToList(), Blocks = blocks }, null);
    }

    public static (RulesPack Pack, string? Error) Move(RulesPack pack, int index, int delta)
    {
        var to = index + delta;
        if (index < 0 || index >= pack.Phases.Count || to < 0 || to >= pack.Phases.Count) return Fail(pack, "Can't move further");
        if (delta == 0) return (pack, null);
        var list = pack.Phases.ToList();
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(to, item);
        var moved = pack with { Phases = list };
        foreach (var b in pack.Blocks)
        {
            var f = IndexOf(moved, b.FirstPhaseId);
            var l = IndexOf(moved, b.LastPhaseId);
            if (f >= 0 && l >= 0 && f > l) return Fail(pack, BlockBreakMessage);
        }
        return (moved, null);
    }

    public static (RulesPack Pack, string? Error) MakeBlock(RulesPack pack, int first, int last, int count, string label)
    {
        if (first < 0 || last >= pack.Phases.Count || first > last) return Fail(pack, "Select neighbouring phases");
        if (count < 1) return Fail(pack, "Count must be at least 1");
        foreach (var b in pack.Blocks)
        {
            var f = IndexOf(pack, b.FirstPhaseId);
            var l = IndexOf(pack, b.LastPhaseId);
            if (f >= 0 && l >= 0 && f <= last && first <= l) return Fail(pack, "These phases are already in a block");
        }
        var block = new PhaseBlock { FirstPhaseId = pack.Phases[first].Id, LastPhaseId = pack.Phases[last].Id, Count = count, Label = (label ?? "").Trim() };
        return (pack with { Blocks = pack.Blocks.Append(block).ToList() }, null);
    }

    public static (RulesPack Pack, string? Error) RemoveBlock(RulesPack pack, int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= pack.Blocks.Count) return Fail(pack, "Select a phase inside a block first");
        return (pack with { Blocks = pack.Blocks.Where((_, i) => i != blockIndex).ToList() }, null);
    }

    // ---------- sides ----------

    private static string? SideProblem(RulesPack pack, string name, string? exceptId)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "The name cannot be empty";
        return pack.Sides.Any(s => s.Id != exceptId && string.Equals(s.Name.Trim(), n, StringComparison.OrdinalIgnoreCase))
            ? "Another side already has this name" : null;
    }

    public static (RulesPack Pack, string? Error) AddSide(RulesPack pack, string name)
    {
        if (SideProblem(pack, name, null) is { } err) return Fail(pack, err);
        var side = new SideDef { Id = Guid.NewGuid().ToString("N"), Name = name.Trim() };
        return (pack with { Sides = pack.Sides.Append(side).ToList() }, null);
    }

    public static (RulesPack Pack, string? Error) RenameSide(RulesPack pack, string id, string name)
    {
        if (pack.Sides.All(s => s.Id != id)) return Fail(pack, "That side no longer exists");
        if (SideProblem(pack, name, id) is { } err) return Fail(pack, err);
        return (pack with { Sides = pack.Sides.Select(s => s.Id == id ? s with { Name = name.Trim() } : s).ToList() }, null);
    }

    public static (RulesPack Pack, string? Error) SetSideFilter(RulesPack pack, string id, string? filter) =>
        (pack with { Sides = pack.Sides.Select(s => s.Id == id ? s with { UnitFilter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim() } : s).ToList() }, null);

    public static (RulesPack Pack, string? Error) RemoveSide(RulesPack pack, string id) =>
        (pack with
        {
            Sides = pack.Sides.Where(s => s.Id != id).ToList(),
            Phases = pack.Phases.Select(p => p.SideId == id ? p with { SideId = null } : p).ToList(),
        }, null);

    // ---------- per-phase fields ----------

    public static RulesPack UpdatePhase(RulesPack pack, string id, Func<PhaseDef, PhaseDef> edit) =>
        pack with { Phases = pack.Phases.Select(p => p.Id == id ? edit(p) : p).ToList() };
}
