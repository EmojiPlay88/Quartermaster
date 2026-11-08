using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// What "Show in Ledger" does for some counters: the tab to show (<see cref="LedgerTabDef.Key"/>; null when no row can be shown),
/// whether the text filter must be cleared, the row keys to select (in the tab's own order), how many of the counters have no row
/// there and a short message for the status bar (null when every counter was found).
/// </summary>
public sealed record LedgerJumpPlan(string? TabKey, bool ClearFilter, IReadOnlyList<string> RowKeys, int Missing, string? Message);

/// <summary>
/// UI-free decision behind "Show in Ledger" (map counters → their Ledger rows), shared by the Windows app and the cross-platform Desktop app.
/// Rows come only from the viewer's <paramref name="view"/> of the game, and a counter the viewer may not see is never planned.
/// </summary>
public static class LedgerJump
{
    public const string NoValuesHint = "Tick \"Show units without values\" to list units that have no values yet.";

    /// <summary>
    /// Picks the tab that shows the most of the counters with the Ledger's current text filter and side: the current tab when it shows them all,
    /// otherwise the first tab (fixed tabs, then the player's tabs) that shows the most. When the text filter hides some of them, "Everything" with
    /// the filter cleared is used instead if it shows more. Holder rows are never targets.
    /// </summary>
    /// <param name="pieceIds">The counters picked on the map.</param>
    /// <param name="tabs">The Ledger's tabs, as <see cref="LedgerTabs.Tabs"/> lists them.</param>
    /// <param name="currentTabKey">The tab shown now (an unknown key is ignored).</param>
    /// <param name="view">What the current viewer may see.</param>
    /// <param name="filter">The Ledger's text filter.</param>
    /// <param name="sideId">The Side dropdown (applies to unit tabs only, as in the Ledger).</param>
    /// <param name="includeEmpty">"Show units without values".</param>
    /// <param name="canSee">The viewer's visibility check (for example <c>ModuleSession.CanSee</c>); a counter it rejects is never shown.</param>
    public static LedgerJumpPlan Plan(IReadOnlyCollection<string> pieceIds, IReadOnlyList<LedgerTabDef> tabs, string currentTabKey,
        GameState view, RulesPack pack, ModuleModel model, string filter, string? sideId, bool includeEmpty, Func<string, bool>? canSee = null)
    {
        var ids = (pieceIds ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) return new LedgerJumpPlan(null, false, Array.Empty<string>(), 0, null);

        bool Visible(string id)
        {
            if (!view.Pieces.ContainsKey(id)) return false;
            try { return canSee?.Invoke(id) ?? true; }
            catch (Exception) { return false; }   // fail closed
        }
        var visible = ids.Where(Visible).ToList();
        var targets = visible.Select(LedgerKey.Piece).ToHashSet(StringComparer.Ordinal);

        string? tabKey = null;
        var clear = false;
        IReadOnlyList<string> rows = Array.Empty<string>();
        if (targets.Count > 0)
        {
            IReadOnlyList<string> Shown(LedgerTabDef t, string f) =>
                LedgerTabs.Rows(t, view, pack, model, f, sideId, LedgerGrouping.None, includeEmpty).Rows
                    .Where(r => !r.IsGroupHeader && targets.Contains(r.Row.Key)).Select(r => r.Row.Key).Distinct(StringComparer.Ordinal).ToList();

            var order = tabs.Where(t => t.Key == currentTabKey).Concat(tabs.Where(t => t.Key != currentTabKey));
            foreach (var t in order)
            {
                var shown = Shown(t, filter ?? "");
                if (shown.Count > rows.Count) { tabKey = t.Key; rows = shown; }
                if (rows.Count == targets.Count) break;
            }
            var f = (filter ?? "").Trim();
            if (rows.Count < targets.Count && f.Length > 0 && tabs.FirstOrDefault(t => t.Kind == LedgerTabKind.Everything) is { } all)
            {
                var shown = Shown(all, "");
                if (shown.Count > rows.Count) { tabKey = all.Key; rows = shown; clear = true; }
            }
        }

        var missing = ids.Count - rows.Count;
        string? message = null;
        if (missing > 0)
        {
            message = ids.Count == 1 ? "That counter has no row in the Ledger."
                : missing == ids.Count ? $"None of the {ids.Count} selected counters has a row in the Ledger."
                : $"{missing} of the {ids.Count} selected counters have no row in the Ledger.";
            var shownSet = rows.ToHashSet(StringComparer.Ordinal);
            if (!includeEmpty && visible.Any(id => !shownSet.Contains(LedgerKey.Piece(id)))) message += " " + NoValuesHint;
        }
        return new LedgerJumpPlan(tabKey, clear, rows, missing, message);
    }
}
