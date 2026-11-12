using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Map;
using Quartermaster.Desktop.Preferences;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// <c>--screenshot-state &lt;name&gt;</c>: drives the headless main window into one state before the picture is taken (never a real window):
/// <c>menu</c> (counter context menu), <c>hover</c> (hover popup with a ledger breakdown tip, needs a rules pack), <c>los</c> (line of sight line),
/// <c>log</c> (log panel with entries), <c>rules</c> (rules pack loaded; headless windows have no title bar, so the picture only proves the pack loads).
/// Menu-bar popups (File, Tools ...) are not drawn by the headless platform, so they have no state.
/// </summary>
static class ScreenshotStates
{
    public static readonly string[] Names = { "menu", "hover", "los", "log", "rules", "phases", "nextphase", "stepback", "formula", "cheatsheet", "batch", "alerts", "tree", "floatlog", "floatphases",
        "ledger", "ledger-counters", "edittabs", "outliner", "inventory", "notes", "diagnostics", "overview", "export-choice", "chart", "document", "document-search",
        "workbench", "workbench-formulas", "workbench-phases", "workbench-search", "workbench-scan",
        "settings", "settings-general", "settings-game", "settings-display", "settings-map", "settings-dice", "settings-toolbar", "settings-keyboard", "settings-folders",
        "settings-ocr", "ocr-dropdown", "tour" };

    /// <summary>Set by a state whose picture is a window of its own (a dialog, the formula window, a floating panel) instead of the main window.</summary>
    public static Window? Target { get; private set; }

    /// <summary>Closes the extra windows a state opened (before the main window closes).</summary>
    public static void Cleanup(MainWindow window)
    {
        if (window.Session is { } s) FormulaWindows.CloseAllFor(s);
        Target?.Close();
        Target = null;
        // As WinForms SmokeTour: the tour state leaves TourShown as it found it (closing the balloon above marked it shown).
        if (_tourShown is { } shown && window.SettingsStore.Current.TourShown != shown) window.SettingsStore.Update(x => x with { TourShown = shown });
        _tourShown = null;
        if (_scanDir != null) { try { Directory.Delete(_scanDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } _scanDir = null; }
    }

    static string? _scanDir;
    static bool? _tourShown;

    static readonly string[] SettingsPages = { "general", "game", "display", "map", "dice", "toolbar", "keyboard", "folders", "ocr" };

    /// <summary>Start game, then Next Phase until North Combat (checklist, warnings), as a real turn would reach it.</summary>
    static string? ToCombat(MainWindow window, Action? beforeClose = null)
    {
        if (window.Actions == null || window.PhaseControl == null) return "This state needs a rules pack with phases (--rules).";
        for (var i = 0; i < 3; i++)
        {
            window.Actions.NextPhase();
            Pump(300);
        }
        beforeClose?.Invoke();
        CloseCalcWindows(window);
        Pump(300);
        return window.PhaseControl.View().BeforeStart ? "The game did not start." : null;
    }

    /// <summary>The queued runs of a phase start open formula windows / batch grids by themselves; states that picture something else close them.</summary>
    static void CloseCalcWindows(MainWindow window)
    {
        if (window.Session is not { } s) return;
        Pump(300);
        foreach (var b in FormulaWindows.OpenBatchFor(s)) b.Close();
        FormulaWindows.CloseAllFor(s);
    }

    /// <returns>An error text, or null when the state is in place.</returns>
    public static string? Apply(string name, MainWindow window)
    {
        Target = null;   // never the window an earlier state left behind
        var view = window.MapTabs.ActiveView;
        var s = window.Session;
        if (view == null || s?.Game == null) return "No map to put into the state.";
        switch (name)
        {
            case "rules":
                return s.Rules == null ? "State 'rules' needs --rules or --new-rules." : null;
            case "phases":
                return ToCombat(window, () => window.Phases.SetChecked(0, true));
            case "nextphase":
            {
                if (ToCombat(window) is { } e1) return e1;
                var dlg = new NextPhaseDialog(window.PhaseControl!.View().Decisions);
                dlg.Show(window);
                Pump(200);
                Target = dlg;
                return null;
            }
            case "stepback":
            {
                if (ToCombat(window) is { } e2) return e2;
                var h = window.History;
                if (h == null) return "No history.";
                var rows = h.Checkpoints();
                if (rows.Count == 0) return "No checkpoints to list.";
                var dlg = new StepBackDialog(rows, r => HistoryController.Describe(h.Preview(r)), confirmOnly: false);
                dlg.Show(window);
                Pump(200);
                Target = dlg;
                return null;
            }
            case "formula":
            case "cheatsheet":
            {
                if (window.PhaseControl == null) return "This state needs --rules.";
                window.PhaseControl.SetDifficulty(Difficulty.SemiAuto);
                Pump(200);
                var unit = FindPiece(s, view, true);
                if (unit == null) return "No unit is visible.";
                var fw = FormulaWindows.Show(window, s, "f-attack", unit.Value.Id, null);
                if (fw == null) return "The formula window did not open.";
                Pump(400);
                fw.Type("Die", "4");
                fw.Type("Readiness", "3");
                Pump(500);
                Target = name == "formula" ? fw : fw.CheatSheet;
                return Target == null ? "The cheat sheet did not open." : null;
            }
            case "batch":
            {
                if (window.PhaseControl == null) return "This state needs --rules.";
                window.PhaseControl.SetDifficulty(Difficulty.SemiAuto);
                Pump(200);
                window.Actions!.NextPhase();   // Start game: North Supply queues the weather, delivery and a fuel / ammo draw per unit
                Pump(600);
                var grid = FormulaWindows.OpenBatchFor(s).FirstOrDefault();
                if (grid == null)
                {
                    var run = s.Game!.State.Pending.FirstOrDefault(r => r != null && r.FormulaId == "f-fuel-supply");
                    if (run == null) return "No batch run is pending.";
                    grid = FormulaWindows.ShowBatch(window, s, window.Actions!, run.Id, null);
                }
                if (grid == null) return "The batch grid did not open.";
                Pump(500);
                grid.TypeCell(0, 1, "3");
                grid.TypeCell(0, 2, "2");
                grid.SelectRow(0);
                Pump(500);
                Target = grid;
                return null;
            }
            case "alerts":
            {
                if (window.PhaseControl == null) return "This state needs --rules.";
                var game = s.Game!;
                var low = 0;
                foreach (var stack in s.View!.StacksOn(view.Map.Id))
                    foreach (var pid in stack.PieceIds)
                    {
                        if (low >= 3) break;
                        var key = LedgerKey.Piece(pid);
                        var cur = Ledger.Get(game.State, key, "fuel");
                        if (cur == null) continue;
                        game.Execute(new LedgerDelta(key, "fuel", cur.Value, 1, "manual", null, null, "forced march", null));
                        low++;
                    }
                window.Actions!.NextPhase();
                Pump(300);
                CloseCalcWindows(window);
                window.FlushAlerts();
                Pump(400);
                return window.Alerts.ChipTexts.Count > 0 ? null : "No alert chips appeared.";
            }
            case "tree":
                Pump(400);
                return window.ModuleTreeShown ? null : "The module tree is hidden.";
            case "floatlog":
                window.Commands.Execute("dice.1d6");
                window.Commands.Execute("dice.2d6");
                if (window.Log is not { } lp) return "No log panel.";
                Pump(500);
                lp.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Pump(400);
                Target = window.LogFloat;
                return Target == null ? "The log did not pop out." : null;
            case "floatphases":
            {
                if (ToCombat(window) is { } e3) return e3;
                window.Phases.PopButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Pump(500);
                Target = window.PhaseFloat;
                return Target == null ? "The phase panel did not pop out." : null;
            }
            case "ledger":
            case "ledger-counters":
            {
                if (window.OpenLedger() is not { } lw) return "State 'ledger' needs --rules.";
                Pump(400);
                lw.SelectTab("units");   // the tab used last is remembered per module; the picture always shows the units
                Pump(300);
                lw.CountersButton.IsChecked = name == "ledger-counters";   // remembered per module, so set it explicitly
                Pump(name == "ledger-counters" ? 1500 : 300);   // the counter pictures are drawn on a worker and posted back
                Target = lw;
                return null;
            }
            case "edittabs":
            {
                if (window.OpenLedger() is not { } lw2) return "State 'edittabs' needs --rules.";
                Pump(300);
                var dlg = new Quartermaster.Desktop.Ledgers.EditTabsDialog(lw2.Prefs.Tabs, s.Rules, 0, true);
                dlg.Show(lw2);
                Pump(300);
                Target = dlg;
                return null;
            }
            case "outliner":
                if (window.Outliner == null) return "No outliner.";
                if (!window.OutlinerShown) window.ClickMenu("&Outliner");
                Pump(600);
                return window.OutlinerShown ? null : "The outliner did not show.";
            case "inventory":
                window.ClickMenu("&Inventory");
                Pump(600);
                Target = window.InventoryTool;
                return Target == null ? "The inventory did not open." : null;
            case "notes":
            {
                window.ClickMenu("&Notes");
                Pump(300);
                if (window.NotesTool is not { } nw) return "The notes did not open.";
                nw.Type("Fuel is short in the north." + Environment.NewLine + "Watch the river crossing on turn 3.");
                Pump(300);
                Target = nw;
                return null;
            }
            case "diagnostics":
                s.Log.Add(DiagnosticSeverity.Warning, "demo", "An invented warning for the picture.");
                s.Log.Add(DiagnosticSeverity.Error, "demo", "An invented error for the picture.");
                Pump(400);
                window.ClickMenu("&Diagnostics");
                Pump(400);
                Target = window.DiagnosticsTool;
                return Target == null ? "The diagnostics did not open." : null;
            case "overview":
                window.ClickMenu("&Overview");
                Pump(800);
                Target = window.OverviewTool;
                return Target == null ? "The overview did not open." : null;
            case "export-choice":
            {
                var dlg = new ExportChoiceDialog(view.Map.Name ?? "Map");
                dlg.Show(window);
                Pump(300);
                Target = dlg;
                return null;
            }
            case "chart":
            {
                if (s.Model.Charts.Count == 0) return "State 'chart' needs a module with a chart (the documents fixture, see build/port-screenshots.ps1).";
                window.OpenChart(s.Model.Charts[0]);
                Pump(600);
                Target = window.ChartWindows.Values.FirstOrDefault();
                return Target == null ? "The chart window did not open." : null;
            }
            case "document":
            case "document-search":
            {
                var doc = s.Model.Documents.FirstOrDefault(d => d.Kind == DocumentKind.Pdf);
                if (doc == null) return "State 'document' needs a module with a PDF document (the documents fixture, see build/port-screenshots.ps1).";
                var search = name == "document-search";
                window.OpenDocument(doc);
                if (!window.DocumentWindows.TryGetValue(doc.EntryName, out var dw)) return "The document window did not open.";
                var viewer = dw.Viewer;
                if (search) viewer.GoToPage(2, "supply");   // the viewer has no search box (search pane is stage 4): the page is drawn with the term highlighted
                var until = DateTime.UtcNow.AddSeconds(20);
                do PumpUntil(() => viewer.Rendering.IsCompleted && !viewer.Busy, 500); while (DateTime.UtcNow < until && (viewer.Busy || viewer.PageCount == 0));
                Pump(400);
                Target = dw;
                return null;
            }
            case "workbench":
            case "workbench-formulas":
            case "workbench-phases":
            {
                if (s.Rules == null) return $"State '{name}' needs --rules.";
                var wb = OpenWorkbench(window);
                if (wb == null) return "The workbench did not open.";
                var rules = s.Rules;
                if (name == "workbench") { SelectWorkbenchTab(wb, "Tables"); if (rules.Tables.Count > 0) wb.ShowTable(rules.Tables[0].Id); }
                else if (name == "workbench-formulas") { SelectWorkbenchTab(wb, "Formulas"); if (rules.Formulas.Count > 0) wb.ShowFormula(rules.Formulas[0].Id); }
                else { SelectWorkbenchTab(wb, "Phases"); if (rules.Phases.Count > 0) wb.ShowPhase(rules.Phases[0].Id); }
                Pump(600);
                Target = wb;
                return null;
            }
            case "settings":
            case "settings-general":
            case "settings-game":
            case "settings-display":
            case "settings-map":
            case "settings-dice":
            case "settings-toolbar":
            case "settings-keyboard":
            case "settings-folders":
            case "settings-ocr":
            {
                // Pages in window order; plain "settings" is the first page, as Tools > Settings opens it.
                var page = name == "settings" ? 0 : Array.IndexOf(SettingsPages, name["settings-".Length..]);
                SettingsWindow? win = null;
                window.ShowSettingsHook = w => { win = w; return Task.CompletedTask; };
                _ = window.ShowSettingsAsync();
                window.ShowSettingsHook = null;
                if (win == null) return "The settings window did not open.";
                win.Show(window);
                win.SelectPage(page);
                // The folders page measures sizes off the UI thread; wait for them (as WinForms waits for FoldersLoadedForSmoke).
                if (name == "settings-folders" && win.Pages.OfType<FoldersPage>().FirstOrDefault() is { } fp) PumpUntil(() => fp.SizesLoaded, 10000);
                Pump(300);
                Target = win;
                return null;
            }
            case "tour":
                // Help > Interface Tour: the balloon on step 1, captured as its own (borderless, transparent-backed) window.
                _tourShown = window.SettingsStore.Current.TourShown;   // closing the balloon marks the tour shown; Cleanup puts it back
                window.ShowTour();
                Pump(300);
                if (window.Tour is not { } tour) return "The tour did not open.";
                tour.ApplyTransparencyFallback(WindowTransparencyLevel.None);   // the headless frame has no alpha: no black arrow strip
                Pump(100);
                Target = tour;
                return null;
            case "ocr-dropdown":
            {
                // The scan pane's OCR panel with its language list open: the real catalog's display names and "More languages…".
                if (s.Rules == null) return "State 'ocr-dropdown' needs --rules.";
                var wb = OpenWorkbench(window, 1280, 760);
                if (wb == null) return "The workbench did not open.";
                _scanDir = Path.Combine(Path.GetTempPath(), "qm-shot-scan-" + Guid.NewGuid().ToString("N"));
                var (path, region) = WriteInventedTable(_scanDir);
                var open = wb.OpenScanFile(path);
                if (!PumpUntil(() => open.IsCompleted && wb.Scan.Loading.IsCompleted && wb.Scan.Image != null, 30000)) return "The scan did not open.";
                wb.Scan.Pane.CommitRegion(region);
                wb.Scan.Ocr.LanguageBox.IsDropDownOpen = true;
                Pump(600);
                Target = wb;
                return null;
            }
            case "workbench-search":
            {
                if (s.Rules == null) return "State 'workbench-search' needs the phases fixture (--rules phases.qrules, see build/port-screenshots.ps1).";
                var wb = OpenWorkbench(window);
                if (wb == null) return "The workbench did not open.";
                if (!PumpUntil(() => wb.SearchIndexForTests != null, 60000)) return "The rulebook index was not ready after 60 s.";
                wb.RunSearch("supply");
                Pump(300);
                var panel = wb.SearchPanelForTests;
                if (!panel.SelectHit(2)) return "The search found no hit on page 2.";
                panel.ActivateSelected();
                var viewer = wb.Viewer;
                var until = DateTime.UtcNow.AddSeconds(20);
                do PumpUntil(() => viewer.Rendering.IsCompleted && !viewer.Busy, 500); while (DateTime.UtcNow < until && (viewer.Busy || viewer.PageCount == 0));
                if (s.Rules.Formulas.Count > 0) { SelectWorkbenchTab(wb, "Formulas"); wb.ShowFormula(s.Rules.Formulas[0].Id); }
                Pump(600);
                Target = wb;
                return null;
            }
            case "workbench-scan":
            {
                if (s.Rules == null) return "State 'workbench-scan' needs --rules.";
                var wb = OpenWorkbench(window, 1280, 760);
                if (wb == null) return "The workbench did not open.";
                _scanDir = Path.Combine(Path.GetTempPath(), "qm-shot-scan-" + Guid.NewGuid().ToString("N"));
                var (path, region) = WriteInventedTable(_scanDir);
                var open = wb.OpenScanFile(path);
                if (!PumpUntil(() => open.IsCompleted && wb.Scan.Loading.IsCompleted && wb.Scan.Image != null, 30000)) return "The scan did not open.";
                wb.Scan.Pane.CommitRegion(region);
                wb.Scan.Ocr.ShowGridTab();
                wb.Scan.Ocr.ClickReadForSmoke();
                PumpUntil(() => wb.Scan.Ocr.LastRead.IsCompleted, 60000);
                Pump(600);
                Console.Error.WriteLine("ocr status: " + wb.Scan.Ocr.StatusText);
                Target = wb;
                return null;
            }
            case "log":
                window.Commands.Execute("dice.1d6");
                window.Commands.Execute("dice.2d6");
                if (window.FindMenuItem("Log") is { IsChecked: not true }) window.ClickMenu("Log");   // shown by default; keeps the state explicit
                PumpUntil(() => window.Log is { Rows.Count: > 1 }, 2000);   // the panel refreshes on a 150 ms debounce
                Pump(300);
                return null;
            case "los":
                view.LosEnabled = true;
                var a = view.TranslatePoint(new Point(300, 250), window) ?? default;
                var b = view.TranslatePoint(new Point(520, 330), window) ?? default;
                window.MouseDown(a, MouseButton.Left);
                window.MouseMove(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), RawInputModifiers.LeftMouseButton);
                window.MouseMove(b, RawInputModifiers.LeftMouseButton);
                window.MouseUp(b, MouseButton.Left);
                Pump(100);
                return view.HasLosLine ? null : "The line of sight line did not appear.";
            case "menu":
            case "hover":
                var piece = FindPiece(s, view, name == "hover" && s.Rules != null);
                if (piece == null) return "No suitable counter is visible.";
                if (name == "menu")
                {
                    view.SelectOnly(piece.Value.Id);
                    var at = view.TranslatePoint(piece.Value.At, window) ?? default;
                    window.MouseDown(at, MouseButton.Right);   // a real right-click, so the menu opens at the pointer
                    window.MouseUp(at, MouseButton.Right);
                    Pump(100);
                    return view.OpenMenu != null ? null : "The context menu did not open.";
                }
                return Hover(window, view, s, piece.Value);
            default:
                return $"Unknown state '{name}' (known: {string.Join(", ", Names)}).";
        }
    }

    static string? Hover(MainWindow window, MapView view, ModuleSession s, (string Id, Point At) piece)
    {
        view.HoverDelayMs = 50;
        if (s.Rules != null && s.Game is { } game)
        {
            var key = LedgerKey.Piece(piece.Id);
            game.Execute(new LedgerDelta(key, "fuel", Ledger.Get(game.State, key, "fuel") ?? 0, 6, "manual", null, null, "starting load", null));
            game.Execute(new LedgerDelta(key, "fuel", 6, 4, "manual", null, null, "forced march", null));
            Pump(50);
        }
        var at = view.TranslatePoint(piece.At, window) ?? default;
        window.MouseMove(at);
        if (!PumpUntil(() => view.Popup != null, 3000)) return "The hover popup did not open.";
        var popup = view.Popup!;
        if (popup.Layout.HotLines.Count > 0)
        {
            var line = popup.Layout.HotLines.FirstOrDefault(h => h.Tip.Title.StartsWith("Fuel ", StringComparison.Ordinal)) ?? popup.Layout.HotLines[0];
            if (popup.TranslatePoint(new Point(line.X + 2, line.Top + 2), window) is { } onLine) window.MouseMove(onLine);
            Pump(100);
        }
        return null;
    }

    /// <summary>A lone counter well inside the view; with <paramref name="unit"/> only one that is a unit (these have ledger lines under a rules pack).</summary>
    static (string Id, Point At)? FindPiece(ModuleSession s, MapView view, bool unit)
    {
        var st = s.View!;
        var renderer = new PieceRenderer(s.Images);
        foreach (var stack in st.StacksOn(view.Map.Id))
        {
            if (stack.PieceIds.Count != 1) continue;
            var id = stack.PieceIds[0];
            var sp = view.View.ToScreen(stack.Pos);
            if (sp.X < 20 || sp.Y < 20 || sp.X > view.Bounds.Width - 20 || sp.Y > view.Bounds.Height - 20) continue;
            if (HitTester.PieceAt(st, view.Map, s.Model, s.Images, stack.Pos) != id) continue;
            if (unit)
            {
                var p = st.Pieces[id];
                if (!PieceDetails.For(p, renderer.Types.Get(p.TypeString)).Properties.Contains(("Kind", "Unit"))) continue;
            }
            return (id, new Point(sp.X, sp.Y));
        }
        return null;
    }

    static Quartermaster.Desktop.Workbench.WorkbenchWindow? OpenWorkbench(MainWindow window, int w = 1100, int h = 720)
    {
        window.ShowWorkbench();
        var wb = window.WorkbenchTool;
        if (wb == null) return null;
        wb.Width = w;
        wb.Height = h;
        Pump(500);
        return wb;
    }

    static void SelectWorkbenchTab(Quartermaster.Desktop.Workbench.WorkbenchWindow wb, string header)
    {
        foreach (var item in wb.Tabs.Items.OfType<TabItem>())
            if ((string?)item.Header == header) { wb.Tabs.SelectedItem = item; break; }
        Pump(200);
    }

    /// <summary>An invented 6 x 4 table of two-digit numbers with borders, drawn with Skia (200 DPI look); returns the file and the table's region in pixels.</summary>
    static (string Path, System.Drawing.Rectangle Region) WriteInventedTable(string dir)
    {
        const int cols = 6, rows = 4, cw = 200, ch = 70, margin = 60;
        Directory.CreateDirectory(dir);
        var rng = new Random(7341);
        using var bmp = new SkiaSharp.SKBitmap(cols * cw + 2 * margin, rows * ch + 2 * margin);
        using (var canvas = new SkiaSharp.SKCanvas(bmp))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            using var face = SkiaSharp.SKTypeface.FromFamilyName("DejaVu Sans") ?? SkiaSharp.SKTypeface.Default;
            using var font = new SkiaSharp.SKFont(face, 11f * 200f / 72f) { Edging = SkiaSharp.SKFontEdging.Antialias };
            using var ink = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true };
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < cols; c++)
                {
                    var text = rng.Next(10, 100).ToString();
                    var x = margin + c * cw + (cw - font.MeasureText(text)) / 2f;
                    var y = margin + r * ch + ch / 2f - (font.Metrics.Ascent + font.Metrics.Descent) / 2f;
                    canvas.DrawText(text, x, y, SkiaSharp.SKTextAlign.Left, font, ink);
                }
            using var pen = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, StrokeWidth = 2, Style = SkiaSharp.SKPaintStyle.Stroke };
            for (var r = 0; r <= rows; r++) canvas.DrawLine(margin, margin + r * ch, margin + cols * cw, margin + r * ch, pen);
            for (var c = 0; c <= cols; c++) canvas.DrawLine(margin + c * cw, margin, margin + c * cw, margin + rows * ch, pen);
        }
        var path = Path.Combine(dir, "invented-table.png");
        using (var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)) File.WriteAllBytes(path, data.ToArray());
        return (path, new System.Drawing.Rectangle(margin, margin, cols * cw, rows * ch));
    }

    static void Pump(int ms) => PumpUntil(() => false, ms);

    static bool PumpUntil(Func<bool> done, int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            if (done()) return true;
            using var slice = new CancellationTokenSource(10);
            Dispatcher.UIThread.MainLoop(slice.Token);
        }
        Dispatcher.UIThread.RunJobs();
        return done();
    }
}
