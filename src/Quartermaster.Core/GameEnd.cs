using System.Text.Json.Serialization;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>
/// How the game ended (B5 #73, <see cref="GameState.GameOver"/>): the result text (shown as <c>Game over: {Text}</c>), the winning side ids
/// (null = no winner), the victory condition that ended it (null = the last turn was played) and the position it ended at.
/// </summary>
public sealed record GameOverState(string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Winners,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VictoryId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PhasePosition? At);

/// <summary>The last turn and the victory conditions of a rules pack (B5 #73). Never throws on bad pack data.</summary>
public static class GameEnd
{
    /// <summary>The result text when the last turn has been played and no victory condition is met.</summary>
    public const string LastTurnText = "the last turn has been played";

    /// <summary>
    /// True when the step from <paramref name="cur"/> to <paramref name="next"/> passes the pack's end: with <see cref="RulesPack.LastTurn"/>
    /// set, <paramref name="next"/> is past it; with <see cref="RulesPack.LastStage"/> also set, also when <paramref name="cur"/> is in the
    /// last turn, in a block at that stage (its <see cref="PhasePosition.Repeat"/>), and <paramref name="next"/> is not in that block's same
    /// repeat. Without a last turn the game never ends this way.
    /// </summary>
    public static bool Reached(RulesPack pack, PhasePosition cur, PhasePosition next)
    {
        if (pack.LastTurn is not { } last) return false;
        if (next.Turn > last) return true;
        if (pack.LastStage is not { } stage || cur.Turn != last || cur.Repeat != stage) return false;
        var idx = Sequencer.IndexOf(pack, cur);
        if (idx < 0 || Sequencer.OuterBlock(pack, idx) is not { } block) return false;
        var sameRepeat = next.Turn == cur.Turn && next.Repeat == cur.Repeat && Sequencer.IndexOf(pack, next) is var n && n >= 0
            && ReferenceEquals(Sequencer.OuterBlock(pack, n), block);
        return !sameRepeat;
    }

    /// <summary>
    /// The first victory condition (pack order) that is true at <paramref name="at"/>, read as a phase condition (globals, tables, turn(),
    /// stage(), segment(), phaseside(), whole-game aggregates) on <paramref name="s"/>; null when none is. With <paramref name="phaseId"/>,
    /// only conditions whose <see cref="VictoryCondition.PhaseIds"/> is null or holds it are checked; null checks every condition (the
    /// last-turn check). Fail-closed: a condition that fails to parse or evaluate counts as not met, and <paramref name="warn"/> gets
    /// <c>Victory condition {n} failed: {error}; it counts as not met</c> (n 1-based in pack order). Host only: never call it on a player
    /// view or while replaying.
    /// </summary>
    public static VictoryCondition? Check(RulesPack pack, GameState s, PhasePosition at, string? phaseId, ModuleModel? model, Action<string>? warn)
    {
        var list = pack.Victory;
        if (list == null) return null;
        for (var i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (v == null || string.IsNullOrWhiteSpace(v.Condition)) continue;
            if (phaseId != null && v.PhaseIds != null && !v.PhaseIds.Contains(phaseId, StringComparer.Ordinal)) continue;
            try
            {
                if (PhaseConditions.Evaluate(pack, s, v.Condition, at, model).IsTruthy) return v;
            }
            catch (Exception ex) when (ex is EvalError or ParseError)
            {
                warn?.Invoke($"Victory condition {i + 1} failed: {ex.Message}; it counts as not met");
            }
        }
        return null;
    }

    /// <summary>
    /// The result text of a game ended by <paramref name="v"/>: its <see cref="VictoryCondition.Message"/>; blank → <c>{side name} wins</c>;
    /// blank and no side → <c>the game has ended</c>. Null (the last turn played, no victory) → <see cref="LastTurnText"/>.
    /// </summary>
    public static string ResultText(RulesPack pack, VictoryCondition? v)
    {
        if (v == null) return LastTurnText;
        if (!string.IsNullOrWhiteSpace(v.Message)) return v.Message.Trim();
        if (string.IsNullOrWhiteSpace(v.SideId)) return "the game has ended";
        var name = pack.Sides?.FirstOrDefault(x => x != null && x.Id == v.SideId)?.Name;
        return $"{(string.IsNullOrWhiteSpace(name) ? v.SideId : name)} wins";
    }
}
