// Ported from VASSAL PrototypeDefinition.java / PieceCloner.java (prototype expansion and starting state) (LGPL-2.1).
namespace Quartermaster.Vassal;

/// <summary>
/// A piece type with prototypes spliced in. <see cref="StateIndex"/>[i] is the index into the unexpanded state list
/// for expanded trait i, or -1 for traits that came from a prototype. <see cref="DerivedState"/>[i] is the starting state
/// of such a trait taken from the prototype definition's own state ("" when absent, and for non-derived traits).
/// </summary>
public sealed record ExpandedPiece(PieceType Type, IReadOnlyList<int> StateIndex, IReadOnlyList<string>? DerivedState = null);

public sealed class PrototypeExpander(IReadOnlyDictionary<string, PieceType> prototypes, DiagnosticLog log,
    IReadOnlyDictionary<string, string>? prototypeStates = null)
{
    private const int MaxDepth = 32;

    public ExpandedPiece Expand(PieceType t)
    {
        var traits = new List<TraitDef>();
        var index = new List<int>();
        var derived = new List<string>();
        var stack = new List<string>();
        for (var i = 0; i < t.Traits.Count; i++)
        {
            var tr = t.Traits[i];
            if (tr is PrototypeRef pr)
            {
                var start = traits.Count;
                var st = new List<string>();
                if (Splice(pr, traits, st, stack))
                {
                    for (var k = start; k < traits.Count; k++) { index.Add(-1); derived.Add(st[k - start]); }
                }
                else
                {
                    traits.Add(new InertTrait(pr.RawType));
                    index.Add(i);
                    derived.Add("");
                }
            }
            else
            {
                traits.Add(tr);
                index.Add(i);
                derived.Add("");
            }
        }
        return new ExpandedPiece(new PieceType(traits), index, derived);
    }

    /// <summary>
    /// Expands the prototypes of an encoded piece type, as VASSAL's PieceCloner does when a piece is made from a slot.
    /// Returns the re-encoded type; <paramref name="expandedState"/> is the matching state chain (unexpanded traits keep their
    /// state, prototype-derived traits take the prototype definition's own starting state, as VASSAL PrototypeDefinition.getPiece does). A type without prototype traits is returned unchanged with its state.
    /// </summary>
    public string ExpandTypeAndState(string type, string state, out string expandedState)
    {
        var parsed = PieceTypeParser.Parse(type, log, null);
        if (!parsed.Traits.Any(t => t is PrototypeRef))
        {
            expandedState = state;
            return type;
        }
        var e = Expand(parsed);
        var states = PieceTypeParser.SplitState(state, parsed.Traits.Count);
        expandedState = PieceTypeParser.EncodeState(e.StateIndex.Select((i, j) => i >= 0 ? states[i] : e.DerivedState![j]).ToList());
        return PieceTypeParser.EncodeType(e.Type.Traits);
    }

    /// <summary>Appends the named prototype's traits (minus its BasicPiece) to <paramref name="outList"/>; false when it cannot be expanded.</summary>
    private bool Splice(PrototypeRef pr, List<TraitDef> outList, List<string> outState, List<string> stack)
    {
        var name = pr.PrototypeName;
        if (stack.Count >= MaxDepth)
        {
            log.AddOnce("trait:protodepth:" + name, DiagnosticSeverity.Warning, "trait", $"Prototype nesting too deep at '{name}'.", name);
            return false;
        }
        if (stack.Contains(name))
        {
            log.AddOnce("trait:protocycle:" + name, DiagnosticSeverity.Warning, "trait", $"Prototype '{name}' refers to itself.", name);
            return false;
        }
        if (!prototypes.TryGetValue(name, out var proto))
        {
            log.AddOnce("trait:protomissing:" + name, DiagnosticSeverity.Warning, "trait", $"Prototype '{name}' not found.", name);
            return false;
        }

        stack.Add(name);
        var protoState = prototypeStates != null && prototypeStates.TryGetValue(name, out var ps)
            ? PieceTypeParser.SplitState(ps, proto.Traits.Count)
            : null;
        for (var k = 0; k < proto.Traits.Count; k++)
        {
            var tr = proto.Traits[k];
            if (tr is BasicPieceDef) continue;
            var own = protoState != null ? protoState[k] : "";
            if (tr is PrototypeRef inner)
            {
                if (!Splice(inner, outList, outState, stack)) { outList.Add(new InertTrait(inner.RawType)); outState.Add(own); }
            }
            else { outList.Add(tr); outState.Add(own); }
        }
        stack.RemoveAt(stack.Count - 1);
        return true;
    }
}
