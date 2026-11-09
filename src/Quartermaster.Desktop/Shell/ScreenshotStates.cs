using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Quartermaster.Core;
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
    public static readonly string[] Names = { "menu", "hover", "los", "log", "rules" };

    /// <returns>An error text, or null when the state is in place.</returns>
    public static string? Apply(string name, MainWindow window)
    {
        var view = window.MapTabs.ActiveView;
        var s = window.Session;
        if (view == null || s?.Game == null) return "No map to put into the state.";
        switch (name)
        {
            case "rules":
                return s.Rules == null ? "State 'rules' needs --rules or --new-rules." : null;
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
