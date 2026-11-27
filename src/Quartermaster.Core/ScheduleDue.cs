using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// One row of the due list (B5 #62): the schedule row, its label, whether it is due now (else overdue) and the ids of its counters the viewer
/// may act on (in a solo game every counter; possibly none there).
/// </summary>
public sealed record DueRow(ScheduleRow Row, string Label, bool Now, IReadOnlyList<string> PieceIds);

/// <summary>
/// The reinforcement and withdrawal schedule (B5 #62): when a row is reached, which counters it acts on, and the due list. The engine never
/// places or withdraws by itself: a reached row stays listed (due now, then overdue) until a player acts on it (<see cref="MarkScheduled"/>).
/// Everything is read from the state given, so a player's list built from its own view shows only what that player may see and act on.
/// Never throws on bad pack data.
/// </summary>
public static class ScheduleDue
{
    /// <summary>
    /// True when the game at <paramref name="pos"/> is at or past the row: a later turn; or the row's turn and its phase (at its stage when
    /// set); or, in the row's turn, a later phase, except inside the block holding the row's phase, where the stage counts first (a later
    /// repeat is past the row, an earlier one is not). A row whose phase the pack lacks is never reached.
    /// </summary>
    public static bool Reached(RulesPack pack, ScheduleRow row, PhasePosition pos)
    {
        var idx = pack.Phases.FindIndex(p => p.Id == row.PhaseId);
        if (idx < 0) return false;
        if (pos.Turn != row.Turn) return pos.Turn > row.Turn;
        var at = Sequencer.IndexOf(pack, pos);
        var stage = row.Stage ?? 1;
        if (at == idx) return row.Stage == null || pos.Repeat >= row.Stage;
        if (at >= 0 && Sequencer.OuterBlock(pack, idx) is { } block && ReferenceEquals(Sequencer.OuterBlock(pack, at), block))
            return pos.Repeat > stage || (pos.Repeat == stage && at > idx);
        return at > idx;
    }

    /// <summary>True when the row is reached and <paramref name="pos"/> is exactly its turn and phase (and stage when set); a reached row
    /// that is not due now is overdue.</summary>
    public static bool IsNow(RulesPack pack, ScheduleRow row, PhasePosition pos) =>
        Reached(pack, row, pos) && pos.Turn == row.Turn && pos.PhaseId == row.PhaseId && (row.Stage == null || pos.Repeat == row.Stage);

    /// <summary>The row's <see cref="ScheduleRow.Label"/>, else <c>Arrivals, turn {T}</c> / <c>Withdrawals, turn {T}</c> /
    /// <c>Returns, turn {T}</c>.</summary>
    public static string Label(ScheduleRow row) =>
        !string.IsNullOrWhiteSpace(row.Label) ? row.Label.Trim() : row.Action switch
        {
            ScheduleAction.Leave => $"Withdrawals, turn {row.Turn}",
            ScheduleAction.Return => $"Returns, turn {row.Turn}",
            _ => $"Arrivals, turn {row.Turn}",
        };

    /// <summary>The module map a row's <see cref="ScheduleRow.ZoneMapId"/> names: by id, else by name (as a formula's Remove ToMapId); null
    /// when there is no model or no such map.</summary>
    public static MapDef? ZoneMap(ModuleModel? model, string? id) => model == null ? null : ZoneMapIn(model.Maps, id);

    /// <summary>The map <see cref="MapRefs.Find"/> resolves (the one resolver, as the validator uses): trimmed, an exact id first, then an exact name.</summary>
    internal static MapDef? ZoneMapIn(IReadOnlyList<MapDef> maps, string? id) =>
        MapRefs.Find(maps, id);

    /// <summary>
    /// The counters of a row in <paramref name="s"/>: the pieces matching its <see cref="ScheduleRow.Piece"/> filter (none when blank); Arrive
    /// and Return keep those on the zone map (none without one), Leave those not on it (every matching piece without a zone map). In a
    /// player view another side's masked placeholders are never counters.
    /// </summary>
    public static IReadOnlyList<PieceInstance> Counters(RulesPack pack, GameState s, ModuleModel? model, ScheduleRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Piece)) return Array.Empty<PieceInstance>();
        var zone = string.IsNullOrWhiteSpace(row.ZoneMapId) ? null : ZoneMap(model, row.ZoneMapId)?.Id ?? row.ZoneMapId.Trim();
        if (zone == null && row.Action != ScheduleAction.Leave) return Array.Empty<PieceInstance>();
        var match = PieceFilter.Compile(row.Piece, s, pack, model);
        return s.Pieces.Values.Where(p => p != null && !(s.IsPlayerView && PlayerView.IsPlaceholder(p))
            && (zone == null || (p.MapId == zone) == (row.Action != ScheduleAction.Leave)) && match(p)).ToList();
    }

    /// <summary>
    /// The due list at <paramref name="s"/>'s position: reached rows not yet done, in order (turn, stage, phase, pack order); empty when the
    /// game is over or has not started. Each row's <see cref="DueRow.PieceIds"/> are its counters a player holding <paramref name="sides"/>
    /// may act on (<see cref="PieceAccess.CanAct"/> on <paramref name="s"/>); a row with none is left out, except in solo (null sides),
    /// where it is listed without counters.
    /// </summary>
    public static IReadOnlyList<DueRow> For(RulesPack pack, GameState s, ModuleModel? model, IReadOnlySet<string>? sides)
    {
        if (s.GameOver != null || s.Position is not { } pos || pack.Schedule is not { Count: > 0 } rows) return Array.Empty<DueRow>();
        var access = sides == null ? null : new PieceAccess(s, pack, model, sides);
        var res = new List<(DueRow Due, int Stage, int Phase)>();
        foreach (var row in rows)
        {
            if (row == null || s.ScheduleDone?.Contains(row.Id, StringComparer.Ordinal) == true || !Reached(pack, row, pos)) continue;
            var ids = Counters(pack, s, model, row).Select(p => p.Id).Where(id => access == null || access.CanAct(id)).ToList();
            if (ids.Count == 0 && sides != null) continue;
            res.Add((new DueRow(row, Label(row), IsNow(pack, row, pos), ids), row.Stage ?? 1, pack.Phases.FindIndex(p => p.Id == row.PhaseId)));
        }
        return res.OrderBy(r => r.Due.Row.Turn).ThenBy(r => r.Stage).ThenBy(r => r.Phase).Select(r => r.Due).ToList();
    }
}
