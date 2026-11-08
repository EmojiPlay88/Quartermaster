using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Quartermaster.Core;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 2a task 8: the log panel (docked at the bottom, View > Log), its kind toggles, search, export and the chat box. Headless.</summary>
[Collection("MacFlag")]
public sealed class LogPanelTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-log-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly FakeDialogs _dialogs = new();

    public LogPanelTests() { ShortcutGestures.IsMac = false; Directory.CreateDirectory(_tmp); }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Demo(string file = "Riverland-Demo.vmod", [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    static void Pump() => Dispatcher.UIThread.RunJobs();

    MainWindow Game(bool rules = false)
    {
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null) { Width = 1024, Height = 768 };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(Demo()));
        w.Session!.SetRoller((n, _) => Enumerable.Repeat(4, n).ToList());
        w.StartSetup(null);
        if (rules) w.Session.OpenRules(Demo("Riverland-Demo.qrules"));
        Pump();
        return w;
    }

    static LogPanel Panel(MainWindow w)
    {
        var p = w.Log!;
        p.Reload();
        return p;
    }

    [AvaloniaFact]
    public void Log_shows_moves_and_rolls()
    {
        var w = Game();
        var piece = w.Session!.Game!.State.Pieces.Values.First();
        w.Actions!.Move(new[] { new MovePieces(new[] { piece.Id }, piece.MapId, new PointD(piece.Pos.X + 40, piece.Pos.Y + 40), null) });
        w.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Pump();
        var p = Panel(w);

        Assert.True(w.LogShown);
        Assert.True(p.IsVisible && w.FindControl<Border>("LogHost")!.IsVisible);
        Assert.Contains(p.Rows, r => r.Kind == LogKind.Move);
        var roll = Assert.Single(p.Rows, r => r.Kind == LogKind.Dice);
        Assert.Equal(w.Session.Game.State.Log[^1].Text, roll.Text);
        Assert.Equal(w.Session.View!.Log.Count, p.Rows.Count);   // every kind on: the whole view log
        Assert.Equal(p.Rows.Count, p.List.ItemCount);

        // View > Log toggles the dock (label and shortcut as WinForms), Ctrl+Shift+L too.
        var item = w.FindControl<Menu>("MainMenu")!.Items.OfType<MenuItem>().Select(m => m.Items.OfType<MenuItem>().FirstOrDefault(i => MenuText.ToWinForms((string)i.Header!) == "&Log")).First(i => i != null)!;
        Assert.Equal(new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift), item.InputGesture);
        Assert.True(item.IsChecked);
        w.ClickMenu("&Log");
        Pump();
        Assert.False(w.LogShown);
        Assert.False(item.IsChecked);
        w.KeyPress(Key.L, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.L, null);
        Pump();
        Assert.True(w.LogShown);
        Assert.True(item.IsChecked);
    }

    [AvaloniaFact]
    public void Kind_toggle_filters()
    {
        var w = Game();
        w.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Pump();
        var s = w.Session!;
        s.Game!.Execute(new AddLogLine(LogKind.Chat, "hello there"));
        var p = Panel(w);
        Assert.Equal(LogFilter.AllKinds.Select(LogFilter.KindLabel), p.KindButtons.Values.Select(b => (string)b.Content!));
        Assert.Contains(p.Rows, r => r.Kind == LogKind.Dice);

        p.KindButtons[LogKind.Dice].IsChecked = false;
        p.Reload();
        Assert.DoesNotContain(p.Rows, r => r.Kind == LogKind.Dice);
        Assert.Contains(p.Rows, r => r.Kind == LogKind.Chat);

        p.Search.Text = "HELLO";   // case-insensitive text search
        p.Reload();
        Assert.Equal("hello there", Assert.Single(p.Rows).Text);

        p.Search.Text = "";
        p.KindButtons[LogKind.Dice].IsChecked = true;
        p.Reload();
        Assert.Contains(p.Rows, r => r.Kind == LogKind.Dice);

        // Show-moves off hides move/place/delete rows only in the display.
        var piece = s.Game.State.Pieces.Values.First();
        w.Actions!.Move(new[] { new MovePieces(new[] { piece.Id }, piece.MapId, new PointD(piece.Pos.X + 40, piece.Pos.Y + 40), null) });
        p.Reload();
        Assert.Contains(p.Rows, r => r.Kind == LogKind.Move);
        p.ApplySettings(new Settings { LogShowMoves = false });
        Assert.DoesNotContain(p.Rows, r => r.Kind == LogKind.Move);
        Assert.Contains(s.View!.Log, r => r.Kind == LogKind.Move);
    }

    [AvaloniaFact]
    public void Log_filters_private_entries_for_viewer()
    {
        var w = Game(rules: true);
        var s = w.Session!;
        s.Game!.State.Log.Add(new LogEntry(9001, LogKind.Calc, "south secret working 42", "local", null, null, DateTimeOffset.UtcNow, SideId: "south"));
        s.SetViewSides(new HashSet<string> { "north" });
        Pump();
        var p = Panel(w);
        Assert.DoesNotContain(p.Rows, r => (r.Text ?? "").Contains("secret") || (r.Text ?? "").Contains("42"));
        // Exactly what the view gives this viewer (the same list WinForms binds), nothing from the full state.
        Assert.Equal(LogFilter.Apply(s.View!.Log, new LogFilterSpec(LogFilter.AllKinds.ToHashSet(), "")).Select(r => r.Text), p.Rows.Select(r => r.Text));
        Assert.Contains(p.Rows, r => r.Kind == LogKind.Calc);   // redacted, not silently dropped
        Assert.DoesNotContain(p.CopyText(), "secret");

        s.SetViewSides(null);   // everything visible again: the real line shows
        Pump();
        p.Reload();
        Assert.Contains(p.Rows, r => (r.Text ?? "").Contains("south secret"));
    }

    [AvaloniaFact]
    public async Task Export_text_and_html()
    {
        var w = Game();
        w.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
        Pump();
        var p = Panel(w);
        var txt = Path.Combine(_tmp, "out.txt");
        var html = Path.Combine(_tmp, "out.html");
        _dialogs.SaveFiles.Enqueue(txt);
        _dialogs.SaveFiles.Enqueue(html);

        await p.ExportAsync();
        await p.ExportAsync();

        Assert.Equal(new[] { "game-log", "game-log" }, _dialogs.SuggestedNames.TakeLast(2));
        Assert.Equal("Export log", _dialogs.PickerTitles[^1]);
        Assert.Equal(LogFilter.ToText(p.Rows), File.ReadAllText(txt));
        var h = File.ReadAllText(html);
        Assert.Equal(LogFilter.ToHtml(p.Rows), h);
        Assert.StartsWith("<table", h);

        _dialogs.SaveFiles.Enqueue(null);   // cancelled: nothing written
        var before = Directory.GetFiles(_tmp).Length;
        await p.ExportAsync();
        Assert.Equal(before, Directory.GetFiles(_tmp).Length);
    }

    [AvaloniaFact]
    public void Chat_sends_line()
    {
        var w = Game();
        var p = Panel(w);
        Assert.Null(p.Chat);   // offline: WinForms shows no chat line
        var sent = new List<string>();
        p.SetChat(sent.Add);
        var chat = Assert.IsType<ChatBox>(p.Chat);
        Assert.Equal("Chat — press Enter to send", chat.Input.PlaceholderText);

        chat.Input.Text = "  hello\tteam \n";
        chat.Input.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(new[] { "hello team" }, sent);
        Assert.Equal("", chat.Input.Text ?? "");

        chat.Input.Text = "   ";   // blank: nothing sent
        chat.SendButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Single(sent);

        chat.Input.Text = "second";
        chat.SendButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { "hello team", "second" }, sent);

        var failing = new ChatBox(_ => throw new InvalidOperationException("offline"));
        failing.Input.Text = "keep me";
        failing.Send();
        Assert.Equal("keep me", failing.Input.Text);   // not handed on: the text stays

        p.SetChat(null);
        Assert.Null(p.Chat);
    }

    [AvaloniaFact]
    public void Large_log_stays_responsive()
    {
        var w = Game();
        var s = w.Session!;
        var log = s.Game!.State.Log;
        for (var i = 0; i < 10_000; i++)
            log.Add(new LogEntry(100_000 + i, i % 3 == 0 ? LogKind.Move : LogKind.Chat, "entry " + i, "local", null, null, DateTimeOffset.UtcNow, Turn: 1 + i / 100, Phase: "Supply"));
        s.SetViewSides(null);
        var p = w.Log!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        p.Reload();
        w.UpdateLayout();
        Pump();
        sw.Stop();
        Assert.True(p.Rows.Count >= 10_000);
        Assert.True(sw.ElapsedMilliseconds < 5000, "reload took " + sw.ElapsedMilliseconds + " ms");
        // Virtualised: only the rows in view have containers.
        var realised = p.List.GetVisualDescendants().OfType<ListBoxItem>().Count();
        Assert.InRange(realised, 1, 200);

        sw.Restart();
        p.Search.Text = "entry 99";
        p.Reload();
        p.KindButtons[LogKind.Move].IsChecked = false;
        p.Reload();
        Pump();
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 5000, "filter took " + sw.ElapsedMilliseconds + " ms");
        Assert.All(p.Rows, r => Assert.Contains("entry 99", r.Text));
    }
}
