using System.Globalization;

namespace Quartermaster.Core;

/// <summary>One line of the human-readable game log. Kind is a <see cref="LogKind"/> value; unknown kinds are shown as system.</summary>
/// <summary>SideId: the side the entry was explicitly made for (formula runs, stamped by the caller); null when not stamped.</summary>
/// <summary>FormulaId: the formula whose working a calc line shows (so <see cref="PlayerView"/> can hide working with hidden inputs), or
/// (B9 #55) whose draw a side-stamped System line reports (kept only for that side); null otherwise.</summary>
/// <summary>Error: the line reports a failure (a run that failed or was refused, a missing formula, an invalid filter); omitted when false.
/// <see cref="PlayerView"/> clears it on a line it redacts.</summary>
public sealed record LogEntry(long Seq, string Kind, string Text, string? Who, string? PieceId, string? MapId, DateTimeOffset At, int? Turn = null, string? Phase = null, string? SideId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? FormulaId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Error = false)
{
    /// <summary>The formula id on the one line that records an applied run (spec #71, read by <c>runs()</c>); null otherwise. Omitted when null.
    /// <see cref="PlayerView"/> clears it on a line it redacts.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AppliedRun { get; init; }

    /// <summary>The stage (<see cref="PhasePosition.Repeat"/>) the run was applied in; set only with <see cref="AppliedRun"/>. Omitted when null.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? Stage { get; init; }
}

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
    /// <summary>A player's chat message ("{name}: {text}"); public, never filtered by <see cref="PlayerView"/>.</summary>
    public const string Chat = "chat";

    /// <summary>
    /// <see cref="LogEntry.FormulaId"/> of a calc line that shows no formula working (a summary, a skipped trigger). A calc line with no
    /// formula id at all (made before formula ids were logged) may show working, so <see cref="PlayerView"/> hides its text.
    /// </summary>
    public const string NoWorking = "-";
}

public sealed record TurnState(int Turn, string? Phase);

/// <summary>Builds log entries for the reducer. Pure: all inputs come from the command.</summary>
static class LogBuilder
{
    static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    static LogEntry Make(GameState s, string kind, string text, string? who, DateTimeOffset? at, string? pieceId = null, string? mapId = null, string? sideId = null,
        string? formulaId = null, int? turn = null, string? phase = null, bool error = false) =>
        new(s.Log.Count + 1, kind, text, who, pieceId, mapId, at ?? DateTimeOffset.UnixEpoch, turn ?? s.Position?.Turn, phase ?? (s.Position == null ? null : s.Turn.Phase),
            string.IsNullOrWhiteSpace(sideId) ? null : sideId, string.IsNullOrWhiteSpace(formulaId) ? null : formulaId, error);

    public static LogEntry Place(GameState s, AddPiece a) =>
        Make(s, LogKind.Place, a.LogText ?? $"piece {a.PieceId} placed", a.Who, a.At, a.PieceId, a.MapId);

    public static LogEntry Move(GameState s, MovePieces m, int movedCount, string? firstId) =>
        Make(s, LogKind.Move, m.LogText ?? $"{movedCount} piece(s) moved", m.Who, m.At, firstId, m.MapId);

    public static LogEntry Delete(GameState s, RemovePiece r, string? mapId) =>
        Make(s, LogKind.Delete, r.LogText ?? $"piece {r.PieceId} removed", r.Who, r.At, r.PieceId, mapId, r.SideId);

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
        return Make(s, LogKind.Dice, $"{d.Purpose}: {body}", d.Who, d.At, d.PieceId, sideId: d.SideId, formulaId: d.FormulaId);
    }

    public static LogEntry Ledger(GameState s, LedgerDelta d)
    {
        var text = d.Working ?? $"{d.PropertyId} {Num(d.Before)} → {Num(d.After)}";
        if (d.Source == "manual" && !string.IsNullOrEmpty(d.Note)) text += $" (note: {d.Note})";
        if (!string.IsNullOrEmpty(d.Subject)) text = $"{d.Subject}: {text}";
        if (!string.IsNullOrEmpty(d.Tag)) text += $" ({d.Tag})";
        var kind = d.Tag != null ? LogKind.Calc : d.Source == "formula" ? LogKind.Formula : LogKind.Ledger;
        string? pieceId = LedgerKey.TryParse(d.Key, out var isPiece, out var id) && isPiece ? id : d.PieceId;
        return Stamp(s, Make(s, kind, text, d.Who, d.At, pieceId, sideId: d.SideId), d.AppliedRun);
    }

    public static LogEntry Line(GameState s, string kind, string text, string? who, DateTimeOffset? at, string? pieceId = null, string? sideId = null,
        string? formulaId = null, bool error = false, string? appliedRun = null) =>
        Stamp(s, Make(s, kind, text, who, at, pieceId, sideId: sideId, formulaId: formulaId, error: error), appliedRun);

    /// <summary>The run stamp (<see cref="LogEntry.AppliedRun"/>, with the current stage) when the command carries one.</summary>
    static LogEntry Stamp(GameState s, LogEntry e, string? appliedRun) =>
        string.IsNullOrWhiteSpace(appliedRun) ? e : e with { AppliedRun = appliedRun, Stage = s.Position?.Repeat };

    /// <summary>A line stamped with the given turn and phase instead of the game's current ones (a skipped phase's own place).</summary>
    public static LogEntry LineAt(GameState s, string kind, string text, string? who, DateTimeOffset? at, int turn, string? phase) =>
        Make(s, kind, text, who, at, turn: turn, phase: phase);

    public static LogEntry Notes(GameState s, SetNotes n) => Make(s, LogKind.Note, "notes updated", n.Who, n.At);

    public static LogEntry Turn(GameState s, SetTurn t) =>
        Make(s, LogKind.Turn, $"Turn {t.Turn}" + (string.IsNullOrEmpty(t.Phase) ? "" : " · " + t.Phase), t.Who, t.At);

    public static string PlayersText(IReadOnlyList<PlayerDef> players) =>
        "Players: " + (players.Count == 0 ? "solo" : string.Join("; ", players.Select(p => $"{p.Name} ({string.Join(", ", p.Sides)})")));

    public static LogEntry SteppedBack(GameState s, SteppedBack c)
    {
        var text = $"Stepped back to {c.Target}. Undid {c.ActionsUndone} {(c.ActionsUndone == 1 ? "action" : "actions")}";
        if (c.DiceUndone > 0) text += $", {c.DiceUndone} dice {(c.DiceUndone == 1 ? "roll" : "rolls")}";
        return Make(s, LogKind.System, text + ".", c.Who, c.At);
    }
}
