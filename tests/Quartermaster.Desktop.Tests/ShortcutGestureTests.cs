using Avalonia.Input;
using Quartermaster.Desktop.Input;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Tests;

[Collection("MacFlag")]
public class ShortcutGestureTests
{
    [Fact]
    public void Ctrl_maps_to_meta_on_mac()
    {
        var g = ShortcutGestures.Parse("Ctrl+Shift+W", true);
        Assert.NotNull(g);
        Assert.Equal(KeyModifiers.Meta | KeyModifiers.Shift, g!.KeyModifiers);
        Assert.Equal(Key.W, g.Key);
    }

    [Fact]
    public void Ctrl_stays_control_elsewhere()
    {
        var g = ShortcutGestures.Parse("Ctrl+Shift+W", false);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, g!.KeyModifiers);
        Assert.Equal(Key.W, g.Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Function_key(bool mac)
    {
        var g = ShortcutGestures.Parse("F5", mac);
        Assert.Equal(Key.F5, g!.Key);
        Assert.Equal(KeyModifiers.None, g.KeyModifiers);
    }

    [Fact]
    public void Comma()
    {
        Assert.Equal(Key.OemComma, ShortcutGestures.Parse("Ctrl+,", false)!.Key);
        Assert.Equal(Key.OemComma, ShortcutGestures.Parse("ctrl+,", true)!.Key);
    }

    [Fact]
    public void Alt_is_option_on_mac()
    {
        Assert.Equal(KeyModifiers.Alt, ShortcutGestures.Parse("Alt+Enter", true)!.KeyModifiers);
        Assert.Equal(Key.Enter, ShortcutGestures.Parse("Alt+Enter", true)!.Key);
    }

    [Fact]
    public void Display_mac_symbols()
    {
        Assert.Equal("⇧⌘W", ShortcutGestures.Display("Ctrl+Shift+W", true));
        Assert.Equal("⌥⇧⌘S", ShortcutGestures.Display("Ctrl+Alt+Shift+S", true));
        Assert.Equal("⌘,", ShortcutGestures.Display("Ctrl+,", true));
        Assert.Equal("F5", ShortcutGestures.Display("F5", true));
        Assert.Equal("Ctrl+Shift+W", ShortcutGestures.Display("ctrl+shift+w", false));
        Assert.Equal("", ShortcutGestures.Display("Ctrl+", true));
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Bogus")]
    public void Garbage_is_null(string spec)
    {
        Assert.Null(ShortcutGestures.Parse(spec, false));
        Assert.Null(ShortcutGestures.Parse(spec, true));
    }

    [Fact]
    public void IsMac_is_overridable()
    {
        var old = ShortcutGestures.IsMac;
        try
        {
            ShortcutGestures.IsMac = true;
            Assert.Equal(KeyModifiers.Meta, ShortcutGestures.Parse("Ctrl+S")!.KeyModifiers);
            ShortcutGestures.IsMac = false;
            Assert.Equal(KeyModifiers.Control, ShortcutGestures.Parse("Ctrl+S")!.KeyModifiers);
        }
        finally { ShortcutGestures.IsMac = old; }
    }

    [Fact]
    public void Every_token_parses()
    {
        foreach (var s in new[] { "Tab", "Enter", "Space", "Esc", "Backspace", "Delete", "Insert", "Home", "End", "PageUp", "PageDown", "Left", "Right", "Up", "Down",
                                  "A", "Z", "0", "9", "F1", "F24", ",", ".", ";", "'", "/", "\\", "[", "]", "-", "=", "`" })
        {
            Assert.NotNull(ShortcutGestures.Parse(s, false));
            Assert.NotNull(ShortcutGestures.Parse("Ctrl+" + s, true));
        }
    }

    [Fact]
    public void Every_default_parses()
    {
        foreach (var d in CommandDefaults.All.Where(d => d.DefaultShortcut.Length > 0))
        {
            Assert.True(ShortcutGestures.Parse(d.DefaultShortcut, true) != null, d.Id + " mac");
            Assert.True(ShortcutGestures.Parse(d.DefaultShortcut, false) != null, d.Id + " non-mac");
        }
    }
}
