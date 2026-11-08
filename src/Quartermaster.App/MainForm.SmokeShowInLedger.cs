using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// Smoke for Show in Ledger (--smoke-show-in-ledger): picks a counter on an open map that the Ledger lists (giving it a ledger value first when
/// none has one), opens the Ledger on the Depots &amp; Global tab, selects the counter and presses Ctrl+L through the shortcut dispatch;
/// the Ledger must move to a tab with the counter's row and select exactly that row.
/// </summary>
sealed partial class MainForm
{
    /// <summary>Set by Program from --smoke-show-in-ledger.</summary>
    internal static bool SmokeShowInLedgerFlag;

    void SmokeShowInLedger()
    {
        void Fail(string what) { Program.AppLog("smoke: show in ledger FAILED: " + what); Program.ExitCode = 1; }
        if (_session?.Game is not { } game || _session.Rules is not { } pack || _session.View is not { } view) { Fail("needs a game with a rules pack"); return; }

        // A counter on an open map; prefer one that already has a Ledger row.
        var open = _maps.Values.Where(w => !w.IsDisposed).ToList();
        MapWindow? MapOf(string mapId) => open.FirstOrDefault(w => w.Map.Id == mapId || w.Map.Name == mapId);
        var all = LedgerTabs.Tabs(Array.Empty<UserLedgerTab>()).First(t => t.Kind == LedgerTabKind.Everything);
        var withRows = LedgerTabs.Rows(all, view, pack, _session.Model, "", null, LedgerGrouping.None, false).Rows
            .Where(r => r.PieceId != null && view.Pieces.TryGetValue(r.PieceId, out var p) && MapOf(p.MapId) != null).Select(r => r.PieceId!).ToList();
        string? id = withRows.FirstOrDefault();
        if (id == null)
        {
            var candidates = LedgerTabs.Rows(all, view, pack, _session.Model, "", null, LedgerGrouping.None, true).Rows
                .Where(r => r.PieceId != null && view.Pieces.TryGetValue(r.PieceId, out var p) && MapOf(p.MapId) != null).Select(r => r.PieceId!).ToList();
            var prop = pack.Properties.FirstOrDefault(p => p.Scope != PropertyScope.Holders);
            if (candidates.FirstOrDefault() is not { } c || prop == null) { Fail("no counter on an open map can have a Ledger row"); return; }
            var key = LedgerKey.Piece(c);
            game.Execute(new LedgerDelta(key, prop.Id, Ledger.Get(game.State, key, prop.Id) ?? 0, 1, "manual", null, null, "smoke", null));
            id = c;
        }
        var map = MapOf(view.Pieces[id].MapId)!;

        if (_ledgers.TryGetValue("ledger", out var old) && !old.IsDisposed) { old.Close(); Application.DoEvents(); }
        if (OpenLedger() is not { } lw) { Fail("the ledger did not open"); return; }
        lw.SelectTabForSmoke("holders");
        map.Activate();
        Application.DoEvents();
        map.Canvas.SelectOnly(id);
        var dispatched = TryDispatchShortcut(Keys.Control | Keys.L, map, map.Canvas);
        Application.DoEvents();

        var want = LedgerKey.Piece(id);
        var got = lw.SelectedKeysForSmoke;
        Program.AppLog($"smoke: show in ledger: Ctrl+L {(dispatched ? "dispatched" : "not dispatched")}, tab {lw.CurrentTabKeyForSmoke}, selected {string.Join(",", got)} (want {want})");
        if (!dispatched) Fail("Ctrl+L was not dispatched");
        else if (lw.CurrentTabKeyForSmoke == "holders") Fail("stayed on Depots & Global");
        else if (got.Count != 1 || !got.Contains(want)) Fail("the counter's row is not the selected row");
        else Program.AppLog("smoke: show in ledger ok");

        // A depot alert opens the Ledger at the holder's row; it must not jump to the counters selected on the map.
        if (pack.Holders?.FirstOrDefault() is { } holder)
        {
            map.Activate();
            map.Canvas.SelectOnly(id);
            const string sentinel = "smoke: alert sentinel";
            SetStatus(sentinel);
            GoToMatch(new AlertMatch(null, holder.Id, "smoke"));
            Application.DoEvents();
            var sel = lw.IsDisposed ? new List<string>() : lw.SelectedKeysForSmoke.ToList();
            Program.AppLog($"smoke: show in ledger: depot alert selected {string.Join(",", sel)}, status '{_sbMessage.Text}'");
            if (_sbMessage.Text != sentinel) Fail("the depot alert ran Show in Ledger for the map selection");
            else if (sel.Count != 1 || sel[0] != LedgerKey.Holder(holder.Id)) Fail("the depot alert did not select the holder's row");
            else Program.AppLog("smoke: show in ledger: depot alert does not jump, ok");
        }
        else Program.AppLog("smoke: show in ledger: no holder in the rules pack, depot alert check skipped");

        map.Canvas.SelectOnly(null);
        map.Canvas.Invalidate();
        if (!lw.IsDisposed) lw.Close();
        Application.DoEvents();
    }
}
