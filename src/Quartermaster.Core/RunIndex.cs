using System.Runtime.CompilerServices;
using Quartermaster.Rules;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Core;

/// <summary>
/// Counts applied formula runs for <c>runs()</c> (spec #71) from the log's run stamps (<see cref="LogEntry.AppliedRun"/>, one line per
/// applied run, with its turn and <see cref="LogEntry.Stage"/>). An index is cached per <see cref="GameState.Log"/> list: it is extended
/// with lines appended since the last count, and rebuilt when the list shrinks or its last known line was replaced (undo, step back, load).
/// So a ForEach pass does not rescan the log per unit.
/// </summary>
internal static class RunIndex
{
    /// <summary>Test hook: log lines read into an index on this thread.</summary>
    [ThreadStatic] internal static int ScannedForTest;

    private const string HiddenText = "runs() cannot be counted in this view";

    private sealed class Index
    {
        public int Count;
        public LogEntry? Last;
        /// <summary>(formula id, piece id or "" for no unit) → (turn, stage) of each applied run, in log order.</summary>
        public readonly Dictionary<(string Formula, string Piece), List<(int? Turn, int? Stage)>> Runs = new();
    }

    private static readonly ConditionalWeakTable<List<LogEntry>, Index> Cache = new();

    /// <summary>
    /// The applied runs of <paramref name="formulaId"/> on <paramref name="pieceId"/> (null: runs made without a unit) in
    /// <paramref name="period"/> ("stage": same turn and <see cref="PhasePosition.Repeat"/>; "turn": same turn; "game": all).
    /// Throws <see cref="EvalError"/> for an unknown formula, and, in a player view, for no piece or a piece the viewer does not hold
    /// (<see cref="EvalError.HiddenData"/>: the count may include runs the view cannot see). <paramref name="sides"/> is the caller's
    /// side map, compiled on first use.
    /// </summary>
    public static double Count(GameState s, RulesPack pack, string formulaId, string? pieceId, string period,
        Lazy<Func<PieceInstance, string?>>? sides = null)
    {
        if (pack.ById<FormulaDef>(formulaId) is null) throw new EvalError($"runs() names formula \"{formulaId}\", which does not exist");
        if (Ownership.InUnitFilter) throw new EvalError("runs() is not available in a side's unit filter");
        if (s.IsPlayerView)
        {
            if (pieceId == null || !s.Pieces.TryGetValue(pieceId, out var piece)) throw new EvalError(HiddenText) { HiddenData = true };
            var side = (sides ?? PieceFilter.SideMap(s, pack)).Value(piece);
            if (side == null || s.ViewSides?.Contains(side) != true) throw new EvalError(HiddenText) { HiddenData = true };
        }

        var log = s.Log ?? new List<LogEntry>();
        var index = Cache.GetValue(log, _ => new Index());
        lock (index)
        {
            Update(index, log);
            if (!index.Runs.TryGetValue((formulaId, pieceId ?? ""), out var runs)) return 0;
            int? turn = s.Position?.Turn, stage = s.Position?.Repeat;
            return period switch
            {
                "stage" => runs.Count(r => r.Turn == turn && r.Stage == stage),
                "turn" => runs.Count(r => r.Turn == turn),
                _ => runs.Count,
            };
        }
    }

    private static void Update(Index index, List<LogEntry> log)
    {
        if (log.Count < index.Count || (index.Count > 0 && !ReferenceEquals(log[index.Count - 1], index.Last)))
        {
            index.Runs.Clear();
            index.Count = 0;
            index.Last = null;
        }
        for (var i = index.Count; i < log.Count; i++)
        {
            var e = log[i];
            ScannedForTest++;
            if (e == null || string.IsNullOrEmpty(e.AppliedRun)) continue;
            var key = (e.AppliedRun, e.PieceId ?? "");
            if (!index.Runs.TryGetValue(key, out var list)) index.Runs[key] = list = new();
            list.Add((e.Turn, e.Stage));
        }
        index.Count = log.Count;
        index.Last = log.Count == 0 ? null : log[^1];
    }
}
