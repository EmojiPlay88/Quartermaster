using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>Turn-sequence position math over a rules pack. Never throws on bad pack data.</summary>
public static class Sequencer
{
    /// <summary>A block the sequencer uses, over phase indexes First..Last; Depth 0 = an outer (or the only) block, 1 = inside another.</summary>
    private sealed record Span(PhaseBlock Block, int First, int Last, int Depth);

    /// <summary>
    /// The blocks in use, in pack order. A block is used when its phases exist in order, its count is not negative (0 = until a player
    /// stops), and it is disjoint from every block already used or strictly nested with it, one level only. A partial overlap, the same
    /// phases as a used block, or a third level is ignored (first valid in pack order wins). O(blocks²).
    /// </summary>
    private static List<Span> Spans(RulesPack pack)
    {
        var acc = new List<(PhaseBlock B, int F, int L)>();
        var isInner = new List<bool>();
        var hasInner = new List<bool>();
        var touched = new List<int>();
        foreach (var b in pack.Blocks)
        {
            if (b.Count < 0) continue;
            var f = pack.Phases.FindIndex(p => p.Id == b.FirstPhaseId);
            var l = pack.Phases.FindIndex(p => p.Id == b.LastPhaseId);
            if (f < 0 || l < 0 || f > l) continue;
            bool fits = true, inside = false, around = false;
            touched.Clear();
            for (var i = 0; i < acc.Count && fits; i++)
            {
                var r = acc[i];
                if (l < r.F || f > r.L) continue;
                if (f == r.F && l == r.L) fits = false;
                else if (r.F <= f && l <= r.L) { fits = !isInner[i]; inside = true; touched.Add(i); }
                else if (f <= r.F && r.L <= l) { fits = !hasInner[i]; around = true; touched.Add(i); }
                else fits = false;
            }
            if (!fits || (inside && around)) continue;
            foreach (var i in touched)
                if (inside) hasInner[i] = true;
                else isInner[i] = true;
            acc.Add((b, f, l));
            isInner.Add(inside);
            hasInner.Add(around);
        }
        return acc.Select((r, i) => new Span(r.B, r.F, r.L, isInner[i] ? 1 : 0)).ToList();
    }

    private static Span? SpanAt(List<Span> spans, int idx, int depth) =>
        spans.FirstOrDefault(s => s.Depth == depth && idx >= s.First && idx <= s.Last);

    /// <summary>A repeat number kept inside a block: at least 1, at most the count when the block has one (count 0 has no maximum).</summary>
    private static int Within(int n, PhaseBlock b) => b.Count > 0 ? Math.Clamp(n, 1, b.Count) : Math.Max(n, 1);

    /// <summary>The <see cref="PhasePosition.Inner"/> a phase is entered with: 1 in an inner block, else 0.</summary>
    private static int EnterInner(List<Span> spans, int idx) => SpanAt(spans, idx, 1) == null ? 0 : 1;

    public static IReadOnlyList<PhaseBlock> ValidBlocks(RulesPack pack) => Spans(pack).Select(s => s.Block).ToList();

    /// <summary>The outer (or only) block holding the phase at <paramref name="phaseIndex"/>; null outside blocks.</summary>
    public static PhaseBlock? OuterBlock(RulesPack pack, int phaseIndex) => SpanAt(Spans(pack), phaseIndex, 0)?.Block;

    /// <summary>The block inside another block that holds the phase at <paramref name="phaseIndex"/>; null when there is none.</summary>
    public static PhaseBlock? InnerBlock(RulesPack pack, int phaseIndex) => SpanAt(Spans(pack), phaseIndex, 1)?.Block;

    public static PhasePosition? First(RulesPack pack) =>
        pack.Phases.Count == 0 ? null : new PhasePosition(1, 1, pack.Phases[0].Id, 0) { Inner = EnterInner(Spans(pack), 0) };

    /// <summary>The position after <paramref name="cur"/>, choosing Continue at a block that repeats until a player stops.</summary>
    public static PhasePosition? Next(RulesPack pack, PhasePosition cur) => Next(pack, cur, false);

    /// <summary>
    /// The position after <paramref name="cur"/>. <paramref name="repeat"/> is the player's choice at the last phase of a block that
    /// repeats until a player stops (see <see cref="RepeatChoice"/>): true plays it again, false continues; fixed blocks ignore it.
    /// </summary>
    public static PhasePosition? Next(RulesPack pack, PhasePosition cur, bool repeat)
    {
        if (pack.Phases.Count == 0) return null;
        var idx = pack.Phases.FindIndex(p => p.Id == cur.PhaseId);
        if (idx < 0) idx = Math.Clamp(cur.PhaseIndex, 0, pack.Phases.Count - 1);
        var spans = Spans(pack);
        var outer = SpanAt(spans, idx, 0);
        var inner = SpanAt(spans, idx, 1);
        // 1. The last phase of an inner block: open-ended repeats on the choice (using it up), fixed while Inner < Count.
        if (inner != null && idx == inner.Last)
        {
            var at = Math.Max(cur.Inner, 1);
            if (inner.Block.Count == 0 ? repeat : at < inner.Block.Count)
                return new PhasePosition(cur.Turn, cur.Repeat, pack.Phases[inner.First].Id, inner.First) { Inner = at + 1 };
        }
        // 2. The last phase of the outer block: open-ended repeats on the (unused) choice, fixed while Repeat < Count.
        if (outer != null && idx == outer.Last && (outer.Block.Count == 0 ? repeat : cur.Repeat < outer.Block.Count))
            return new PhasePosition(cur.Turn, cur.Repeat + 1, pack.Phases[outer.First].Id, outer.First) { Inner = EnterInner(spans, outer.First) };
        // 3. The next phase. Moving within a block keeps its repeat; leaving or entering one is repeat 1 (Inner 0 outside an inner block).
        var n = idx + 1;
        if (n >= pack.Phases.Count) return new PhasePosition(cur.Turn + 1, 1, pack.Phases[0].Id, 0) { Inner = EnterInner(spans, 0) };
        var nextOuter = SpanAt(spans, n, 0);
        var nextInner = SpanAt(spans, n, 1);
        var repeatN = outer != null && ReferenceEquals(nextOuter, outer) ? Within(cur.Repeat, outer.Block) : 1;
        var innerN = nextInner == null ? 0 : inner != null && ReferenceEquals(nextInner, inner) ? Within(cur.Inner, inner.Block) : 1;
        return new PhasePosition(cur.Turn, repeatN, pack.Phases[n].Id, n) { Inner = innerN };
    }

    /// <summary>
    /// The block whose repeat the player chooses at <paramref name="pos"/> (play another or continue): the inner block ending here when
    /// it repeats until a player stops; else (no inner block ends here, or a fixed one has played its count) the outer block ending here
    /// when it repeats until a player stops; else null.
    /// </summary>
    public static PhaseBlock? RepeatChoice(RulesPack pack, PhasePosition pos)
    {
        var idx = pack.Phases.FindIndex(p => p.Id == pos.PhaseId);
        if (idx < 0) return null;
        var spans = Spans(pack);
        if (SpanAt(spans, idx, 1) is { } inner && inner.Last == idx)
        {
            if (inner.Block.Count == 0) return inner.Block;
            if (Math.Max(pos.Inner, 1) < inner.Block.Count) return null;
        }
        return SpanAt(spans, idx, 0) is { } outer && outer.Last == idx && outer.Block.Count == 0 ? outer.Block : null;
    }

    public static (PhasePosition? Position, string? Notice) Resolve(RulesPack pack, PhasePosition stored)
    {
        if (pack.Phases.Count == 0) return (null, null);
        var spans = Spans(pack);
        var idx = pack.Phases.FindIndex(p => p.Id == stored.PhaseId);
        if (idx >= 0)
        {
            var outer = SpanAt(spans, idx, 0);
            var inner = SpanAt(spans, idx, 1);
            var repeat = outer == null ? 1 : Within(stored.Repeat, outer.Block);
            var innerN = inner == null ? 0 : Within(stored.Inner, inner.Block);
            return (stored with { PhaseIndex = idx, Repeat = repeat, Inner = innerN }, null);
        }
        var i = Math.Clamp(stored.PhaseIndex, 0, pack.Phases.Count - 1);
        var ph = pack.Phases[i];
        return (new PhasePosition(stored.Turn, 1, ph.Id, i) { Inner = EnterInner(spans, i) },
            $"Phase \"{stored.PhaseId}\" no longer exists; continuing at \"{ph.Name}\"");
    }

    public static PhaseDef? Current(RulesPack pack, GameState s) =>
        s.Position == null ? null : pack.Phases.FirstOrDefault(p => p.Id == s.Position.PhaseId);

    /// <summary>
    /// The block text of a position: per level <c>{label} {n} of {count}</c> (fixed) or <c>{label} {n}</c> (until a player stops), a blank
    /// label reading "Stage"; the outer then the inner level, joined with ", ". Null outside blocks.
    /// </summary>
    public static string? StageText(RulesPack pack, PhasePosition p)
    {
        var idx = pack.Phases.FindIndex(x => x.Id == p.PhaseId);
        if (idx < 0) return null;
        var spans = Spans(pack);
        if (SpanAt(spans, idx, 0) is not { } outer) return null;
        var text = Level(outer.Block, p.Repeat);
        return SpanAt(spans, idx, 1) is { } inner ? $"{text}, {Level(inner.Block, Math.Max(p.Inner, 1))}" : text;

        static string Level(PhaseBlock b, int n)
        {
            var label = string.IsNullOrWhiteSpace(b.Label) ? "Stage" : b.Label;
            return b.Count > 0 ? $"{label} {n} of {b.Count}" : $"{label} {n}";
        }
    }

    public static SideDef? ActingSide(RulesPack pack, PhaseDef phase) =>
        phase.SideId == null ? null : pack.Sides.FirstOrDefault(x => x.Id == phase.SideId);

    /// <summary>
    /// True when a player holding <paramref name="sides"/> may act in <paramref name="phase"/> (B5 #7): solo (null), a phase without a
    /// known side, the acting side held, or a held side among the phase's known <see cref="PhaseDef.Reactors"/> (unknown ids are ignored).
    /// Ticking, acknowledging and Next Phase stay the acting side's; this widens only unit-less runs and global writes.
    /// </summary>
    public static bool MayAct(RulesPack pack, PhaseDef phase, IReadOnlySet<string>? sides)
    {
        if (sides == null || ActingSide(pack, phase) is not { } acting) return true;
        if (sides.Contains(acting.Id)) return true;
        return phase.Reactors?.Any(r => sides.Contains(r) && pack.Sides.Any(x => x.Id == r)) == true;
    }

    /// <param name="model">For grid locations, so the filters can use the hex aggregates (hexsum() ...); without it they are not available.</param>
    public static IReadOnlyList<PieceInstance> UnitsFor(RulesPack pack, GameState s, PhaseDef phase, FormulaDef? formula, string? entryFilter,
        ModuleModel? model = null)
    {
        var side = ActingSide(pack, phase);
        var fs = PieceFilter.Compile(side?.UnitFilter, s, pack, model);
        var ff = PieceFilter.Compile(formula?.AppliesTo, s, pack, model);
        var ef = PieceFilter.Compile(entryFilter, s, pack, model);
        return s.Pieces.Values.Where(p => fs(p) && ff(p) && ef(p)).ToList();
    }

    /// <summary>
    /// The pieces an OnMove entry fires for, by its <see cref="MoveEntry.FiresFor"/> (callers keep those that moved, as with
    /// <see cref="UnitsFor"/>). Acting: <see cref="UnitsFor"/>. Other: the complement of Acting, the pieces the phase side's unit filter does not match
    /// (a piece of no side counts as other; Acting and Other never overlap). Any: every piece. Formula applies-to and the entry filter apply in all modes; a phase
    /// without a (known) side behaves as Acting in every mode.
    /// </summary>
    public static IReadOnlyList<PieceInstance> MovedUnitsFor(RulesPack pack, GameState s, PhaseDef phase, FormulaDef? formula, MoveEntry e,
        ModuleModel? model = null)
    {
        var side = ActingSide(pack, phase);
        if (side == null || e.FiresFor == MoveSide.Acting) return UnitsFor(pack, s, phase, formula, e.UnitFilter, model);
        var ff = PieceFilter.Compile(formula?.AppliesTo, s, pack, model);
        var ef = PieceFilter.Compile(e.UnitFilter, s, pack, model);
        var units = s.Pieces.Values.Where(p => ff(p) && ef(p));
        if (e.FiresFor == MoveSide.Other)
        {
            var acting = PieceFilter.Compile(side.UnitFilter, s, pack, model);
            units = units.Where(p => !acting(p));
        }
        return units.ToList();
    }
}
