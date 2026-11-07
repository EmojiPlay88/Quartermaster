using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

/// <summary>One thing an alert points at: a piece, a holder, or neither (text only).</summary>
public sealed record AlertMatch(string? PieceId, string? HolderId, string Text);

/// <summary>An alert that currently has at least one match.</summary>
public sealed record AlertHit(string Id, string Name, AlertSeverity Severity, string Message, IReadOnlyList<AlertMatch> Matches, string? Notes);

/// <summary>Built-in and user-defined alerts over the game state. Never throws on bad pack data or expressions.</summary>
public static class AlertEngine
{
    private const string Token = "{unit}";
    private const int TooltipMatches = 10;

    public const string InputId = "builtin:input";
    public const string PendingId = "builtin:pending";
    public const string WarningId = "builtin:warning";
    public static string NegativeId(string propertyId) => "builtin:neg:" + propertyId;

    /// <summary>Hits with at least one match, Critical first, then by name. Hidden alerts are left out.</summary>
    public static IReadOnlyList<AlertHit> Evaluate(GameState s, RulesPack? pack, ModuleModel? model, PhaseDef? current, string? actingSideId)
    {
        var hits = new List<AlertHit>();
        var hidden = s.HiddenAlerts ?? new();
        void Add(AlertHit h) { if (h.Matches.Count > 0 && !hidden.Contains(h.Id)) hits.Add(h); }

        Guarded(() => Negative(s, pack, hits, hidden));
        Guarded(() => Pending(s, hits, hidden));
        if (pack != null && current != null)
            Guarded(() => Warnings(s, pack, current, Add));
        if (pack != null)
            foreach (var a in pack.Alerts ?? new())
            {
                if (a == null || hidden.Contains(a.Id)) continue;
                Guarded(() => { var h = User(s, pack, a, actingSideId); if (h != null) Add(h); });
            }

        return hits
            .OrderByDescending(h => h.Severity)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Message line, the first 10 match texts, then "… and N more".</summary>
    public static string Tooltip(AlertHit h)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(h.Message)) lines.Add(h.Message);
        lines.AddRange(h.Matches.Take(TooltipMatches).Select(m => m.Text));
        if (h.Matches.Count > TooltipMatches) lines.Add($"… and {h.Matches.Count - TooltipMatches} more");
        return string.Join("\n", lines);
    }

    private static void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private static void Negative(GameState s, RulesPack? pack, List<AlertHit> hits, List<string> hidden)
    {
        var byProp = new Dictionary<string, List<AlertMatch>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (key, inner) in s.Ledgers)
        {
            if (!LedgerKey.TryParse(key, out var isPiece, out var id)) continue;
            AlertMatch? who = null;
            foreach (var (prop, v) in inner)
            {
                if (!(v < 0)) continue;
                if (who == null)
                {
                    if (isPiece)
                    {
                        if (!s.Pieces.TryGetValue(id, out var p)) break;
                        var n = PieceInfo.Name(p);
                        who = new AlertMatch(id, null, string.IsNullOrEmpty(n) ? id : n);
                    }
                    else
                    {
                        var h = pack?.ById<HolderDef>(id);
                        if (h == null && id != HolderDef.GlobalId) break;
                        who = new AlertMatch(null, id, h?.Name is { Length: > 0 } hn ? hn : id == HolderDef.GlobalId ? "Global" : id);
                    }
                }
                if (!byProp.TryGetValue(prop, out var list)) { byProp[prop] = list = new(); order.Add(prop); }
                list.Add(who);
            }
        }
        foreach (var prop in order)
        {
            var id = NegativeId(prop);
            if (hidden.Contains(id)) continue;
            var name = pack?.ById<PropertyDef>(prop)?.Name ?? prop;
            hits.Add(new AlertHit(id, $"{name} below zero", AlertSeverity.Critical, $"{name} is below zero.", byProp[prop], null));
        }
    }

    private static void Pending(GameState s, List<AlertHit> hits, List<string> hidden)
    {
        var input = new List<AlertMatch>();
        var open = new List<AlertMatch>();
        foreach (var r in s.Pending ?? new())
        {
            if (r == null) continue;
            var target = r.NeedsInput ? input : open;
            var units = (r.Units ?? Array.Empty<PendingUnit>()).Where(u => u != null && !u.Applied).ToList();
            if (units.Count == 0 && r.NeedsInput) target.Add(new AlertMatch(null, null, r.FormulaName));
            foreach (var u in units) target.Add(new AlertMatch(u.PieceId, null, u.Label));
        }
        if (input.Count > 0 && !hidden.Contains(InputId))
            hits.Add(new AlertHit(InputId, "Formula waiting for input", AlertSeverity.Warning, "A formula needs your input before it can run.", input, null));
        if (open.Count > 0 && !hidden.Contains(PendingId))
            hits.Add(new AlertHit(PendingId, "Formula not applied", AlertSeverity.Info, "A formula has not been applied to these units yet.", open, null));
    }

    private static void Warnings(GameState s, RulesPack pack, PhaseDef phase, Action<AlertHit> add)
    {
        var matches = new List<AlertMatch>();
        foreach (var w in Decisions.Warnings(pack, s, phase))
        {
            if (!w.IsDecision || w.Acknowledged) continue;
            for (var i = 0; i < w.PieceIds.Count; i++)
                matches.Add(new AlertMatch(w.PieceIds[i], null, i < w.UnitTexts.Count ? w.UnitTexts[i] : w.Message));
        }
        add(new AlertHit(WarningId, "Unacknowledged warning", AlertSeverity.Warning, "The current phase has warnings you have not acknowledged.", matches, null));
    }

    private static AlertHit? User(GameState s, RulesPack pack, AlertDef a, string? actingSideId)
    {
        if (string.IsNullOrWhiteSpace(a.Condition)) return null;
        string? sideFilter = null;
        if (a.SideId != null)
        {
            var side = pack.ById<SideDef>(a.SideId);
            if (side == null) return null;
            if (pack.Phases.Count > 0 && actingSideId != a.SideId) return null;
            sideFilter = side.UnitFilter;
        }
        var inSide = PieceFilter.Compile(sideFilter, s, pack);
        var cond = PieceFilter.Compile(a.Condition, s, pack);
        var msg = a.Message ?? "";
        var matches = new List<AlertMatch>();
        foreach (var p in s.Pieces.Values)
        {
            if (!inSide(p) || !cond(p)) continue;
            var name = PieceInfo.Name(p);
            if (string.IsNullOrEmpty(name)) name = p.Id;
            matches.Add(new AlertMatch(p.Id, null, msg.Contains(Token) ? msg.Replace(Token, name) : name));
        }
        return new AlertHit(a.Id, a.Name, a.Severity, msg.Contains(Token) ? a.Name : msg, matches, a.Notes);
    }
}
