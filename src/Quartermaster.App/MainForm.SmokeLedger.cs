using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>
/// Smoke for the Ledger tabs (--screenshot-ledger-tabs &lt;png&gt;): writes invented TEST user tabs into the (temporary) QUARTERMASTER_LAYOUT_ROOT,
/// opens the Ledger on the Units tab with counters on, checks the tab strip, a user tab, a broken-filter banner and the hover preview,
/// then saves the window as the PNG and the preview as &lt;png&gt;-preview.png.
/// </summary>
sealed partial class MainForm
{
    /// <summary>Set by Program from --screenshot-ledger-tabs.</summary>
    internal static string? LedgerTabsShot;

    void SmokeLedgerTabs()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataRoot.EnvVar))) { SmokeFail("the ledger tabs smoke needs QUARTERMASTER_LAYOUT_ROOT (it writes Ledger tabs there)"); return; }
        if (_session?.Game == null || _session.Rules == null) { SmokeFail("the ledger tabs smoke needs a game with a rules pack"); return; }
        var sha = _session.Archive.Sha256;
        if (_ledgers.TryGetValue("ledger", out var old) && !old.IsDisposed) { old.Close(); Application.DoEvents(); }
        LedgerTabStore.Save(AppDataRoot.Path, sha, LedgerPrefs.Default with
        {
            Tabs = new[]
            {
                new UserLedgerTab("Supply", "Fuel < 1000", new[] { "fuel" }, true, "Units that still have fuel"),
                new UserLedgerTab("Fuel check", "Fuel <", Array.Empty<string>(), false, null),
            },
            LastTab = "units", ShowCounters = true, Grouping = LedgerGrouping.Map,
        });
        OpenLedger();
        if (!_ledgers.TryGetValue("ledger", out var w) || w.IsDisposed || w is not LedgerWindow lw) { SmokeFail("the ledger did not open"); return; }
        lw.Size = new Size(900, 560);
        var ok = true;
        void Check(bool cond, string what) { if (!cond) { ok = false; Program.AppLog("smoke: ledger tabs check FAILED: " + what); } }

        var titles = lw.TabTitlesForSmoke;
        Program.AppLog("smoke: ledger tabs " + string.Join(" | ", titles));
        Check(titles.Count == 6 && titles[0].StartsWith("Units (") && titles[1].StartsWith("Depots & Global (") && titles[2] == "Everything"
            && titles[3] == "★ Supply" && titles[4] == "★ Fuel check" && titles[5] == "+", "tab titles");
        Check(lw.CurrentTabKeyForSmoke == "units", "opens on the last tab (units)");

        lw.SelectTabForSmoke("user:Supply");
        Program.AppLog($"smoke: Supply tab has {lw.RowCountForSmoke} row(s)");
        Check(lw.RowCountForSmoke > 0 && !lw.BannerShownForSmoke, "user tab rows");
        lw.SelectTabForSmoke("user:Fuel check");
        Check(lw.RowCountForSmoke == 0 && lw.BannerShownForSmoke, "broken filter shows the banner and no rows");
        lw.SelectTabForSmoke("units");
        Check(LedgerTabStore.Load(AppDataRoot.Path, sha).LastTab == "units", "last tab remembered");

        // Let the worker draw the visible counters before the picture is taken.
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until && (lw.VisibleUnitRowsForSmoke() == 0 || lw.ReadyCounterRowsForSmoke() < lw.VisibleUnitRowsForSmoke()))
        { lw.PaintForSmoke(); System.Threading.Thread.Sleep(50); }
        for (var i = 0; i < 5; i++) { lw.PaintForSmoke(); System.Threading.Thread.Sleep(40); }
        Program.AppLog($"smoke: ledger counters ready {lw.ReadyCounterRowsForSmoke()}/{lw.VisibleUnitRowsForSmoke()} visible unit row(s)");
        Check(lw.VisibleUnitRowsForSmoke() > 0 && lw.ReadyCounterRowsForSmoke() == lw.VisibleUnitRowsForSmoke(), "counter pictures drawn");
        lw.SaveScreenshot(LedgerTabsShot!);

        lw.Activate();
        var tip = lw.ShowPreviewForSmoke();
        until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && tip is { Visible: false }) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
        for (var i = 0; i < 4; i++) { lw.PaintForSmoke(); System.Threading.Thread.Sleep(40); }
        if (tip is { Visible: true })
        {
            var r = tip.Bounds;
            Program.AppLog($"smoke: counter preview shown {r.Width}x{r.Height}");
            Check(Math.Max(r.Width, r.Height) >= 200, "preview is about 200 px");
            using var bmp = new Bitmap(r.Width, r.Height);
            tip.DrawToBitmap(bmp); // drawn from the window itself, never copied off the screen (other programs may be behind it)
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LedgerTabsShot!)) ?? ".", Path.GetFileNameWithoutExtension(LedgerTabsShot!) + "-preview.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Program.AppLog("smoke: counter preview saved to " + path);
        }
        else Check(false, "hover preview shown");

        tip?.Hide();

        // The Edit tabs dialog, shown without blocking, on the broken tab so its inline parse error is visible.
        using (var dlg = new EditTabsDialog(LedgerTabStore.Load(AppDataRoot.Path, sha).Tabs, _session.Rules, 1, false))
        {
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(Left + 60, Top + 60);
            dlg.Show(this);
            for (var i = 0; i < 5; i++) { lw.PaintForSmoke(); System.Threading.Thread.Sleep(40); }
            using var bmp = new Bitmap(dlg.Width, dlg.Height);
            dlg.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(LedgerTabsShot!)) ?? ".", Path.GetFileNameWithoutExtension(LedgerTabsShot!) + "-dialog.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Program.AppLog("smoke: edit tabs dialog saved to " + path);
            dlg.Close();
        }

        Program.AppLog("smoke: ledger tabs " + (ok ? "ok" : "FAILED"));
        if (!ok) Program.ExitCode = 1;
    }
}
