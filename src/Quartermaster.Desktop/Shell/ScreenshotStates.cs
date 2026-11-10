using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Formulas;
using Quartermaster.Desktop.Map;
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
        "ledger", "ledger-counters", "edittabs", "outliner", "inventory", "notes", "diagnostics", "overview", "export-choice", "chart", "document", "document-search" };

    /// <summary>Set by a state whose picture is a window of its own (a dialog, the formula window, a floating panel) instead of the main window.</summary>
    public static Window? Target { get; private set; }

    /// <summary>Closes the extra windows a state opened (before the main window closes).</summary>
    public static void Cleanup(MainWindow window)
    {
        if (window.Session is { } s) FormulaWindows.CloseAllFor(s);
        Target?.Close();
        Target = null;
    }

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
