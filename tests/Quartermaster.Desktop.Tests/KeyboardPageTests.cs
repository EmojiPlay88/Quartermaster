using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Preferences;
using Quartermaster.Render;
using Xunit;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4b task 4: the Keyboard settings page. Headless only.</summary>
[Collection("MacFlag")]
public sealed class KeyboardPageTests
{
    readonly FakeDialogs _dialogs = new();

    static CommandRegistry Reg()
    {
        var r = new CommandRegistry();
        foreach (var d in CommandDefaults.All) r.Register(d);
        return r;
    }

    static Settings With(params (string, string)[] o) => new() { ShortcutOverrides = o.ToDictionary(x => x.Item1, x => x.Item2) };

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    static void WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(done());
    }

    (KeyboardPage Page, Window Host) Show(Settings s)
    {
        var host = new Window();
        var page = new KeyboardPage(Reg(), _dialogs, () => host);
        page.Load(s);
        host.Content = page;
        host.Show();
        return (page, host);
    }

    static (string Id, string Label, string Shortcut, string Default, bool Flagged) Row(KeyboardPage p, string id) => p.RowsForTest.Single(r => r.Id == id);

    [AvaloniaFact]
    public void Columns_and_rows()
    {
        var old = ShortcutGestures.IsMac;
        try
        {
            ShortcutGestures.IsMac = true;
            var (p, host) = Show(With(("dice.2d6", "")));
            Assert.Equal("Keyboard", p.Title);
            Assert.Equal(new double[] { 250, 150, 100 }, p.ColumnWidths.ToArray());
            Assert.Equal(CommandDefaults.All.Count, p.RowsForTest.Count);
            var d = Row(p, "dice.2d6");
            Assert.Equal("(none)", d.Shortcut);
            var one = Row(p, "dice.1d6");
            Assert.Equal(ShortcutGestures.Display("F5", true), one.Shortcut);
            var st = Row(p, "tools.settings");
            Assert.Contains("⌘", st.Shortcut);
            Assert.Contains("⌘", st.Default);
            host.Close();
        }
        finally { ShortcutGestures.IsMac = old; }
    }

    [AvaloniaFact]
    public void Set_clear_reset_reset_all()
    {
        var (p, host) = Show(With());
        Assert.Equal(new object[] { "Set...", "Clear", "Reset", "Reset all" }, new[] { p.SetButton, p.ClearButton, p.ResetButton, p.ResetAllButton }.Select(b => b.Content).ToArray());
        Assert.False(p.SetButton.IsEnabled);
        Assert.False(p.ClearButton.IsEnabled);
        Assert.False(p.ResetButton.IsEnabled);
        Assert.True(p.ResetAllButton.IsEnabled);

        p.SelectId("dice.1d6");
        Assert.True(p.SetButton.IsEnabled && p.ClearButton.IsEnabled && p.ResetButton.IsEnabled);
        _dialogs.Shortcuts.Enqueue("F7");
        Click(p.SetButton);
        WaitFor(() => Row(p, "dice.1d6").Shortcut == ShortcutGestures.Display("F7"));
        Assert.Single(_dialogs.ShortcutPrompts);
        Assert.Equal("dice.1d6", p.List.SelectedItem is ListBoxItem { Tag: string t } ? t : null);

        _dialogs.Shortcuts.Enqueue(null); // cancelled
        p.SetSelectedAsync().GetAwaiter().GetResult();
        Assert.Equal(ShortcutGestures.Display("F7"), Row(p, "dice.1d6").Shortcut);

        Click(p.ClearButton);
        Assert.Equal("(none)", Row(p, "dice.1d6").Shortcut);
        Click(p.ResetButton);
        Assert.Equal(ShortcutGestures.Display("F5"), Row(p, "dice.1d6").Shortcut);

        p.SelectId("dice.2d6");
        Click(p.ClearButton);
        Click(p.ResetAllButton);
        Assert.Equal(ShortcutGestures.Display("F5"), Row(p, "dice.1d6").Shortcut);
        Assert.NotEqual("(none)", Row(p, "dice.2d6").Shortcut);
        Assert.Empty(p.Collect(With()).ShortcutOverrides);

        // double-click = Set
        _dialogs.Shortcuts.Enqueue("F8");
        var item = p.List.Items.OfType<ListBoxItem>().Single(i => (string?)i.Tag == "dice.2d6");
        var pt = item.TranslatePoint(new Point(20, 8), host)!.Value;
        for (var i = 0; i < 2; i++)
        {
            Avalonia.Headless.HeadlessWindowExtensions.MouseDown(host, pt, Avalonia.Input.MouseButton.Left);
            Avalonia.Headless.HeadlessWindowExtensions.MouseUp(host, pt, Avalonia.Input.MouseButton.Left);
        }
        WaitFor(() => Row(p, "dice.2d6").Shortcut == ShortcutGestures.Display("F8"));
        host.Close();
    }

    [AvaloniaFact]
    public void Conflict_rows_red_and_status_text()
    {
        var (p, host) = Show(With());
        Assert.Equal("", p.StatusText);
        p.SelectId("dice.2d6");
        _dialogs.Shortcuts.Enqueue("F5"); // same as dice.1d6
        p.SetSelectedAsync().GetAwaiter().GetResult();
        Assert.True(Row(p, "dice.1d6").Flagged);
        Assert.True(Row(p, "dice.2d6").Flagged);
        Assert.False(Row(p, "tools.settings").Flagged);
        Assert.Equal(new KeyboardEditModelFor(p).Problem, p.StatusText);
        Assert.Contains("F5 is used by", p.StatusText);
        var tb = p.GetVisualDescendantsOf<TextBlock>().Single(t => t.Text == p.StatusText);
        Assert.Equal(Brushes.Firebrick, tb.Foreground);
        host.Close();
    }

    sealed class KeyboardEditModelFor
    {
        public string? Problem { get; }
        public KeyboardEditModelFor(KeyboardPage p)
        {
            var m = new KeyboardEditModel(Reg());
            m.Load(p.Collect(With()));
            Problem = m.Problem();
        }
    }

    [AvaloniaFact]
    public async Task Conflict_blocks_ok_and_selects_keyboard_page()
    {
        var applied = 0;
        SettingsWindow? w = null;
        var kb = new KeyboardPage(Reg(), _dialogs, () => w);
        var ctx = new SettingsContext(new[] { ("save", "Save") }, Path.GetTempPath(), () => null, _ => Task.CompletedTask, _dialogs);
        var pages = SettingsWindow.StandardPages(ctx, kb);
        w = new SettingsWindow(With(), _ => applied++, pages, null, _dialogs);
        w.Show();
        w.SelectPage(0);
        kb.SelectId("dice.2d6");
        _dialogs.Shortcuts.Enqueue("F5");
        await kb.SetSelectedAsync();
        Click(w.OkButton);
        WaitFor(() => _dialogs.Warnings.Count == 1);
        Assert.Equal(6, w.SelectedPage);
        Assert.Equal(0, applied);
        Assert.Equal("Settings", _dialogs.Warnings[0].Caption);
        Assert.StartsWith("These shortcuts need fixing before the settings can be applied:", _dialogs.Warnings[0].Message);
        Assert.Equal(kb.Validate(), _dialogs.Warnings[0].Message);
        w.Close();
    }

    [AvaloniaFact]
    public void Unknown_override_ids_survive_keyboard_apply()
    {
        var (p, host) = Show(With(("mod.unknown", "Ctrl+Q"), ("dice.1d6", "F7")));
        p.SelectId("dice.2d6");
        _dialogs.Shortcuts.Enqueue("F9");
        p.SetSelectedAsync().GetAwaiter().GetResult();
        var map = p.Collect(With(("mod.unknown", "Ctrl+Q"), ("dice.1d6", "F7"))).ShortcutOverrides;
        Assert.Equal("Ctrl+Q", map["mod.unknown"]);
        Assert.Equal("F7", map["dice.1d6"]);
        Assert.Equal("F9", map["dice.2d6"]);
        host.Close();
    }
}
