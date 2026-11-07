using Quartermaster.Render;

namespace Quartermaster.App;

/// <summary>The interface tour: Help → Interface Tour, and the one-time automatic start when a game is first opened.</summary>
sealed partial class MainForm
{
    TourOverlay? _tour;

    /// <summary>Screen rectangle of a tour target id, or null when that control is not on screen.</summary>
    Rectangle? ResolveTourTarget(string id)
    {
        Rectangle? Of(Control? c) => c is { IsDisposed: false, Visible: true, IsHandleCreated: true, Width: > 0, Height: > 0 } ? c.RectangleToScreen(c.ClientRectangle) : null;
        Rectangle? Menu(string caption)
        {
            var item = _menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == caption);
            return item != null && _menu.Visible && item.Bounds.Width > 0 ? _menu.RectangleToScreen(item.Bounds) : null;
        }
        if (_session?.Game == null) return null;
        return id switch
        {
            "ModuleTree" => Of(_tree),
            "MapWindow" => FirstMap() is { Visible: true } m ? Of(m) : null,
            "PhaseStrip" => Of(_strip),
            "PhasePanel" => Of(_phaseDock?.Panel),
            "AlertBar" => Of(_alertBar),
            "OutlinerPanel" => Of(_outlinerDock?.Panel),
            "LedgerMenu" => _session.Rules != null ? Menu("&View") : null,
            "EditMenu" => Menu("&Edit"),
            _ => null,
        };
    }

    /// <summary>Starts the tour on the first visible step. Does nothing while a tour is already open or no game is open.</summary>
    void ShowTour()
    {
        if (_tour is { IsDisposed: false } || _session?.Game == null) return;
        var steps = TourOverlay.VisibleSteps(ResolveTourTarget);
        if (steps.Count == 0) return;
        var tour = new TourOverlay(steps, ResolveTourTarget, _ => MarkTourShown());
        tour.FormClosed += (_, _) => { if (ReferenceEquals(_tour, tour)) _tour = null; tour.Dispose(); };
        _tour = tour;
        tour.Show(this);
        tour.Go(0);
    }

    void MarkTourShown()
    {
        if (!_settings.Current.TourShown) _settings.Save(_settings.Current with { TourShown = true });
    }

    /// <summary>First game open ever (setting not yet true) starts the tour once the window has settled; never during smoke runs.</summary>
    void MaybeAutoTour()
    {
        if (Program.Smoke || _settings.Current.TourShown || _session?.Game == null) return;
        BeginInvoke(new Action(() => Guard(() => { if (!_settings.Current.TourShown) ShowTour(); })));
    }

    /// <summary>Smoke (--screenshot-tour): step 1 and, when present, the alert bar step, captured from the screen with the balloon.</summary>
    void SmokeTour()
    {
        if (_session?.Game == null) { SmokeFail("tour smoke needs a game"); return; }
        CloseFormulaDialogs();
        Application.DoEvents();
        var steps = TourOverlay.VisibleSteps(ResolveTourTarget);
        ShowTour();
        if (_tour is not { } tour) { SmokeFail("tour did not open (no visible steps)"); return; }
        Application.DoEvents();
        var path = _opts.ScreenshotTour!;
        ShotWithBalloon(path);
        var log = $"smoke: tour opened on '{tour.CurrentTitle}' with {steps.Count} visible steps ({string.Join(", ", steps.Select(s => s.Id))})";
        var alertIdx = steps.ToList().FindIndex(s => s.Id == "alertBar");
        if (alertIdx > 0)
        {
            tour.Go(alertIdx);
            Application.DoEvents();
            ShotWithBalloon(Path.ChangeExtension(path, null) + "-alerts.png");
            log += $"; second shot on '{tour.CurrentTitle}'";
        }
        tour.Go(steps.Count - 1);
        Application.DoEvents();
        ShotWithBalloon(Path.ChangeExtension(path, null) + "-last.png");
        var saved = _settings.Current.TourShown;
        if (saved) _settings.Save(_settings.Current with { TourShown = false }); // repeatable regardless of prior state
        tour.Close();
        Application.DoEvents();
        var ok = _settings.Current.TourShown;
        _settings.Save(_settings.Current with { TourShown = saved }); // a smoke run must not change the real first-run state
        Program.AppLog($"{log}; skip sets TourShown {(ok ? "ok" : "FAILED")}");
        if (!ok) Program.ExitCode = 1;
    }

    /// <summary>Main window drawn off-screen (so other windows cannot cover it), then the balloon drawn over it through its pointer-shaped region.</summary>
    void ShotWithBalloon(string path)
    {
        for (var i = 0; i < 3; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(80); }
        SaveScreenshot(this, path, true);
        if (_tour is not { IsDisposed: false, Region: { } region } tour) return;
        using var baseImg = Image.FromFile(path);
        using var bmp = new Bitmap(baseImg);
        baseImg.Dispose();
        using var balloon = new Bitmap(tour.Width, tour.Height);
        tour.DrawToBitmap(balloon, new Rectangle(Point.Empty, balloon.Size));
        using (var g = Graphics.FromImage(bmp))
        {
            var at = new Point(tour.Left - Left, tour.Top - Top);
            using var clip = region.Clone();
            clip.Translate(at.X, at.Y);
            g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Replace);
            g.DrawImageUnscaled(balloon, at);
        }
        bmp.Save(path + ".tmp", System.Drawing.Imaging.ImageFormat.Png);
        File.Move(path + ".tmp", path, true);
    }
}
