namespace Quartermaster.Rules;

/// <summary>
/// What the pack says about renamed sides (former ids) and alliances. <see cref="Held"/> is the one place a player's held side set is formed:
/// it is called only by ModuleSession.SetViewSides (and its recompute on Commit/UpdateRules/LoadGuestRules), ModuleSession.ApplySnapshot,
/// IntentExecutor.Execute, HostSession.SendSnapshot and PackHarness.ViewFor. No rule reads an alliance itself; <see cref="Allies"/> (B10) is
/// read only by Sequencer.Covers (the units of a phase) and PhaseController.OnMoveCore (whether to build its side map).
/// </summary>
public static class SideFacts
{
    /// <summary>Former id → the side that lists it (ordinal); null when no side has a former id. A former id that is a current side id is ignored; one listed by two sides maps to the first in pack order.</summary>
    public static IReadOnlyDictionary<string, string>? FormerMap(RulesPack? pack)
    {
        if (pack is null) return null;
        Dictionary<string, string>? map = null;
        HashSet<string>? current = null;
        foreach (var s in pack.Sides)
        {
            if (s.FormerIds is not { Count: > 0 } ids) continue;
            current ??= pack.Sides.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var f in ids)
            {
                if (string.IsNullOrEmpty(f) || current.Contains(f)) continue;
                map ??= new Dictionary<string, string>(StringComparer.Ordinal);
                map.TryAdd(f, s.Id);
            }
        }
        return map;
    }

    /// <summary>The side that lists <paramref name="sideId"/> as a former id, else the id unchanged.</summary>
    public static string? Canonical(RulesPack? pack, string? sideId) =>
        sideId != null && FormerMap(pack) is { } map && map.TryGetValue(sideId, out var to) ? to : sideId;

    /// <summary>The side's trimmed alliance, or null.</summary>
    public static string? Alliance(SideDef? side)
    {
        var a = side?.AllianceId?.Trim();
        return string.IsNullOrEmpty(a) ? null : a;
    }

    /// <summary>True when some side has an alliance.</summary>
    public static bool HasAlliances(RulesPack? pack) => pack != null && pack.Sides.Any(s => Alliance(s) != null);

    /// <summary>
    /// A player's held set: null stays null (solo); each id is translated by <see cref="Canonical"/>; every side in the alliance of a held side is added.
    /// Without alliances and former ids in the pack an <see cref="IReadOnlySet{T}"/> comes back as the same instance; otherwise a new ordinal set. Empty stays empty.
    /// </summary>
    public static IReadOnlySet<string>? Held(RulesPack? pack, IEnumerable<string>? sides)
    {
        if (sides is null) return null;
        var map = FormerMap(pack);
        bool alliances = HasAlliances(pack);
        if (map is null && !alliances && sides is IReadOnlySet<string> same) return same;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in sides)
            if (id != null) set.Add(map != null && map.TryGetValue(id, out var to) ? to : id);
        if (alliances && set.Count > 0)
        {
            var held = new HashSet<string>(pack!.Sides.Where(s => set.Contains(s.Id) && Alliance(s) != null).Select(s => Alliance(s)!), StringComparer.OrdinalIgnoreCase);
            if (held.Count > 0)
                foreach (var s in pack.Sides)
                    if (Alliance(s) is { } a && held.Contains(a)) set.Add(s.Id);
        }
        return set;
    }

    /// <summary>B10: the side and every other side with the same alliance (trimmed, case-insensitive), ordinal; null when the side is
    /// unknown, has no alliance, or no other side shares it.</summary>
    public static IReadOnlySet<string>? Allies(RulesPack? pack, string? sideId)
    {
        if (pack is null || sideId is null) return null;
        var side = pack.Sides.FirstOrDefault(s => s != null && s.Id == sideId);
        if (Alliance(side) is not { } alliance) return null;
        HashSet<string>? set = null;
        foreach (var s in pack.Sides)
            if (s != null && s.Id != sideId && !string.IsNullOrEmpty(s.Id) && Alliance(s) is { } a && string.Equals(a, alliance, StringComparison.OrdinalIgnoreCase))
                (set ??= new HashSet<string>(StringComparer.Ordinal) { sideId }).Add(s.Id);
        return set;
    }

    /// <summary>The number of alliance groups: sides sharing an alliance count once, a side without one counts alone.</summary>
    public static int Groups(RulesPack pack) =>
        pack.Sides.Select(Alliance).Where(a => a != null).Distinct(StringComparer.OrdinalIgnoreCase).Count() + pack.Sides.Count(s => Alliance(s) == null);

    /// <summary>The pack with every side reference that names a former id rewritten to the listing side; the same instance when nothing changes.</summary>
    public static RulesPack MapReferences(RulesPack pack)
    {
        var map = FormerMap(pack);
        if (map is null) return pack;
        string? M(string? id) => id != null && map.TryGetValue(id, out var to) ? to : id;
        bool changed = false;
        T Pick<T>(T before, T after) { if (!ReferenceEquals(before, after)) changed = true; return after; }

        List<StartEntry>? Entries(List<StartEntry>? list)
        {
            if (list is null || !list.Any(e => M(e.AskSideId) != e.AskSideId)) return list;
            return list.Select(e => M(e.AskSideId) != e.AskSideId ? e with { AskSideId = M(e.AskSideId) } : e).ToList();
        }

        var phases = pack.Phases.Select(ph =>
        {
            var side = M(ph.SideId);
            List<string>? reactors = ph.Reactors;
            if (reactors != null && (reactors.Any(r => M(r) != r) || side != ph.SideId))
                reactors = reactors.Select(r => M(r)!).Distinct(StringComparer.Ordinal).Where(r => r != side).ToList() is { Count: > 0 } l ? l : null;
            var start = Entries(ph.OnStart)!;
            var end = Entries(ph.OnEnd);
            if (side == ph.SideId && ReferenceEquals(reactors, ph.Reactors) && ReferenceEquals(start, ph.OnStart) && ReferenceEquals(end, ph.OnEnd)) return ph;
            return ph with { SideId = side, Reactors = reactors, OnStart = start, OnEnd = end };
        }).ToList();
        if (phases.Where((p, i) => !ReferenceEquals(p, pack.Phases[i])).Any()) changed = true; else phases = pack.Phases;

        var holders = pack.Holders.Any(h => M(h.SideId) != h.SideId) ? pack.Holders.Select(h => h with { SideId = M(h.SideId) }).ToList() : pack.Holders;
        var owners = pack.MapOwners.Any(o => M(o.SideId) != o.SideId) ? pack.MapOwners.Select(o => o with { SideId = M(o.SideId)! }).ToList() : pack.MapOwners;
        var alerts = pack.Alerts.Any(a => M(a.SideId) != a.SideId) ? pack.Alerts.Select(a => a with { SideId = M(a.SideId) }).ToList() : pack.Alerts;
        var victory = pack.Victory != null && pack.Victory.Any(v => M(v.SideId) != v.SideId) ? pack.Victory.Select(v => v with { SideId = M(v.SideId) }).ToList() : pack.Victory;
        _ = Pick(pack.Holders, holders); _ = Pick(pack.MapOwners, owners); _ = Pick(pack.Alerts, alerts); _ = Pick(pack.Victory, victory);
        return changed ? pack with { Phases = phases, Holders = holders, MapOwners = owners, Alerts = alerts, Victory = victory } : pack;
    }
}
