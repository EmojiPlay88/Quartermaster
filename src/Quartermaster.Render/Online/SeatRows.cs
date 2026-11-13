using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>One line of the host's Players dialog. <see cref="Seat"/> is null for the "(open seat)" line.</summary>
public sealed record SeatRow(string Player, string Sides, string Status, bool Greyed, Seat? Seat);

/// <summary>What the host's Players dialog shows and allows (spec 8a.10), shared by every UI.</summary>
public static class SeatRows
{
    public const string Note = "Free sides are open seats in the lobby. A seat can be freed when its player has left or holds no side.";
    public const string CannotFreeText = "Only a seat whose player has left, or that holds no side yet, can be freed.";
    public const string AssignPrompt = "Sides this player holds:";

    public static string FreeQuestion(string name) => $"Free {name}'s seat? Their sides become open seats again.";

    public static string AssignTitle(string name) => $"Sides for {name}";

    public static IReadOnlyList<SeatRow> For(Seats seats, RulesPack? pack)
    {
        string NameOf(string sideId) => pack?.Sides?.FirstOrDefault(x => x?.Id == sideId)?.Name is { Length: > 0 } n ? n : sideId;
        var rows = new List<SeatRow>();
        foreach (var seat in seats.All)
        {
            var status = seat.IsHost ? "host (you)" : !seat.Connected ? "left" : seat.Sides.Count == 0 ? "choosing a side" : "connected";
            rows.Add(new SeatRow(seat.Name, seat.Sides.Count == 0 ? "—" : string.Join(", ", seat.Sides.Select(NameOf)), status, !seat.Connected, seat));
        }
        var free = seats.FreeSides(pack);
        if (free.Count > 0)
            rows.Add(new SeatRow("(open seat)", string.Join(", ", free.Select(NameOf)), "free", true, null));
        return rows;
    }

    public static bool CanAssign(Seat? s) => s != null;

    public static bool CanFree(Seat? s) => s is { IsHost: false } && (!s.Connected || s.Sides.Count == 0);

    /// <summary>The sides the host may tick for <paramref name="seat"/>: free ones plus its own, in pack order.</summary>
    public static IReadOnlyList<SideDef> Offered(Seats seats, RulesPack pack, Seat seat)
    {
        var free = seats.FreeSides(pack);
        return pack.Sides.Where(x => x?.Id != null && (free.Contains(x.Id) || seat.Sides.Contains(x.Id))).ToList();
    }
}
