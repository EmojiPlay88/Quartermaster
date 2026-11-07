using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// Turns commands issued by the UI into logged commands: stamps At/Who (the reducer stays pure) and writes readable LogText
/// from piece names and board-local grid locations. Never throws; on any problem the reducer's default text is used.
/// </summary>
public static class CommandLog
{
    public const string LocalUser = "local";

    public static GameCommand Prepare(GameCommand c, GameState st, ModuleSession session)
    {
        try { return PrepareCore(c, st, session.Model, session.Rules); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Stamp(c); }
    }

    /// <summary>Sets At/Who where the command carries them and they are not set yet (Who defaults to <see cref="LocalUser"/>, At to now).</summary>
    public static GameCommand Stamp(GameCommand c, string? who = null, DateTimeOffset? when = null)
    {
        var at = when ?? DateTimeOffset.UtcNow;
        var w = who ?? LocalUser;
        return c switch
        {
            AddPiece a => a with { At = a.At ?? at, Who = a.Who ?? w },
            MovePieces m => m with { At = m.At ?? at, Who = m.Who ?? w },
            RemovePiece r => r with { At = r.At ?? at, Who = r.Who ?? w },
            RollDice d => d with { At = d.At ?? at, Who = d.Who ?? w },
            LedgerDelta l => l with { At = l.At ?? at, Who = l.Who ?? w },
            SetNotes n => n with { At = n.At ?? at, Who = n.Who ?? w },
            SetTurn t => t with { At = t.At ?? at, Who = t.Who ?? w },
            SetPlayers sp => sp with { At = sp.At ?? at, Who = sp.Who ?? w },
            AdvancePhase a => a with { At = a.At ?? at, Who = a.Who ?? w },
            SetDifficulty d => d with { At = d.At ?? at, Who = d.Who ?? w },
            TickChecklist t => t with { At = t.At ?? at, Who = t.Who ?? w },
            AcknowledgeWarning aw => aw with { At = aw.At ?? at, Who = aw.Who ?? w },
            QueueRuns q => q with { At = q.At ?? at, Who = q.Who ?? w },
            ResolveRun r => r with { At = r.At ?? at, Who = r.Who ?? w },
            SteppedBack b => b with { At = b.At ?? at, Who = b.Who ?? w },
            HideAlert h => h with { At = h.At ?? at, Who = h.Who ?? w },
            AddLogLine l => l with { At = l.At ?? at, Who = l.Who ?? w },
            _ => c,
        };
    }

    static GameCommand PrepareCore(GameCommand c, GameState st, ModuleModel model, RulesPack? rules)
    {
        c = Stamp(c);
        switch (c)
        {
            case MovePieces m when m.LogText == null && m.PieceIds.Count > 0:
                return MoveLogText.For(m, st, model, rules) is { } text ? m with { LogText = text } : c;
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
