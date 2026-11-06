using Quartermaster.Core;
using Quartermaster.Render;

namespace Quartermaster.Render.Tests;

public class LogFilterTests
{
    static DateTimeOffset Local(int h, int m, int s) => new(new DateTime(2026, 1, 2, h, m, s, DateTimeKind.Local));

    static LogEntry E(long seq, string kind, string text, string? piece = null) =>
        new(seq, kind, text, "local", piece, null, Local(14, 3, (int)seq));

    static readonly IReadOnlyList<LogEntry> Sample = new[]
    {
        E(1, LogKind.Move, "Infantry moved A1 → B2", "p1"),
        E(2, LogKind.Dice, "Attack: 2d6 = 3 + 4 = 7"),
        E(3, LogKind.Move, "Armor moved C3 → D4", "p2"),
        E(4, "weird-kind", "Something odd"),
        E(5, LogKind.System, "Game started"),
    };

    static LogFilterSpec Spec(string search, params string[] kinds) => new(new HashSet<string>(kinds), search);

    [Fact]
    public void Filter_ByKind_And_Search()
    {
        var byKind = LogFilter.Apply(Sample, Spec("", LogKind.Move));
        Assert.Equal(new long[] { 1, 3 }, byKind.Select(e => e.Seq));

        var both = LogFilter.Apply(Sample, Spec("ARMOR", LogKind.Move, LogKind.Dice));
        Assert.Equal(new long[] { 3 }, both.Select(e => e.Seq));

        Assert.Empty(LogFilter.Apply(Sample, Spec("")));
    }

    [Fact]
    public void Filter_UnknownKind_TreatedAsSystem()
    {
        var sys = LogFilter.Apply(Sample, Spec("", LogKind.System));
        Assert.Equal(new long[] { 4, 5 }, sys.Select(e => e.Seq));
        Assert.DoesNotContain(LogFilter.Apply(Sample, Spec("", LogKind.Move)), e => e.Seq == 4);
        Assert.Equal("system", LogFilter.DisplayKind("weird-kind"));
    }

    [Fact]
    public void ToText_Format()
    {
        var text = LogFilter.ToText(new[] { E(9, LogKind.Dice, "Attack: 2d6 = 7"), E(10, "zzz", "odd") });
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("[14:03:09] dice  Attack: 2d6 = 7", lines[0]);
        Assert.Equal("[14:03:10] system  odd", lines[1]);
    }

    [Fact]
    public void ToHtml_EscapesText()
    {
        var html = LogFilter.ToHtml(new[] { E(1, LogKind.Note, "<b>\"x\" & y</b>") });
        Assert.Contains("&lt;b&gt;&quot;x&quot; &amp; y&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("<table", html);
        Assert.Contains("14:03:01", html);
    }
}
