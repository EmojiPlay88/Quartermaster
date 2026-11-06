namespace Quartermaster.Vassal;

/// <summary>
/// A piece type with prototypes spliced in. <see cref="StateIndex"/>[i] is the index into the unexpanded state list
/// for expanded trait i, or -1 for traits that came from a prototype (VASSAL stores no state for them).
/// </summary>
public sealed record ExpandedPiece(PieceType Type, IReadOnlyList<int> StateIndex);

public sealed class PrototypeExpander(IReadOnlyDictionary<string, PieceType> prototypes, DiagnosticLog log)
{
    private const int MaxDepth = 32;

    public ExpandedPiece Expand(PieceType t)
    {
        var traits = new List<TraitDef>();
        var index = new List<int>();
        var stack = new List<string>();
        for (var i = 0; i < t.Traits.Count; i++)
        {
            var tr = t.Traits[i];
            if (tr is PrototypeRef pr)
            {
                var start = traits.Count;
                if (Splice(pr, traits, stack))
                {
                    for (var k = start; k < traits.Count; k++) index.Add(-1);
                }
                else
                {
                    traits.Add(new InertTrait(pr.RawType));
                    index.Add(i);
                }
            }
            else
            {
                traits.Add(tr);
                index.Add(i);
            }
        }
        return new ExpandedPiece(new PieceType(traits), index);
    }

    /// <summary>Appends the named prototype's traits (minus its BasicPiece) to <paramref name="outList"/>; false when it cannot be expanded.</summary>
    private bool Splice(PrototypeRef pr, List<TraitDef> outList, List<string> stack)
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
        foreach (var tr in proto.Traits)
        {
            if (tr is BasicPieceDef) continue;
            if (tr is PrototypeRef inner)
            {
                if (!Splice(inner, outList, stack)) outList.Add(new InertTrait(inner.RawType));
            }
            else outList.Add(tr);
        }
        stack.RemoveAt(stack.Count - 1);
        return true;
    }
}
