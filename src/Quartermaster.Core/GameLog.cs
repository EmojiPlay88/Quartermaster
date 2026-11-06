using System.Globalization;

namespace Quartermaster.Core;

/// <summary>One line of the human-readable game log. Kind is a <see cref="LogKind"/> value; unknown kinds are shown as system.</summary>
public sealed record LogEntry(long Seq, string Kind, string Text, string? Who, string? PieceId, string? MapId, DateTimeOffset At, int? Turn = null, string? Phase = null);

public static class LogKind
{
    public const string Dice = "dice";
    public const string Move = "move";
    public const string Place = "place";
    public const string Delete = "delete";
    public const string Formula = "formula";
    public const string Ledger = "ledger";
    public const string Note = "note";
    public const string Turn = "turn";
    public const string System = "system";
    public const string Calc = "calc";
}

public sealed record TurnState(int Turn, string? Phase);

/// <summary>Builds log entries for the reducer. Pure: all inputs come from the command.</summary>
static class LogBuilder
{
    static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static LogEntry Make(GameState s, string kind, string text, string? who, DateTimeOffset? at, string? pieceId = null, string? mapId = null) =>
        new(s.Log.Count + 1, kind, text, who, pieceId, mapId, at ?? DateTimeOffset.UnixEpoch, s.Position?.Turn, s.Position == null ? null : s.Turn.Phase);

    public static LogEntry Place(GameState s, AddPiece a) =>
        Make(s, LogKind.Place, a.LogText ?? $"piece {a.PieceId} placed", a.Who, a.At, a.PieceId, a.MapId);

    public static LogEntry Move(GameState s, MovePieces m, int movedCount, string? firstId) =>
        Make(s, LogKind.Move, m.LogText ?? $"{movedCount} piece(s) moved", m.Who, m.At, firstId, m.MapId);

    public static LogEntry Delete(GameState s, RemovePiece r, string? mapId) =>
        Make(s, LogKind.Delete, r.LogText ?? $"piece {r.PieceId} removed", r.Who, r.At, r.PieceId, mapId);

    public static LogEntry Dice(GameState s, RollDice d)
    {
        var results = d.Results ?? Array.Empty<int>();
        var expr = d.Expression ?? $"{d.Count}d{d.Sides}";
        string body;
        if (d.FaceTexts != null)
            body = $"{expr} = {string.Join(", ", d.FaceTexts)}";
        else
        {
            var detail = string.Join(" + ", results.Select(r => r.ToString(CultureInfo.InvariantCulture)));
            var total = results.Sum(r => (long)r) + d.Modifier;
            if (d.Modifier > 0) detail += " + " + d.Modifier.ToString(CultureInfo.InvariantCulture);
            else if (d.Modifier < 0) detail += " − " + (-(long)d.Modifier).ToString(CultureInfo.InvariantCulture);
            body = d.Count == 1 && d.Modifier == 0
                ? $"{expr} = {detail}"
                : $"{expr} = {detail} = {total.ToString(CultureInfo.InvariantCulture)}";
        }
        return Make(s, LogKind.Dice, $"{d.Purpose}: {body}", d.Who, d.At);
    }

    public static LogEntry Ledger(GameState s, LedgerDelta d)
    {
        var text = d.Working ?? $"{d.PropertyId} {Num(d.Before)} → {Num(d.After)}";
        if (d.Source == "manual" && !string.IsNullOrEmpty(d.Note)) text += $" (note: {d.Note})";
        if (!string.IsNullOrEmpty(d.Subject)) text = $"{d.Subject}: {text}";
        if (!string.IsNullOrEmpty(d.Tag)) text += $" ({d.Tag})";
        var kind = d.Tag != null ? LogKind.Calc : d.Source == "formula" ? LogKind.Formula : LogKind.Ledger;
        string? pieceId = LedgerKey.TryParse(d.Key, out var isPiece, out var id) && isPiece ? id : null;
        return Make(s, kind, text, d.Who, d.At, pieceId);
    }

    public static LogEntry Line(GameState s, string kind, string text, string? who, DateTimeOffset? at, string? pieceId = null) =>
        Make(s, kind, text, who, at, pieceId);

    public static LogEntry Notes(GameState s, SetNotes n) => Make(s, LogKind.Note, "notes updated", n.Who, n.At);

    public static LogEntry Turn(GameState s, SetTurn t) =>
        Make(s, LogKind.Turn, $"Turn {t.Turn}" + (string.IsNullOrEmpty(t.Phase) ? "" : " · " + t.Phase), t.Who, t.At);
}
