using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>Pure edit operations behind the workbench "Schedule" tab. Empty lists and blank text become null.</summary>
public static class ScheduleEdit
{
    /// <summary>Inserts a row (turn 1, the first phase, no counters yet) with the first unused id <c>sched-{n}</c>.</summary>
    public static RulesPack Add(RulesPack pack, int at)
    {
        var list = (pack.Schedule ?? new()).ToList();
        var n = 1;
        while (list.Any(r => r.Id == $"sched-{n}")) n++;
        list.Insert(Math.Clamp(at, 0, list.Count), new ScheduleRow { Id = $"sched-{n}", Turn = 1, PhaseId = pack.Phases.FirstOrDefault()?.Id ?? "" });
        return pack with { Schedule = list };
    }

    /// <summary>Applies the edit to the row with this id; blank zone map, formula and label become null.</summary>
    public static RulesPack Update(RulesPack pack, string id, Func<ScheduleRow, ScheduleRow> edit)
    {
        if (pack.Schedule == null) return pack;
        return pack with { Schedule = pack.Schedule.Select(r => r.Id == id ? Normalise(edit(r)) : r).ToList() };
    }

    private static ScheduleRow Normalise(ScheduleRow r) => r with
    {
        ZoneMapId = string.IsNullOrWhiteSpace(r.ZoneMapId) ? null : r.ZoneMapId,
        FormulaId = string.IsNullOrWhiteSpace(r.FormulaId) ? null : r.FormulaId,
        Label = string.IsNullOrWhiteSpace(r.Label) ? null : r.Label,
    };

    /// <summary>Unchanged pack (same instance) when the index is out of range; the last removal makes the list null.</summary>
    public static RulesPack Remove(RulesPack pack, int index)
    {
        if (pack.Schedule == null || index < 0 || index >= pack.Schedule.Count) return pack;
        var list = pack.Schedule.Where((_, i) => i != index).ToList();
        return pack with { Schedule = list.Count == 0 ? null : list };
    }

    /// <summary>Unchanged pack (same instance) when the move would leave the list.</summary>
    public static RulesPack Move(RulesPack pack, int index, int delta)
    {
        var to = index + delta;
        if (pack.Schedule == null || index < 0 || index >= pack.Schedule.Count || to < 0 || to >= pack.Schedule.Count || delta == 0) return pack;
        var list = pack.Schedule.ToList();
        (list[index], list[to]) = (list[to], list[index]);
        return pack with { Schedule = list };
    }
}
