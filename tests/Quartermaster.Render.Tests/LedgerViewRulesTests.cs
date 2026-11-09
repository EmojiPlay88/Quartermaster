using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;

namespace Quartermaster.Render.Tests;

public class LedgerViewRulesTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-lvr-" + Guid.NewGuid().ToString("N"));
    readonly List<FixtureModule> _fx = new();
    readonly List<ModuleSession> _ss = new();
    public LedgerViewRulesTests() => Directory.CreateDirectory(_tmp);
    public void Dispose()
    {
        foreach (var s in _ss) s.Dispose();
        foreach (var f in _fx) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    ModuleSession Make()
    {
        var fx = PhaseFixture.Module();
        _fx.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), Path.Combine(_tmp, "root"));
        _ss.Add(s);
        s.LoadSetup(s.Model.Setups[0]);
        s.NewRules("t");
        s.UpdateRules(_ => PhaseFixture.Pack(s.Archive.Sha256));
        return s;
    }

    static LedgerRowEx R(string name, string kind, params double?[] v) =>
        new(new LedgerRow("k-" + name, name, kind, v), "p-" + name, null, null, false);
    static LedgerRowEx H(string name) => new(new LedgerRow("h-" + name, name, "", Array.Empty<double?>()), null, null, null, true);

    [Fact]
    public void Sort_keeps_group_headers_and_puts_blanks_last()
    {
        var rows = new[] { H("A"), R("x", "k", (double?)null), R("y", "k", 5), R("z", "k", 1), H("B"), R("w", "k", 9), R("v", "k", (double?)null) };
        foreach (var asc in new[] { true, false })
        {
            var s = LedgerViewRules.Sort(rows, 2, asc);
            Assert.True(s[0].IsGroupHeader);
            Assert.True(s[4].IsGroupHeader);
            Assert.Equal(asc ? new[] { "z", "y", "x" } : new[] { "y", "z", "x" }, s.Skip(1).Take(3).Select(r => r.Row.Name));
            Assert.Equal(new[] { "w", "v" }, s.Skip(5).Select(r => r.Row.Name));
        }
    }

    [Fact]
    public void Sort_is_stable()
    {
        var rows = new[] { R("c", "k", 1), R("a", "k", 1), R("b", "k", 1) };
        Assert.Equal(new[] { "c", "a", "b" }, LedgerViewRules.Sort(rows, 2, true).Select(r => r.Row.Name));
        Assert.Equal(new[] { "c", "a", "b" }, LedgerViewRules.Sort(rows, 2, false).Select(r => r.Row.Name));
        Assert.Equal(new[] { "a", "b", "c" }, LedgerViewRules.Sort(rows, 0, true).Select(r => r.Row.Name));
    }

    [Fact]
    public void Parse_invariant_then_current_culture()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.True(LedgerViewRules.TryParseNumber("1,5", out var v)); Assert.Equal(1.5, v);
            Assert.True(LedgerViewRules.TryParseNumber("2.5", out v)); Assert.Equal(2.5, v);
            Assert.False(LedgerViewRules.TryParseNumber("abc", out _));
            Assert.False(LedgerViewRules.TryParseNumber("NaN", out _));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Manual_edit_parses_comma_decimal_in_de_DE()
    {
        var s = Make();
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var c = LedgerViewRules.CheckManualEdit(s, LedgerKey.Piece("b1"), "fuel", 1, "2,5", "Fuel");
            Assert.True(c.Ok); Assert.Equal(2.5, c.Value); Assert.False(c.Unchanged);
            Assert.Equal("Why is Fuel being set to " + LedgerTable.FormatNumber(2.5) + "? (required)", c.NotePrompt);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Manual_edit_not_a_number_text()
    {
        var c = LedgerViewRules.CheckManualEdit(Make(), LedgerKey.Piece("b1"), "fuel", 1, "abc");
        Assert.False(c.Ok);
        Assert.Equal("'abc' is not a number.", c.Error);
        Assert.Null(c.NotePrompt);
    }

    [Fact]
    public void Manual_edit_unchanged_writes_nothing()
    {
        var c = LedgerViewRules.CheckManualEdit(Make(), LedgerKey.Piece("b1"), "fuel", 3, "3");
        Assert.True(c.Unchanged);
        Assert.Null(c.NotePrompt);
        Assert.Null(c.Error);
    }

    [Fact]
    public void Manual_edit_other_side_refused()
    {
        var s = Make();
        s.SetViewSides(new HashSet<string> { "red" });
        var c = LedgerViewRules.CheckManualEdit(s, LedgerKey.Piece("b1"), "fuel", 1, "4");
        Assert.False(c.Ok);
        Assert.Equal("That value belongs to a side you don't hold.", c.Error);
    }

    [Fact]
    public void Csv_file_name_sanitised()
    {
        Assert.Equal("ledger-units.csv", LedgerViewRules.CsvFileName("Units"));
        Assert.Equal("ledger-★-my-tab-a-b.csv", LedgerViewRules.CsvFileName("★ My Tab a/b"));
        Assert.Equal("ledger-depots-&-global.csv", LedgerViewRules.CsvFileName("Depots & Global"));
    }

    [Fact]
    public void Tab_title_with_counts()
    {
        var units = new LedgerTabDef("units", "Units", LedgerTabKind.Units, null);
        var every = new LedgerTabDef("everything", "Everything", LedgerTabKind.Everything, null);
        var user = new LedgerTabDef("user:Mine", "Mine", LedgerTabKind.User, new UserLedgerTab("Mine", null, Array.Empty<string>(), false, null));
        Assert.Equal($"Units ({1234:N0})", LedgerViewRules.TabTitle(units, 1234));
        Assert.Equal("Units", LedgerViewRules.TabTitle(units, null));
        Assert.Equal("Everything", LedgerViewRules.TabTitle(every, 5));
        Assert.Equal("★ Mine", LedgerViewRules.TabTitle(user, 5));
        Assert.True(LedgerViewRules.SideAndGroupApply(units));
        Assert.True(LedgerViewRules.SideAndGroupApply(user));
        Assert.False(LedgerViewRules.SideAndGroupApply(every));
        Assert.Equal(52, LedgerViewRules.RowHeight(true));
    }

    [Fact]
    public void Filter_banner_text() =>
        Assert.Equal("The unit filter of \"Mine\" does not parse, so this tab shows no rows: oops   (Edit tabs… to fix it)", LedgerViewRules.FilterBanner("Mine", "oops"));

    [Fact]
    public void Same_tabs_compares_all_fields()
    {
        UserLedgerTab T(string n = "a", string? f = null, string[]? c = null, bool h = false, string? no = null) => new(n, f, c ?? new[] { "x" }, h, no);
        Assert.True(LedgerViewRules.SameTabs(new[] { T() }, new[] { T() }));
        Assert.False(LedgerViewRules.SameTabs(new[] { T() }, new[] { T("b") }));
        Assert.False(LedgerViewRules.SameTabs(new[] { T() }, new[] { T(f: "q") }));
        Assert.False(LedgerViewRules.SameTabs(new[] { T() }, new[] { T(c: new[] { "y" }) }));
        Assert.False(LedgerViewRules.SameTabs(new[] { T() }, new[] { T(h: true) }));
        Assert.False(LedgerViewRules.SameTabs(new[] { T() }, new[] { T(no: "n") }));
        Assert.False(LedgerViewRules.SameTabs(new[] { T() }, Array.Empty<UserLedgerTab>()));
    }

    [Fact]
    public void New_tab_names_increment()
    {
        UserLedgerTab T(string n) => new(n, null, Array.Empty<string>(), false, null);
        Assert.Equal("New tab", LedgerViewRules.NewTabName(Array.Empty<UserLedgerTab>()));
        Assert.Equal("New tab 2", LedgerViewRules.NewTabName(new[] { T("New tab") }));
        Assert.Equal("New tab 3", LedgerViewRules.NewTabName(new[] { T("new tab"), T(" New tab 2 ") }));
        Assert.Equal("(no name)", LedgerViewRules.NoNameText);
        Assert.Equal("  (depots & Global)", LedgerViewRules.HolderColumnSuffix);
        var before = new[] { T("a") };
        Assert.Equal("user:b", LedgerViewRules.TabToOpenAfterEdit(before, new[] { T("a"), T("b") }, "units", true));
        Assert.Equal("units", LedgerViewRules.TabToOpenAfterEdit(before, new[] { T("a"), T("b") }, "units", false));
    }
}
