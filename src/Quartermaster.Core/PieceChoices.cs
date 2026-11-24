using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>One candidate of a Piece binding: the label the player picks (and types) and the piece it stands for.</summary>
public sealed record PieceChoice(string Label, string PieceId);

/// <summary>
/// The candidate pieces of a Piece binding (<see cref="BindingKind.Piece"/>): every piece matching its "which pieces" filter that the viewer
/// sees, labelled "{name} ({location})" ("{name}" off grid), a repeated label (case-insensitive) suffixed " #2", " #3" ... in state order (the
/// first keeps the plain label), sorted by label (ordinal, ignoring case). The runner resolves a typed label against the same list.
/// </summary>
public static class PieceChoices
{
    /// <summary>A typed label that is not (or no longer) one of the candidates.</summary>
    public const string Unknown = "Choose one of the listed pieces";

    /// <summary>No candidate with an empty filter; with a filter it is "No piece matches \"{filter}\"".</summary>
    public const string NoPiece = "No piece to choose";

    /// <param name="sides">The viewer's sides: the list is built on that viewer's <see cref="PlayerView"/> (only pieces it can see, a masked
    /// piece of another side under its nameless placeholder name, the filter answered as the viewer's own list answers it), so the host and the
    /// guest get the same labels. Null = every piece of <paramref name="s"/>, real names.</param>
    public static IReadOnlyList<PieceChoice> For(GameState s, RulesPack pack, ModuleModel model, VariableBinding b, IReadOnlySet<string>? sides) =>
        Named(s, pack, model, b, sides).Select(c => new PieceChoice(c.Label, c.PieceId)).ToList();

    /// <summary>The candidates with each piece's name as the viewer sees it (for the working description).</summary>
    internal static List<(string Label, string PieceId, string Name)> Named(GameState s, RulesPack pack, ModuleModel model, VariableBinding b,
        IReadOnlySet<string>? sides)
    {
        var shown = sides == null ? s : PlayerView.For(s, pack, model, sides);
        var match = PieceFilter.Compile(b.PieceFilter, shown, pack, model);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string Label, string PieceId, string Name)>();
        foreach (var p in shown.Pieces.Values)
        {
            if (p?.Id == null || !match(p)) continue;
            var name = PieceInfo.Name(p) ?? "";
            var loc = PieceInfo.Location(p, model);
            var label = (loc == null ? name : $"{name} ({loc})").Trim();
            var n = seen.TryGetValue(label, out var k) ? k + 1 : 1;
            seen[label] = n;
            list.Add((n == 1 ? label : $"{label} #{n}".Trim(), p.Id, name));
        }
        return list.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
