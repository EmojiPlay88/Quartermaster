using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// Turns commands issued by the UI into logged commands: stamps At/Who (the reducer stays pure) and writes readable LogText
/// from piece names and board-local grid locations. Never throws; on any problem the reducer's default text is used.
/// </summary>
static class CommandLog
{
    public const string LocalUser = "local";

    public static GameCommand Prepare(GameCommand c, GameState st, ModuleModel model)
    {
        try { return PrepareCore(c, st, model); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Stamp(c); }
    }

    /// <summary>Sets At/Who where the command carries them and they are not set yet.</summary>
    public static GameCommand Stamp(GameCommand c)
    {
        var at = DateTimeOffset.UtcNow;
        return c switch
        {
            AddPiece a => a with { At = a.At ?? at, Who = a.Who ?? LocalUser },
            MovePieces m => m with { At = m.At ?? at, Who = m.Who ?? LocalUser },
            RemovePiece r => r with { At = r.At ?? at, Who = r.Who ?? LocalUser },
            RollDice d => d with { At = d.At ?? at, Who = d.Who ?? LocalUser },
            LedgerDelta l => l with { At = l.At ?? at, Who = l.Who ?? LocalUser },
            SetNotes n => n with { At = n.At ?? at, Who = n.Who ?? LocalUser },
            SetTurn t => t with { At = t.At ?? at, Who = t.Who ?? LocalUser },
            SetPlayers sp => sp with { At = sp.At ?? at, Who = sp.Who ?? LocalUser },
            AdvancePhase a => a with { At = a.At ?? at, Who = a.Who ?? LocalUser },
            SetDifficulty d => d with { At = d.At ?? at, Who = d.Who ?? LocalUser },
            TickChecklist t => t with { At = t.At ?? at, Who = t.Who ?? LocalUser },
            AcknowledgeWarning w => w with { At = w.At ?? at, Who = w.Who ?? LocalUser },
            QueueRuns q => q with { At = q.At ?? at, Who = q.Who ?? LocalUser },
            ResolveRun r => r with { At = r.At ?? at, Who = r.Who ?? LocalUser },
            SteppedBack b => b with { At = b.At ?? at, Who = b.Who ?? LocalUser },
            HideAlert h => h with { At = h.At ?? at, Who = h.Who ?? LocalUser },
            AddLogLine l => l with { At = l.At ?? at, Who = l.Who ?? LocalUser },
            _ => c,
        };
    }

    static GameCommand PrepareCore(GameCommand c, GameState st, ModuleModel model)
    {
        c = Stamp(c);
        switch (c)
        {
            case MovePieces m when m.LogText == null && m.PieceIds.Count > 0:
            {
                var first = m.PieceIds.Select(id => st.Pieces.GetValueOrDefault(id)).FirstOrDefault(p => p != null);
                if (first == null) return c;
                var from = PieceInfo.Location(first, model);
                var to = Where(m.MapId, m.Pos, model);
                var who = m.PieceIds.Count == 1 ? Name(first) : $"{m.PieceIds.Count} pieces";
                var text = from != null && to != null ? $"{who} moved {from} → {to}" : to != null ? $"{who} moved → {to}" : $"{who} moved";
                return m with { LogText = text };
            }
            case RemovePiece r when r.LogText == null && st.Pieces.TryGetValue(r.PieceId, out var gone):
                return r with { LogText = $"{Name(gone)} removed" };
            case AddPiece a when a.LogText == null:
            {
                var name = Name(new PieceInstance { TypeString = a.TypeString });
                var loc = Where(a.MapId, a.Pos, model);
                return a with { LogText = loc != null ? $"{name} placed at {loc}" : $"{name} placed" };
            }
        }
        return c;
    }

    static string Name(PieceInstance p)
    {
        var n = PieceInfo.Name(p);
        return string.IsNullOrWhiteSpace(n) ? "piece" : n;
    }

    static string? Where(string mapId, PointD pos, ModuleModel model) =>
        PieceInfo.Location(new PieceInstance { MapId = mapId, Pos = pos }, model);
}
