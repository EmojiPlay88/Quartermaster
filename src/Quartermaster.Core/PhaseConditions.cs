using Quartermaster.Rules;
using Quartermaster.Rules.Expr;

namespace Quartermaster.Core;

/// <summary>Where Next Phase (or Start) goes: the position, the phases passed over on the way, and a notice for the phase strip.</summary>
public sealed record PhaseStep(PhasePosition To, IReadOnlyList<SkippedPhase> Skipped, string? Notice);

/// <summary>
/// Phase conditions: a phase whose condition is false is skipped when the game advances. Conditions are evaluated only here, before the
/// controller issues an <see cref="AdvancePhase"/>; replaying that command never re-evaluates them.
/// </summary>
public static class PhaseConditions
{
    public const string GuardNotice = "Every phase was skipped; check the phase conditions";

    /// <summary>
    /// True when the phase runs at <paramref name="at"/>: an empty condition, a true one, or one that fails to parse or evaluate
    /// (fail-open, with <paramref name="error"/> set).
    /// </summary>
    public static bool Runs(RulesPack pack, GameState s, PhaseDef phase, PhasePosition at, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(phase.Condition)) return true;
        try { return Evaluator.Evaluate(Parser.Parse(phase.Condition), new PhaseContext(pack, s, at)).IsTruthy; }
        catch (Exception ex) when (ex is EvalError or ParseError)
        {
            error = ex.Message;
            return true;
        }
    }

    /// <summary>The first position of the game, skipping phases whose condition is false. Null when the pack has no phases.</summary>
    public static PhaseStep? First(RulesPack pack, GameState s) =>
        Sequencer.First(pack) is { } first ? Walk(pack, s, first, first) : null;

    /// <summary>The position after <paramref name="cur"/>, skipping phases whose condition is false. Null when the pack has no phases.</summary>
    public static PhaseStep? Next(RulesPack pack, GameState s, PhasePosition cur) =>
        Sequencer.Next(pack, cur) is { } next ? Walk(pack, s, next, cur) : null;

    /// <summary>
    /// Tests candidates from <paramref name="candidate"/> onward. Stops after one full turn of candidates (on reaching the phase of
    /// <paramref name="origin"/> one turn later, or after every repeat of every phase plus one): then the first candidate is used, with the guard notice.
    /// </summary>
    private static PhaseStep Walk(RulesPack pack, GameState s, PhasePosition candidate, PhasePosition origin)
    {
        var start = candidate;
        var skipped = new List<SkippedPhase>();
        var limit = TurnLength(pack) + 1;
        for (var i = 0; i < limit; i++)
        {
            var phase = pack.Phases[candidate.PhaseIndex];
            if (Runs(pack, s, phase, candidate, out var error))
                return new PhaseStep(candidate, skipped, error == null ? null : $"Condition of {phase.Name} failed: {error}; phase not skipped");
            skipped.Add(new SkippedPhase(phase.Id, phase.Name, candidate.Turn, candidate.Repeat));
            if (candidate.PhaseId == origin.PhaseId && candidate.Turn > origin.Turn) break;
            candidate = Sequencer.Next(pack, candidate)!;
        }
        return new PhaseStep(start, Array.Empty<SkippedPhase>(), GuardNotice);
    }

    /// <summary>How many positions one turn has: each phase once, times its block's count.</summary>
    private static int TurnLength(RulesPack pack)
    {
        var n = pack.Phases.Count;
        foreach (var b in Sequencer.ValidBlocks(pack))
        {
            var f = pack.Phases.FindIndex(p => p.Id == b.FirstPhaseId);
            var l = pack.Phases.FindIndex(p => p.Id == b.LastPhaseId);
            n += (l - f + 1) * (b.Count - 1);
        }
        return n;
    }

    /// <summary>Reads global properties (Default when unset) and the candidate's turn and stage. No pieces, no dice.</summary>
    private sealed class PhaseContext(RulesPack pack, GameState s, PhasePosition at) : IEvalContext
    {
        public Value GetVariable(string name)
        {
            var def = pack.PropertyByName(name) ?? throw new EvalError($"Unknown property '{name}'");
            if (def.Scope == PropertyScope.Pieces) throw new EvalError($"{name} is not a global property");
            return Value.Of(Ledger.GetOrDefault(s, LedgerKey.Holder(HolderDef.GlobalId), def));
        }

        public Value Lookup(string tableName, Value row, Value? col) => PieceFilter.LookupTable(pack, tableName, row, col);
        public IReadOnlyList<int> RollDice(int n, int sides) => throw new EvalError("dice() is not available in a phase condition");
        public bool Has(string property) => throw new EvalError("has() is not available in a phase condition");
        public Value PieceName() => throw new EvalError("name() is not available in a phase condition");
        public Value Marker(string key) => throw new EvalError("marker() is not available in a phase condition");
        public double Turn() => at.Turn;
        public double Stage() => at.Repeat;
    }
}
