using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3a task 8: the Overview window and Save map as picture (choice, progress, current view, whole map). Headless on the Riverland demo.</summary>
[Collection("MacFlag")]
public sealed class OverviewAndExportTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-export-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public OverviewAndExportTests() { ShortcutGestures.IsMac = false; Directory.CreateDirectory(_tmp); }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file = "Riverland-Demo.vmod", [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    static bool WaitFor(Func<bool> condition, int ms = 30000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    MainWindow NewWindow()
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, Path.Combine(_tmp, "layout")) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        return w;
    }

    MainWindow Game()
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo()));
        w.StartSetup(null);
        Assert.True(WaitFor(() => w.MapTabs.WhenTiled().IsCompleted));
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    // ---------- overview ----------

    [AvaloniaFact]
    public void Overview_follows_active_map_and_title()
    {
        var w = Game();
        w.ClickMenu("&Overview");
        var ov = Assert.IsType<OverviewWindow>(w.OverviewTool);
        var active = w.MapTabs.ActiveView!;
        Assert.Same(active, ov.Target);
        Assert.Equal("Overview - " + active.Map.Name, ov.Title);
        Assert.Equal((260d, 240d), (ov.Width, ov.Height));

        // A second click on the menu item brings the same window forward.
        w.ClickMenu("&Overview");
        Assert.Same(ov, w.OverviewTool);

        var other = w.MapTabs.Views.FirstOrDefault(v => !ReferenceEquals(v, active));
        if (other != null)
        {
            w.MapTabs.Activate(other);
            Assert.Same(other, ov.Target);
            Assert.Equal("Overview - " + other.Map.Name, ov.Title);
        }

        // Toolbar button sits between Notes and Inventory and opens it too.
        var ids = w.Toolbar.Buttons.Select(b => b.Id).ToList();
        Assert.True(ids.IndexOf("tool.overview") > ids.IndexOf("tool.notes"));
        Assert.True(ids.IndexOf("tool.overview") < ids.IndexOf("tool.inventory"));
        Assert.Equal("Overview", w.Toolbar.Buttons.First(b => b.Id == "tool.overview").Text);
        ov.Close();
        Assert.Null(w.OverviewTool);
        w.Toolbar.Find("tool.overview")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(w.OverviewTool);
    }

    [AvaloniaFact]
    public void Overview_needs_a_game_and_closes_with_it()
    {
        var w = NewWindow();
        Assert.False(w.FindMenuItem("&Overview")!.IsEnabled);
        Assert.False(w.Toolbar.Find("tool.overview")!.IsEnabled);
        Assert.True(w.OpenModule(Demo()));
        Assert.False(w.FindMenuItem("&Overview")!.IsEnabled);
        w.StartSetup(null);
        Assert.True(w.FindMenuItem("&Overview")!.IsEnabled);
        w.ClickMenu("&Overview");
        Assert.NotNull(w.OverviewTool);
        Assert.True(w.OpenModule(Demo()));   // a new module replaces the session
        Assert.Null(w.OverviewTool);
    }

    [AvaloniaFact]
    public void Overview_viewport_rect_matches_view()
    {
        var w = Game();
        w.ClickMenu("&Overview");
        var ov = w.OverviewTool!;
        var view = w.MapTabs.ActiveView!;
        Dispatcher.UIThread.RunJobs();
        view.ZoomCentered(2f);
        var canvas = ov.Canvas;
        var size = new SizeD(canvas.Bounds.Width, canvas.Bounds.Height);
        Assert.True(size.W > 50 && size.H > 50);

        var ext = FitView.Extent(view.Map);
        var fit = OverviewGeometry.Fit(ext, size);
        var vis = view.View.VisibleMapRect(new SKSize((float)view.Bounds.Width, (float)view.Bounds.Height));
        var expected = new RectD(fit.OffsetX + (vis.X - ext.X) * fit.Scale, fit.OffsetY + (vis.Y - ext.Y) * fit.Scale, vis.W * fit.Scale, vis.H * fit.Scale);
        var rect = canvas.ViewportRectPx()!.Value;
        Assert.Equal((expected.X, expected.Y, expected.W, expected.H), (rect.X, rect.Y, rect.W, rect.H));

        // Drawn: a red 2 px edge along the rectangle's left side (the rectangle is inside the control at 200%).
        using var bmp = new SKBitmap((int)Math.Ceiling(size.W), (int)Math.Ceiling(size.H));
        using (var c = new SKCanvas(bmp)) canvas.DrawMap(c, size.W, size.H);
        var y = (int)(rect.Y + rect.H / 2);
        var hit = Enumerable.Range((int)rect.X - 2, 5).Select(x => bmp.GetPixel(Math.Clamp(x, 0, bmp.Width - 1), Math.Clamp(y, 0, bmp.Height - 1)))
            .Any(p => p.Red > 200 && p.Green < 80 && p.Blue < 80);
        Assert.True(hit, "no red edge at the viewport rectangle");
    }

    [AvaloniaFact]
    public void Overview_click_centres_map()
    {
        var w = Game();
        w.ClickMenu("&Overview");
        var ov = w.OverviewTool!;
        var view = w.MapTabs.ActiveView!;
        view.ZoomCentered(2f);
        var canvas = ov.Canvas;
        var ext = FitView.Extent(view.Map);
        var fit = OverviewGeometry.Fit(ext, new SizeD(canvas.Bounds.Width, canvas.Bounds.Height));
        var want = new PointD(ext.X + ext.W * 0.25, ext.Y + ext.H * 0.25);
        var before = view.View.Pan;
        canvas.PanTo(new Point(fit.OffsetX + (want.X - ext.X) * fit.Scale, fit.OffsetY + (want.Y - ext.Y) * fit.Scale));
        Assert.NotEqual(before, view.View.Pan);
        var centre = view.View.ToMap(new SKPoint((float)view.Bounds.Width / 2, (float)view.Bounds.Height / 2));
        Assert.Equal(want.X, centre.X, 1);
        Assert.Equal(want.Y, centre.Y, 1);
    }

    [AvaloniaFact]
    public void Overview_missing_board_placeholder()
    {
        using var s = ModuleSession.Open(Demo(), Path.Combine(_tmp, "cache2"), null);
        s.StartNewGame(Difficulty.FullyAuto);
        var view = new MapView(s, s.Model.Maps[0]);   // no tiles were set
        var canvas = new OverviewView { Target = view };
        using var bmp = new SKBitmap(240, 200);
        using (var c = new SKCanvas(bmp)) canvas.DrawMap(c, 240, 200);
        var ext = FitView.Extent(view.Map);
        var fit = OverviewGeometry.Fit(ext, new SizeD(240, 200));
        // A corner of the fitted board, clear of the 1 px viewport rectangle at the map's top left, is the placeholder grey; outside it the workspace colour.
        var inside = bmp.GetPixel((int)(fit.OffsetX + ext.W * fit.Scale - 6), (int)(fit.OffsetY + ext.H * fit.Scale - 6));
        Assert.Equal(new SKColor(0xC0, 0xC0, 0xC0), inside);
        Assert.Equal(new SKColor(0xAB, 0xAB, 0xAB), bmp.GetPixel(0, 0));
    }

    [AvaloniaFact]
    public void Overview_draws_tiles_when_present()
    {
        var w = Game();
        w.ClickMenu("&Overview");
        var ov = w.OverviewTool!;
        var view = w.MapTabs.ActiveView!;
        Assert.Contains(view.Tiles.Values, t => !t.IsPlaceholder);
        var size = new SizeD(240, 200);
        using var bmp = new SKBitmap(240, 200);
        using (var c = new SKCanvas(bmp)) ov.Canvas.DrawMap(c, size.W, size.H);
        var ext = FitView.Extent(view.Map);
        var fit = OverviewGeometry.Fit(ext, size);
        var inside = bmp.GetPixel((int)(fit.OffsetX + ext.W * fit.Scale - 6), (int)(fit.OffsetY + ext.H * fit.Scale - 6));
        Assert.NotEqual(new SKColor(0xC0, 0xC0, 0xC0), inside);
        Assert.NotEqual(new SKColor(0xAB, 0xAB, 0xAB), inside);
    }

    // ---------- save map as picture ----------

    static string[] Leftovers(string dir) => Directory.GetFiles(dir, "*.tmp");

    [AvaloniaFact]
    public void Export_current_view_saves_png_same_as_snapshot()
    {
        var w = Game();
        var view = w.MapTabs.ActiveView!;
        var path = Path.Combine(_tmp, "view.png");
        _dialogs.ExportChoices.Enqueue(new ExportChoice(false, 0));
        _dialogs.SaveFiles.Enqueue(path);
        using var expected = view.Snapshot()!;
        var t = w.SaveMapAsPictureAsync(view);
        Assert.True(WaitFor(() => t.IsCompleted));
        t.GetAwaiter().GetResult();

        Assert.Equal(new[] { view.Map.Name }, _dialogs.ExportAsked);
        Assert.Equal("Save map as picture", _dialogs.PickerTitles.Last());
        Assert.Equal(MapExportFlow.SanitizeFileName(view.Map.Name), _dialogs.SuggestedNames.Last());
        Assert.Equal("png", _dialogs.DefaultExtensions.Last());
        Assert.Equal("Saved " + path, w.StatusText);
        using var actual = SKBitmap.Decode(path);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (var y = 0; y < actual.Height; y += 3)
            for (var x = 0; x < actual.Width; x += 3)
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
        Assert.Empty(Leftovers(_tmp));
        Assert.False(w.ExportRunning);
    }

    [AvaloniaFact]
    public void Export_whole_map_writes_png_with_progress()
    {
        var w = Game();
        var view = w.MapTabs.ActiveView!;
        var path = Path.Combine(_tmp, "whole.png");
        _dialogs.ExportChoices.Enqueue(new ExportChoice(true, 0));
        _dialogs.SaveFiles.Enqueue(path);
        var seen = new List<double>();
        w.ExportProgressTap = p => { lock (seen) seen.Add(p); };
        var t = w.SaveMapAsPictureAsync(view);
        Assert.True(WaitFor(() => t.IsCompleted));
        t.GetAwaiter().GetResult();

        Assert.Equal("Saved " + path, w.StatusText);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(path).Take(4).ToArray());
        using var bmp = SKBitmap.Decode(path);
        var (ew, eh) = MapImageExporter.OutputSize(view.Map, 0.25);
        Assert.Equal((ew, eh), (bmp.Width, bmp.Height));
        Assert.NotEmpty(seen);
        Assert.Equal(1.0, seen.Max(), 3);
        var dlg = w.LastExportDialog!;
        Assert.Equal("Saving map as picture", dlg.Title);
        Assert.True(WaitFor(() => dlg.Updates >= seen.Count));
        Assert.Equal(100, dlg.Percent);
        Assert.False(dlg.IsVisible);
        Assert.Empty(Leftovers(_tmp));
    }

    [AvaloniaFact]
    public void Export_whole_map_cancel_leaves_no_file()
    {
        var w = Game();
        var view = w.MapTabs.ActiveView!;
        var path = Path.Combine(_tmp, "cancel.png");
        _dialogs.ExportChoices.Enqueue(new ExportChoice(true, 1));
        _dialogs.SaveFiles.Enqueue(path);
        w.ExportProgressTap = _ => w.CancelExport();   // what the dialog's Cancel button does, from inside the render
        var t = w.SaveMapAsPictureAsync(view);
        Assert.True(WaitFor(() => t.IsCompleted));
        t.GetAwaiter().GetResult();

        Assert.Equal("Export cancelled", w.StatusText);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_tmp, "cancel.png*"));
        Assert.Empty(_dialogs.Warnings);
        Assert.False(w.ExportRunning);
    }

    [AvaloniaFact]
    public void Export_progress_dialog_cancel_button_cancels()
    {
        using var cts = new CancellationTokenSource();
        var d = new ExportProgressDialog(cts);
        d.SetProgress(0.5);
        Assert.Equal(50, d.Percent);
        d.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(cts.IsCancellationRequested);
        Assert.False(d.CancelButton.IsEnabled);
    }

    [AvaloniaFact]
    public void Second_export_refused_while_running()
    {
        var w = Game();
        var view = w.MapTabs.ActiveView!;
        var path = Path.Combine(_tmp, "one.png");
        _dialogs.ExportChoices.Enqueue(new ExportChoice(false, 0));
        var gate = new TaskCompletionSource<string?>();
        _dialogs.SaveGate = gate;   // the first export waits in the file picker
        var first = w.SaveMapAsPictureAsync(view);
        Assert.True(WaitFor(() => _dialogs.PickerTitles.Contains("Save map as picture")));
        Assert.True(w.ExportRunning);

        var second = w.SaveMapAsPictureAsync(view);
        Assert.True(second.IsCompleted);
        Assert.Equal(MapExportFlow.AlreadyRunningText, w.StatusText);
        Assert.Single(_dialogs.ExportAsked);   // no second choice dialog

        gate.SetResult(path);
        Assert.True(WaitFor(() => first.IsCompleted));
        Assert.True(File.Exists(path));
        Assert.False(w.ExportRunning);
    }

    [AvaloniaFact]
    public void Export_cancelled_choice_or_picker_does_nothing()
    {
        var w = Game();
        var view = w.MapTabs.ActiveView!;
        var t = w.SaveMapAsPictureAsync(view);   // choice answers null
        Assert.True(WaitFor(() => t.IsCompleted));
        Assert.DoesNotContain("Save map as picture", _dialogs.PickerTitles);
        _dialogs.ExportChoices.Enqueue(new ExportChoice(true, 0));
        t = w.SaveMapAsPictureAsync(view);   // picker answers null
        Assert.True(WaitFor(() => t.IsCompleted));
        Assert.Null(w.LastExportDialog);
        Assert.False(w.ExportRunning);
    }

    [AvaloniaFact]
    public void Export_no_game_text()
    {
        var w = NewWindow();
        Assert.True(w.OpenModule(Demo()));
        Assert.Null(w.Session!.Game);
        var view = new MapView(w.Session, w.Session.Model.Maps[0]);
        var t = w.SaveMapAsPictureAsync(view);
        Assert.True(WaitFor(() => t.IsCompleted));
        Assert.Equal(("There is no game in progress.", "Save map as picture"), Assert.Single(_dialogs.Warnings));
        Assert.Empty(_dialogs.ExportAsked);
    }

    [AvaloniaFact]
    public void Export_from_empty_map_context_menu_and_toolbar()
    {
        var w = Game();
        var view = w.MapTabs.ActiveView!;
        var menu = MapContextMenu.Build(view, null)!;
        var item = menu.Items.OfType<MenuItem>().Last();
        Assert.Equal("Save map as picture…", item.Header);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(WaitFor(() => _dialogs.ExportAsked.Count == 1));
        Assert.True(WaitFor(() => !w.ExportRunning));

        var button = w.MapTabs.ToolbarOf(view)!.SaveButton;
        Assert.Equal("Save map as picture…", button.Content);
        Assert.Equal(MapToolbar.SaveTip, ToolTip.GetTip(button));
        Assert.Equal("Save the view or the whole map as a PNG", MapToolbar.SaveTip);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(WaitFor(() => _dialogs.ExportAsked.Count == 2));
        Assert.True(WaitFor(() => !w.ExportRunning));
    }

    [AvaloniaFact]
    public void Export_choice_dialog_answers()
    {
        var d = new ExportChoiceDialog("Main Map");
        Assert.Equal("Save map as picture", d.Title);
        Assert.True(d.ViewRadio.IsChecked);
        Assert.False(d.ZoomBox.IsEnabled);
        Assert.Equal(new ExportChoice(false, 0), d.Choice);
        d.WholeRadio.IsChecked = true;
        Assert.True(d.ZoomBox.IsEnabled);
        d.ZoomBox.SelectedIndex = 2;
        Assert.Equal(new ExportChoice(true, 2), d.Choice);
        Assert.Equal(1.0, d.Choice.Zoom);
        Assert.Equal(new[] { "25%", "50%", "100%" }, d.ZoomBox.Items.Cast<string>());
    }
}
