using Quartermaster.Rules;

namespace Quartermaster.Core;

/// <summary>Turn-sequence position math over a rules pack. Never throws on bad pack data.</summary>
public static class Sequencer
{
    private sealed record Span(PhaseBlock Block, int First, int Last);

    private static List<Span> Spans(RulesPack pack)
    {
        var res = new List<Span>();
        foreach (var b in pack.Blocks)
        {
            if (b.Count < 1) continue;
            var f = pack.Phases.FindIndex(p => p.Id == b.FirstPhaseId);
            var l = pack.Phases.FindIndex(p => p.Id == b.LastPhaseId);
            if (f < 0 || l < 0 || f > l) continue;
            if (res.Any(r => f <= r.Last && l >= r.First)) continue;
            res.Add(new Span(b, f, l));
        }
        return res;
    }

    public static IReadOnlyList<PhaseBlock> ValidBlocks(RulesPack pack) => Spans(pack).Select(s => s.Block).ToList();

    public static PhasePosition? First(RulesPack pack) =>
        pack.Phases.Count == 0 ? null : new PhasePosition(1, 1, pack.Phases[0].Id, 0);

    public static PhasePosition? Next(RulesPack pack, PhasePosition cur)
    {
        if (pack.Phases.Count == 0) return null;
        var idx = pack.Phases.FindIndex(p => p.Id == cur.PhaseId);
        if (idx < 0) idx = Math.Clamp(cur.PhaseIndex, 0, pack.Phases.Count - 1);
        var span = Spans(pack).FirstOrDefault(s => idx >= s.First && idx <= s.Last);
        if (span != null && idx == span.Last && cur.Repeat < span.Block.Count)
            return new PhasePosition(cur.Turn, cur.Repeat + 1, pack.Phases[span.First].Id, span.First);
        var n = idx + 1;
        if (n >= pack.Phases.Count) return new PhasePosition(cur.Turn + 1, 1, pack.Phases[0].Id, 0);
        // Moving within a block keeps the current repeat; leaving or entering one is repeat 1.
        var inSameBlock = span != null && n <= span.Last;
        var repeat = inSameBlock ? Math.Clamp(cur.Repeat, 1, span!.Block.Count) : 1;
        return new PhasePosition(cur.Turn, repeat, pack.Phases[n].Id, n);
    }

    public static (PhasePosition? Position, string? Notice) Resolve(RulesPack pack, PhasePosition stored)
    {
        if (pack.Phases.Count == 0) return (null, null);
        var idx = pack.Phases.FindIndex(p => p.Id == stored.PhaseId);
        if (idx >= 0)
        {
            var span = Spans(pack).FirstOrDefault(s => idx >= s.First && idx <= s.Last);
            var repeat = span == null ? 1 : Math.Clamp(stored.Repeat, 1, span.Block.Count);
            return (stored with { PhaseIndex = idx, Repeat = repeat }, null);
        }
        var i = Math.Clamp(stored.PhaseIndex, 0, pack.Phases.Count - 1);
        var ph = pack.Phases[i];
        return (new PhasePosition(stored.Turn, 1, ph.Id, i),
            $"Phase \"{stored.PhaseId}\" no longer exists; continuing at \"{ph.Name}\"");
    }

    public static PhaseDef? Current(RulesPack pack, GameState s) =>
        s.Position == null ? null : pack.Phases.FirstOrDefault(p => p.Id == s.Position.PhaseId);

    public static string? StageText(RulesPack pack, PhasePosition p)
    {
        var idx = pack.Phases.FindIndex(x => x.Id == p.PhaseId);
        if (idx < 0) return null;
        var span = Spans(pack).FirstOrDefault(s => idx >= s.First && idx <= s.Last);
        if (span == null) return null;
        var label = string.IsNullOrWhiteSpace(span.Block.Label) ? "Stage" : span.Block.Label;
        return $"{label} {p.Repeat} of {span.Block.Count}";
    }

    public static SideDef? ActingSide(RulesPack pack, PhaseDef phase) =>
        phase.SideId == null ? null : pack.Sides.FirstOrDefault(x => x.Id == phase.SideId);

    public static IReadOnlyList<PieceInstance> UnitsFor(RulesPack pack, GameState s, PhaseDef phase, FormulaDef? formula, string? entryFilter)
    {
        var side = ActingSide(pack, phase);
        var fs = PieceFilter.Compile(side?.UnitFilter, s, pack);
        var ff = PieceFilter.Compile(formula?.AppliesTo, s, pack);
        var ef = PieceFilter.Compile(entryFilter, s, pack);
        return s.Pieces.Values.Where(p => fs(p) && ff(p) && ef(p)).ToList();
    }
}
