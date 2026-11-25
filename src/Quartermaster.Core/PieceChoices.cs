using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>One candidate of a Piece binding: the label the player picks (and types) and the piece it stands for.</summary>
public sealed record PieceChoice(string Label, string PieceId);

/// <summary>
/// The candidate pieces of a Piece binding (<see cref="BindingKind.Piece"/>): every piece matching its "which pieces" filter that the viewer
/// sees, labelled "{name} ({location})" ("{name}" off grid; a nameless one, a masked placeholder, named "Unknown piece"), a repeated label
/// (case-insensitive) suffixed " #2", " #3" ... in state order (the first keeps the plain label; a suffix that is another candidate's plain
/// label is skipped), sorted by label (ordinal, ignoring case); with a run piece, the candidates in its hex come first (B3). The runner
/// resolves a typed label against the same list.
/// </summary>
public static class PieceChoices
{
    /// <summary>A typed label that is not (or no longer) one of the candidates.</summary>
    public const string Unknown = "Choose one of the listed pieces";

    /// <summary>No candidate with an empty filter; with a filter it is "No piece matches \"{filter}\"".</summary>
    public const string NoPiece = "No piece to choose";

    /// <summary>The name a candidate without one (a masked piece's nameless placeholder) is labelled with.</summary>
    public const string UnknownName = "Unknown piece";

    /// <param name="sides">The viewer's sides: the list is built on that viewer's <see cref="PlayerView"/> (only pieces it can see, a masked
    /// piece of another side under its nameless placeholder name, the filter answered as the viewer's own list answers it), so the host and the
    /// guest get the same labels. Null = every piece of <paramref name="s"/>, real names.</param>
    public static IReadOnlyList<PieceChoice> For(GameState s, RulesPack pack, ModuleModel model, VariableBinding b, IReadOnlySet<string>? sides) =>
        For(s, pack, model, b, sides, null);

    /// <summary>A "same hex" binding (<see cref="VariableBinding.SameHex"/>) with no other candidate in the run piece's hex.</summary>
    public const string NoPieceInHex = "No piece in this hex";

    /// <param name="runPieceId">The piece the formula runs on: candidates in its hex (same map and grid location, located on the viewer's
    /// state) come first, each group sorted by label; a "same hex" binding lists only those, never the run piece itself (none without a run
    /// piece, or with one off grid or not in the viewer's state). Null = today's order.</param>
    public static IReadOnlyList<PieceChoice> For(GameState s, RulesPack pack, ModuleModel model, VariableBinding b, IReadOnlySet<string>? sides,
        string? runPieceId) =>
        Named(s, pack, model, b, sides, runPieceId).Select(c => new PieceChoice(c.Label, c.PieceId)).ToList();

    /// <summary>How many viewer states <see cref="Named"/> built on this thread (<see cref="PlayerView.For"/>; tests read it).</summary>
    [ThreadStatic] internal static int ViewBuilds;

    /// <summary>The candidates with each piece's name as the viewer sees it (for the working description).</summary>
    internal static List<(string Label, string PieceId, string Name)> Named(GameState s, RulesPack pack, ModuleModel model, VariableBinding b,
        IReadOnlySet<string>? sides, string? runPieceId = null)
    {
        if (sides != null) ViewBuilds++;
        var shown = sides == null ? s : PlayerView.For(s, pack, model, sides);
        // The run piece's hex, as the viewer's state places it (null: no run piece, not shown, or off grid).
        var run = runPieceId != null && shown.Pieces.TryGetValue(runPieceId, out var rp) ? rp : null;
        var hexLoc = run == null ? null : PieceInfo.Location(run, model);
        var hexMap = hexLoc == null ? null : run!.MapId;
        if (b.SameHex && hexLoc == null) return new();
        bool InHex(PieceInstance p, string? loc) =>
            hexLoc != null && loc != null && string.Equals(p.MapId, hexMap, StringComparison.Ordinal) && string.Equals(loc, hexLoc, StringComparison.Ordinal);
        var inHex = new HashSet<string>(StringComparer.Ordinal);
        var match = PieceFilter.Compile(b.PieceFilter, shown, pack, model);
        var plain = new List<(string Label, string PieceId, string Name)>();
        foreach (var p in shown.Pieces.Values)
        {
            if (p?.Id == null) continue;
            if (b.SameHex && p.Id == runPieceId) continue;   // "same hex": the other pieces only
            // The location only for pieces that can be listed (a "same hex" binding rejects by hex first; otherwise the filter first).
            string? loc = null;
            if (b.SameHex)
            {
                loc = PieceInfo.Location(p, model);
                if (!InHex(p, loc)) continue;
            }
            if (!match(p)) continue;
            if (!b.SameHex) loc = PieceInfo.Location(p, model);
            if (InHex(p, loc)) inHex.Add(p.Id);
            var name = PieceInfo.Name(p)?.Trim() is { Length: > 0 } n ? n : UnknownName;   // a masked placeholder has no name
            plain.Add(((loc == null ? name : $"{name} ({loc})").Trim(), p.Id, name));
        }
        // A repeated label gets " #2", " #3" ... in state order, skipping any suffixed label that is another candidate's plain label.
        var plains = new HashSet<string>(plain.Select(c => c.Label), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string Label, string PieceId, string Name)>();
        foreach (var c in plain)
        {
            var label = c.Label;
            if (!used.Add(label))
            {
                var k = next.GetValueOrDefault(c.Label, 2);
                while (plains.Contains($"{c.Label} #{k}") || used.Contains($"{c.Label} #{k}")) k++;
                label = $"{c.Label} #{k}";
                next[c.Label] = k + 1;
                used.Add(label);
            }
            list.Add((label, c.PieceId, c.Name));
        }
        return list.OrderBy(c => inHex.Contains(c.PieceId) ? 0 : 1).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
