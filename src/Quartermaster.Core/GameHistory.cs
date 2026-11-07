namespace Quartermaster.Core;

/// <summary>One undoable action: <see cref="Start"/> is the log index of its first command.</summary>
public sealed record ActionMark(int Start, string Label);

/// <summary>An action removed by a rewind, kept so it can be redone.</summary>
public sealed record UndoneAction(string Label, IReadOnlyList<GameCommand> Commands);

public static class ActionLabels
{
    public static string For(GameCommand first, int commandCount)
    {
        switch (first)
        {
            case MovePieces m:
                if (m.LogText is { } t && t.IndexOf(" moved", StringComparison.Ordinal) is var i and > 0)
                    return "move of " + t[..i];
                return $"move of {m.PieceIds.Count} pieces";
            case RollDice r: return "roll " + r.Purpose;
            case LedgerDelta l: return l.PropertyId + " change";
            case AdvancePhase: return "Next Phase";
            case AddPiece: return "add piece";
            case RemovePiece: return "remove piece";
            case SetNotes: return "notes edit";
            case SetTurn: return "turn change";
            default: return SplitWords(first.GetType().Name);
        }
    }

    static string SplitWords(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
