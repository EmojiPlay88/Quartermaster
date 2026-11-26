using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>
/// One row of the phase panel's Due section (B5 #62).
/// </summary>
/// <param name="RowId">The schedule row's id (what <see cref="IActionPort.ActOnSchedule"/> takes).</param>
/// <param name="StateText"><c>Due now</c> or <c>Overdue since turn {T}</c>; a solo row without counters adds <c>, no counter matches</c>.</param>
/// <param name="ButtonText"><c>Place</c> (Arrive, Return) or <c>Withdraw</c> (Leave); null for a row without counters.</param>
/// <param name="Tooltip">The button's tooltip; null without a button.</param>
/// <param name="PieceIds">The row's counters the viewer may act on.</param>
/// <param name="ZoneMapId">The module id of the row's zone map (where arrivals wait, where withdrawals go); null without one.</param>
public sealed record DueEntry(string RowId, string Label, string StateText, string? ButtonText, string? Tooltip, IReadOnlyList<string> PieceIds,
    string? ZoneMapId);

/// <summary>
/// The Due section of the phase panel: the schedule rows reached and not done (<see cref="ScheduleDue.For"/>), built from what the viewer
/// may see (its own player view, its own sides), so another side's hidden counters never show and a row of only such counters is left out.
/// </summary>
public static class DueModel
{
    public const string DueNowText = "Due now";
    public const string NoCounterText = "no counter matches";
    public const string PlaceText = "Place";
    public const string WithdrawText = "Withdraw";
    public const string RemoveTip = "Remove these counters from the game.";

    public static string OverdueText(int turn) => $"Overdue since turn {turn}";
    public static string PlaceTip(string map) => $"Mark these arrivals placed and show the counters on {map}; drag them to their hexes.";
    public static string WithdrawTip(string map) => $"Move these counters to {map}.";

    /// <summary>The due rows for the session's viewer, in due order; empty without a game, before the start or after the end.</summary>
    public static IReadOnlyList<DueEntry> Rows(ModuleSession s)
    {
        if (s.Rules is not { Schedule.Count: > 0 } pack || s.Game?.State is not { } state) return Array.Empty<DueEntry>();
        var view = s.View ?? state;
        // A player view carries its sides; a guest's snapshot is its view (its session holds no view sides).
        IReadOnlySet<string>? sides = view.ViewSides is { } vs ? new HashSet<string>(vs, StringComparer.Ordinal)
            : s.IsGuest ? s.GuestSides ?? new HashSet<string>() : s.ViewSides;
        var res = new List<DueEntry>();
        foreach (var due in ScheduleDue.For(pack, view, s.Model, sides))
        {
            var row = due.Row;
            var map = ScheduleDue.ZoneMap(s.Model, row.ZoneMapId);
            var zoneId = string.IsNullOrWhiteSpace(row.ZoneMapId) ? null : map?.Id ?? row.ZoneMapId.Trim();
            var mapName = map?.Name ?? zoneId ?? "";
            var when = due.Now ? DueNowText : OverdueText(row.Turn);
            if (due.PieceIds.Count == 0)
            {
                res.Add(new DueEntry(row.Id, due.Label, $"{when}, {NoCounterText}", null, null, due.PieceIds, zoneId));
                continue;
            }
            var leave = row.Action == ScheduleAction.Leave;
            var tip = !leave ? PlaceTip(mapName) : zoneId == null ? RemoveTip : WithdrawTip(mapName);
            res.Add(new DueEntry(row.Id, due.Label, when, leave ? WithdrawText : PlaceText, tip, due.PieceIds, zoneId));
        }
        return res;
    }
}
