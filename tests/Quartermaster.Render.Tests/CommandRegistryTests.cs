using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class CommandRegistryTests
{
    static CommandRegistry Sample()
    {
        var r = new CommandRegistry();
        r.Register(new CommandDef("a", "Alpha", "Ctrl+Shift+W"));
        r.Register(new CommandDef("b", "Beta", "F5"));
        r.Register(new CommandDef("c", "Gamma", "L"));
        return r;
    }

    static IReadOnlyDictionary<string, string> Ov(params (string, string)[] p) => p.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void EffectiveShortcut_DefaultOrOverride()
    {
        var r = Sample();
        Assert.Equal("Ctrl+Shift+W", r.EffectiveShortcut("a", Ov()));
        Assert.Equal("Ctrl+Alt+Shift+K", r.EffectiveShortcut("a", Ov(("a", "shift+alt+ctrl+k"))));
        Assert.Equal("", r.EffectiveShortcut("b", Ov(("b", ""))));          // unbound
        Assert.Equal("F5", r.EffectiveShortcut("b", Ov(("b", "Ctrl+Nonsense")))); // bad override falls back
        Assert.Equal("", r.EffectiveShortcut("missing", Ov()));
    }

    [Fact]
    public void CommandRegistry_Conflict_Detected()
    {
        var r = Sample();
        Assert.Empty(r.Conflicts(Ov()));
        var c = Assert.Single(r.Conflicts(Ov(("c", "f5"))));
        Assert.Equal("F5", c.Key);
        Assert.Equal(new[] { "b", "c" }, c.Ids.OrderBy(x => x));
        Assert.Empty(r.Conflicts(Ov(("b", ""), ("c", ""))));   // unbound never conflicts
    }

    [Fact]
    public void Reserved_ReportedAsConflict()
    {
        var r = Sample();
        var c = Assert.Single(r.Conflicts(Ov(("a", "Ctrl+W"))));
        Assert.Equal("Ctrl+W", c.Key);
        Assert.Equal(new[] { "a" }, c.Ids);
        Assert.Contains("Ctrl+Tab", CommandRegistry.Reserved);
        Assert.Contains("Ctrl+Shift+Tab", CommandRegistry.Reserved);
        Assert.Single(r.Conflicts(Ov(("a", "ctrl+shift+tab"))));
    }

    [Fact]
    public void TryParseShortcut_Valid_Invalid()
    {
        foreach (var (text, canon) in new[] { ("Ctrl+Shift+W", "Ctrl+Shift+W"), ("f5", "F5"), ("l", "L"), ("Ctrl+,", "Ctrl+,"), ("Ctrl+Right", "Ctrl+Right"), ("alt+ctrl+Enter", "Ctrl+Alt+Enter"), ("Escape", "Esc") })
        {
            Assert.True(CommandRegistry.TryNormalize(text, out var n), text);
            Assert.Equal(canon, n);
        }
        foreach (var bad in new[] { "", "Ctrl+", "Ctrl+Shift", "Ctrl+Nonsense", "Ctrl+A+B", "Ctrl+Ctrl+A", "F25", "+" })
            Assert.False(CommandRegistry.TryNormalize(bad, out _), bad);
        Assert.True(CommandRegistry.IsTextEntryKey("L"));
        Assert.False(CommandRegistry.IsTextEntryKey("F5"));
        Assert.False(CommandRegistry.IsTextEntryKey("Ctrl+L"));
    }

    [Theory]
    [InlineData("Alt+F4")]
    [InlineData("Esc")]
    [InlineData("Tab")]
    [InlineData("Shift+Tab")]
    [InlineData("Enter")]
    [InlineData("Alt+F")]
    [InlineData("Alt+Z")]
    public void Reserved_WindowKeys_AreConflicts(string key)
    {
        Assert.True(CommandRegistry.IsReserved(key));
        var r = Sample();
        var c = Assert.Single(r.Conflicts(Ov(("b", key))));
        Assert.Equal(key, c.Key);
    }

    [Theory]
    [InlineData("F5")]
    [InlineData("L")]
    [InlineData("Ctrl+L")]
    [InlineData("Alt+Shift+F")]
    [InlineData("Alt+F5")]
    [InlineData("Alt+1")]
    public void Reserved_OrdinaryKeys_AreNot(string key) => Assert.False(CommandRegistry.IsReserved(key));
}

public class ShortcutGateTests
{
    [Theory]
    [InlineData("Ctrl+Right", true, false, false)]   // word navigation in a text box
    [InlineData("Ctrl+Shift+Left", true, false, false)]
    [InlineData("Ctrl+Right", false, false, true)]
    [InlineData("Delete", true, false, false)]
    [InlineData("Ctrl+C", true, false, false)]
    [InlineData("Ctrl+Z", true, false, false)]
    [InlineData("Ctrl+C", false, false, true)]
    [InlineData("F5", true, false, true)]
    [InlineData("Ctrl+S", true, false, true)]
    [InlineData("L", false, true, true)]
    [InlineData("L", false, false, false)]
    [InlineData("L", true, true, false)]
    public void ShouldDispatch(string shortcut, bool text, bool map, bool expected) =>
        Assert.Equal(expected, ShortcutGate.ShouldDispatch(shortcut, text, map));
}
