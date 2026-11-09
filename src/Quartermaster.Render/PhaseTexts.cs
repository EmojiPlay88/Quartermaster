using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>One item of the tracker strip. <paramref name="State"/> is null for a plain label ("Turn n", the stage text, "Before turn 1").</summary>
public sealed record StripChip(string Text, ChipState? State, string? Tooltip);

/// <summary>One top-level line of the phase panel's warning tree.</summary>
public sealed record WarningLine(string Text, int Index, bool CanAck);

/// <summary>What the phase panel shows for a <see cref="PhaseView"/>.</summary>
public sealed record PanelText(string Title, string NextText, bool NextEnabled, string CountText, bool CountRed, string? RuleText, bool ChecklistHidden,
    IReadOnlyList<WarningLine> Warnings);

/// <summary>
/// The texts of the tracker strip, the phase panel, the Next Phase dialog and the Step Back dialog, shared by both apps.
/// Strings are the WinForms app's, character for character.
/// </summary>
public static class PhaseTexts
{
    public const string NoPhasesText = "No phases defined — add them in the Rules Workbench (Phases tab)";
    public const string StartGameText = "Start game ▶";
    public const string NextPhaseText = "Next Phase ▶";

    /// <summary>Strip items in order: the no-phases text alone, else "Before turn 1" or "Turn n" (+ stage), then one chip per phase.</summary>
    public static IReadOnlyList<StripChip> Strip(PhaseView v)
    {
        var l = new List<StripChip>();
        if (v.NoPhases) { l.Add(new StripChip(NoPhasesText, null, null)); return l; }
        if (v.BeforeStart) l.Add(new StripChip("Before turn 1", null, null));
        else
        {
            l.Add(new StripChip($"Turn {v.Turn}", null, null));
            if (!string.IsNullOrWhiteSpace(v.StageText)) l.Add(new StripChip(v.StageText!, null, null));
        }
        foreach (var c in v.Chips)
        {
            var text = c.State == ChipState.Current && v.PlayingText is { Length: > 0 } playing ? playing   // hot-seat: another side's phase
                : c.State == ChipState.Current && !string.IsNullOrEmpty(v.SideName) ? $"{v.SideName} player — {c.Name}" : c.Name;
            l.Add(new StripChip(text, c.State, c.Notes));
        }
        return l;
    }

    /// <param name="viewerSideId">The viewer's side; the view already hides another side's phase (<see cref="PhaseView.PlayingText"/>).</param>
    /// <param name="replaying">A received turn is replaying: Next Phase and Acknowledge are off.</param>
    /// <param name="rules">The pack, to resolve the current phase's rule reference into <see cref="PanelText.RuleText"/>.</param>
    public static PanelText Panel(PhaseView v, string? viewerSideId, bool replaying = false, RulesPack? rules = null)
    {
        _ = viewerSideId;
        var show = !v.NoPhases;
        var enabled = show && (v.BeforeStart || v.NextName != null) && v.PlayingText == null && !replaying;
        if (!show) return new PanelText("", NextPhaseText, enabled, "", false, null, true, Array.Empty<WarningLine>());
        var title = v.BeforeStart ? "Before turn 1" : v.PlayingText is { } playing ? playing : v.Current is { } cur
            ? (v.SideName != null ? $"{v.SideName} player — {cur.Name}" : cur.Name) : "";
        var next = v.BeforeStart ? StartGameText : v.NextName != null ? NextPhaseText + " " + v.NextName : NextPhaseText;
        var n = v.Decisions.Count;
        return new PanelText(title, next, enabled, n == 1 ? "1 decision left" : $"{n} decisions left", n > 0,
            RuleRefOf(v, rules) is { } r ? RuleLinkText(r) : null, v.PlayingText != null,
            v.Warnings.Select(w => new WarningLine(WarningText(w), w.Index, CanAck(w, replaying))).ToList());
    }

    /// <summary>The current phase's rule reference, or null in another side's phase or when the pack has no such reference.</summary>
    public static RuleRef? RuleRefOf(PhaseView v, RulesPack? rules) =>
        v.PlayingText == null && v.Current?.RuleRefId is { Length: > 0 } id ? rules?.ById<RuleRef>(id) : null;

    public static string RuleLinkText(RuleRef r) => r.Page > 0 ? $"Rule: {r.Label} (p. {r.Page})" : $"Rule: {r.Label}";

    /// <summary>"{message} — n unit(s)" plus " (decision)" / " (acknowledged)" for decision warnings.</summary>
    public static string WarningText(WarningHit w)
    {
        var n = w.PieceIds.Count;
        var head = w.Message.Replace("{unit}", "").Trim();
        return $"{head} — {n} {(n == 1 ? "unit" : "units")}{(w.IsDecision ? (w.Acknowledged ? " (acknowledged)" : " (decision)") : "")}";
    }

    /// <summary>Acknowledge applies to a decision warning not yet acknowledged, and never while a received turn replays.</summary>
    public static bool CanAck(WarningHit? w, bool replaying) => !replaying && w is { IsDecision: true, Acknowledged: false };

    public static string NextPhaseHead(int open) => open == 1 ? "1 decision is still open in this phase:" : $"{open} decisions are still open in this phase:";

    /// <summary>"Turn n · Phase", or just the phase when it is the bare turn checkpoint ("Turn n").</summary>
    public static string StepBackWhere(CheckpointRow r) => r.Phase == $"Turn {r.Turn}" ? r.Phase : $"Turn {r.Turn} · {r.Phase}";

    public static readonly IReadOnlyList<string> StepBackColumns = new[] { "Turn", "Stage", "Side", "Phase", "Reached", "Actions" };

    /// <summary>The Step Back To list's cells, in <see cref="StepBackColumns"/> order. Reached is local time "HH:mm".</summary>
    public static IReadOnlyList<string> StepBackCells(CheckpointRow r) => new[]
    {
        r.Turn.ToString(), r.Stage ?? "", r.Side ?? "", r.Phase,
        r.At is { } at ? at.ToLocalTime().ToString("HH:mm") : "", r.ActionsSince.ToString(),
    };

    public static string StepBackConfirm(CheckpointRow r) =>
        $"Step back to the start of {StepBackWhere(r)}?{Environment.NewLine}This undoes everything done since then. You can redo it afterwards.";
}
