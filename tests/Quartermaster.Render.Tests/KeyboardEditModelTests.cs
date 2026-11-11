using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class KeyboardEditModelTests
{
    static CommandRegistry Reg()
    {
        var r = new CommandRegistry();
        foreach (var d in CommandDefaults.All) r.Register(d);
        return r;
    }

    static Settings With(params (string, string)[] o) =>
        new Settings { ShortcutOverrides = o.ToDictionary(x => x.Item1, x => x.Item2) };

    [Fact]
    public void Load_takes_effective_shortcuts()
    {
        var m = new KeyboardEditModel(Reg());
        m.Load(With(("tools.settings", "Ctrl+Shift+P")));
        Assert.Equal("Ctrl+Shift+P", m.Effective("tools.settings"));
        Assert.Equal("F5", m.Effective("dice.1d6"));
        Assert.Equal("", m.Effective("phase.stepBackTo"));
        Assert.Equal(CommandDefaults.All.Count, m.Commands.Count);
    }

    [Fact]
    public void Collect_writes_only_differences()
    {
        var m = new KeyboardEditModel(Reg());
        m.Load(With());
        m.Assign("dice.1d6", "F7");
        m.Assign("dice.2d6", "");
        m.Assign("tools.settings", "ctrl+,"); // equals default once normalised
        var map = m.Collect(With()).ShortcutOverrides;
        Assert.Equal(2, map.Count);
        Assert.Equal("F7", map["dice.1d6"]);
        Assert.Equal("", map["dice.2d6"]);
        Assert.DoesNotContain("tools.settings", map.Keys);
    }

    [Fact]
    public void Reset_and_reset_all_restore_defaults()
    {
        var m = new KeyboardEditModel(Reg());
        m.Load(With(("dice.1d6", "F7"), ("dice.2d6", "")));
        m.Reset("dice.1d6");
        Assert.Equal("F5", m.Effective("dice.1d6"));
        m.ResetAll();
        Assert.Equal("F6", m.Effective("dice.2d6"));
        Assert.Empty(m.Collect(With(("dice.1d6", "F7"))).ShortcutOverrides);
    }

    [Fact]
    public void Collect_keeps_unknown_ids()
    {
        var m = new KeyboardEditModel(Reg());
        m.Load(With(("x.gone", "F9")));
        var map = m.Collect(With(("x.gone", "F9"))).ShortcutOverrides;
        Assert.Equal("F9", map["x.gone"]);
    }

    [Fact]
    public void Problem_text_matches_winforms()
    {
        var m = new KeyboardEditModel(Reg());
        m.Load(With());
        Assert.Null(m.Problem());
        Assert.Null(m.ValidateText());
        m.Assign("dice.2d6", "F5");
        Assert.Equal("F5 is used by Roll 1d6, Roll 2d6", m.Problem());
        Assert.Equal(new[] { "dice.1d6", "dice.2d6" }, m.Flagged().OrderBy(x => x));
        m.Assign("file.saveGame", "Ctrl+W");
        var p = "Ctrl+W is reserved by the window (Save game)";
        Assert.Equal("Ctrl+W is reserved by the window (Save game); F5 is used by Roll 1d6, Roll 2d6", m.Problem());
        Assert.Equal("These shortcuts need fixing before the settings can be applied:\n" + p + "\nF5 is used by Roll 1d6, Roll 2d6", m.ValidateText());
    }
}
