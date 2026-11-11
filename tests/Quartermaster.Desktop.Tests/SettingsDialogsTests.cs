using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Preferences;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4b task 2: the shortcut capture dialog, the colour dialog and the start folder of the open picker. Headless only.</summary>
[Collection("MacFlag")]
public sealed class SettingsDialogsTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;

    public SettingsDialogsTests() => ShortcutGestures.IsMac = false;

    public void Dispose() => ShortcutGestures.IsMac = _wasMac;

    static Window Owner()
    {
        var w = new Window { Width = 400, Height = 300 };
        w.Show();
        return w;
    }

    static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    static void Press(Window w, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        w.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = w });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Capture_esc_cancels()
    {
        var owner = Owner();
        var d = new ShortcutCaptureDialog("Open");
        var t = d.ShowDialog<string?>(owner);
        Press(d, Key.Escape);
        Assert.Null(await t);
        owner.Close();
    }

    [AvaloniaTheory]
    [InlineData(Key.Back)]
    [InlineData(Key.Delete)]
    public async Task Capture_backspace_and_delete_clear(Key key)
    {
        var owner = Owner();
        var d = new ShortcutCaptureDialog("Open");
        var t = d.ShowDialog<string?>(owner);
        Press(d, key);
        Assert.Equal("", await t);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Capture_ctrl_shift_k_returns_text()
    {
        var owner = Owner();
        var d = new ShortcutCaptureDialog("Open");
        var t = d.ShowDialog<string?>(owner);
        Press(d, Key.K, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal("Ctrl+Shift+K", await t);
        owner.Close();
    }

    [AvaloniaTheory]
    [InlineData(Key.Delete, KeyModifiers.Control, "Ctrl+Delete")]
    [InlineData(Key.Escape, KeyModifiers.Shift, "Shift+Esc")]
    [InlineData(Key.Back, KeyModifiers.Control, "Ctrl+Backspace")]
    [InlineData(Key.Tab, KeyModifiers.None, "Tab")]
    [InlineData(Key.Enter, KeyModifiers.None, "Enter")]
    [InlineData(Key.Left, KeyModifiers.None, "Left")]
    public async Task Capture_modified_and_navigation_keys_are_captured(Key key, KeyModifiers mods, string expected)
    {
        var owner = Owner();
        var d = new ShortcutCaptureDialog("Open");
        var t = d.ShowDialog<string?>(owner);
        Press(d, key, mods);
        Assert.Equal(expected, await t);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Capture_ignores_keys_after_the_result()
    {
        var owner = Owner();
        var d = new ShortcutCaptureDialog("Open");
        var t = d.ShowDialog<string?>(owner);
        Press(d, Key.K, KeyModifiers.Control);
        Press(d, Key.Escape);
        Assert.Equal("Ctrl+K", await t);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Capture_modifier_alone_is_swallowed()
    {
        var owner = Owner();
        var d = new ShortcutCaptureDialog("Open");
        var t = d.ShowDialog<string?>(owner);
        Press(d, Key.LeftCtrl, KeyModifiers.Control);
        Assert.True(d.IsVisible);
        Assert.False(t.IsCompleted);
        Press(d, Key.Escape);
        Assert.Null(await t);
        owner.Close();
    }

    [AvaloniaFact]
    public void Capture_texts()
    {
        var owner = Owner();
        var d = new ShortcutCaptureDialog("Zoom in");
        Assert.Equal("Set shortcut", d.Title);
        Assert.Equal("Press the new shortcut for \"Zoom in\".\nEsc cancels; Backspace or Delete clears.", d.Prompt.Text);
        d.ShowDialog<string?>(owner);
        Assert.Equal(new Avalonia.Size(320, 80), d.ClientSize);
        d.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Dialogs_facade_shows_the_capture_dialog()
    {
        IDialogs d = new Dialogs();
        var owner = Owner();
        var task = d.AskShortcutAsync(owner, "x");
        Dispatcher.UIThread.RunJobs();
        Assert.False(task.IsCompleted);
        var shown = owner.OwnedWindows.OfType<ShortcutCaptureDialog>().Single();
        Press(shown, Key.Escape);
        Assert.Null(await task);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Open_file_passes_initial_folder()
    {
        var fake = new FakeDialogs();
        var owner = Owner();
        IDialogs d = fake;
        var filters = new[] { new FileFilter("All", "*.*") };
        await d.OpenFileAsync(owner, "t", filters);
        Assert.Null(fake.LastOpenFolder);
        await d.OpenFileAsync(owner, "t", filters, @"C:\x");
        Assert.Equal(@"C:\x", fake.LastOpenFolder);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Colour_swatch_click_then_ok_returns_hex()
    {
        var owner = Owner();
        var d = new ColourDialog("#FFA500");
        Assert.Equal("Colour", d.Title);
        Assert.Equal(48, d.Swatches.Count);
        Assert.Equal("#FFA500", d.HexBox.Text);
        var t = d.ShowDialog<string?>(owner);
        Click(d.Swatches[8]);   // row 2, column 1: red
        Assert.Equal("#FF0000", d.HexBox.Text);
        Assert.True(d.OkButton.IsEnabled);
        Click(d.OkButton);
        Assert.Equal("#FF0000", await t);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Colour_typed_hex_is_upper_cased()
    {
        var owner = Owner();
        var d = new ColourDialog("#FFA500");
        var t = d.ShowDialog<string?>(owner);
        d.HexBox.Text = "#a1b2c3";
        Click(d.OkButton);
        Assert.Equal("#A1B2C3", await t);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Colour_bad_hex_disables_ok()
    {
        var owner = Owner();
        var d = new ColourDialog("#FFA500");
        var t = d.ShowDialog<string?>(owner);
        d.HexBox.Text = "#12";
        Assert.False(d.OkButton.IsEnabled);
        d.HexBox.Text = "#123456";
        Assert.True(d.OkButton.IsEnabled);
        d.HexBox.Text = "red";
        Assert.False(d.OkButton.IsEnabled);
        Click(d.CancelButton);
        Assert.Null(await t);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Colour_cancel_returns_null()
    {
        var owner = Owner();
        var d = new ColourDialog("#FFA500");
        var t = d.ShowDialog<string?>(owner);
        Click(d.CancelButton);
        Assert.Null(await t);
        owner.Close();
    }

    [AvaloniaFact]
    public void Colour_bad_initial_starts_with_ok_disabled()
    {
        var d = new ColourDialog("nonsense");
        Assert.False(d.OkButton.IsEnabled);
    }
}
