using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>One chip of the alert bar.</summary>
public sealed record AlertChip(string Id, string Label, AlertSeverity Severity, string Tooltip, int MatchCount);

/// <summary>UI-free logic of the alert bar: evaluates the alerts for what the viewer may see, builds the chips and cycles through matches.</summary>
public sealed class AlertBarModel(ModuleSession s)
{
    public const string GoToNextMatchText = "Go to next match";
    public const string HideUntilNextTurnText = "Hide this alert until next turn";
    public const string NoticeTip = "Click to open the turn and replay it.";

    private readonly Dictionary<string, int> _cycle = new();
    private IReadOnlyList<AlertHit> _hits = Array.Empty<AlertHit>();

    /// <summary>The hits of the last <see cref="Recompute"/> (Critical first).</summary>
    public IReadOnlyList<AlertHit> Hits => _hits;

    /// <summary>Evaluates the alerts on the viewer's state; <paramref name="hiddenThisTurn"/> ids are left out on top of the game's own hidden list.</summary>
    public IReadOnlyList<AlertChip> Recompute(PhaseDef? current, string? actingSideId, ISet<string> hiddenThisTurn)
    {
        if (s.Game is not { } game) { _hits = Array.Empty<AlertHit>(); return Array.Empty<AlertChip>(); }
        _hits = AlertEngine.Evaluate(s.View ?? game.State, s.Rules, s.Model, current, actingSideId)
            .Where(h => !hiddenThisTurn.Contains(h.Id)).ToList();
        var visibleOnly = s.ViewFiltered;
        return _hits.Select(h => new AlertChip(h.Id, Label(h), h.Severity, Tooltip(h, visibleOnly), h.Matches.Count)).ToList();
    }

    /// <summary>The next match that can be shown (a unit or a holder) for the alert, cycling and wrapping; null when none can.</summary>
    public AlertMatch? NextTarget(string alertId)
    {
        var hit = _hits.FirstOrDefault(h => h.Id == alertId);
        if (hit == null) return null;
        var targets = hit.Matches.Where(m => m.PieceId != null || m.HolderId != null).ToList();
        if (targets.Count == 0) return null;
        var next = _cycle.TryGetValue(alertId, out var at) ? (at + 1) % targets.Count : 0;
        _cycle[alertId] = next;
        return targets[next];
    }

    /// <summary>The piece (else holder) id of <see cref="NextTarget"/>.</summary>
    public string? NextMatch(string alertId) => NextTarget(alertId) is { } m ? m.PieceId ?? m.HolderId : null;

    /// <summary>Forgets the cycle positions and hits (game closed).</summary>
    public void Reset() { _cycle.Clear(); _hits = Array.Empty<AlertHit>(); }

    public static string Label(AlertHit h) => h.Matches.Count > 1 ? $"{h.Name} ({h.Matches.Count})" : h.Name;

    /// <summary>Chip tooltip: message and matches, the visible-only remark, the alert's notes.</summary>
    public static string Tooltip(AlertHit h, bool visibleOnly) =>
        AlertEngine.Tooltip(h) + (visibleOnly ? "\n(" + ModuleSession.VisibleOnlyText + ")" : "")
        + (string.IsNullOrWhiteSpace(h.Notes) ? "" : "\n\n" + h.Notes);

    /// <summary>Text of the chip standing for the alerts that did not fit.</summary>
    public static string OverflowText(int n) => "+" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Severity accent colour of a chip.</summary>
    public static (byte R, byte G, byte B) Accent(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => (200, 40, 40),
        AlertSeverity.Warning => (214, 140, 0),
        _ => (40, 100, 200),
    };
}
