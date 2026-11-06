using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Rules.Tests;

public class TableTests
{
    private static TableDef OneD(string name, string[] rows, string[] vals) => new()
    {
        Id = "t1", Name = name, Kind = TableKind.OneD,
        RowKeys = rows.ToList(), ColumnKeys = new() { "Value" },
        Cells = vals.Select(v => new List<string> { v }).ToList(),
    };

    [Fact] public void Key_Number() { var k = TableLookup.ParseKey(" 5 "); Assert.Equal(TableKeyKind.Number, k.Kind); Assert.Equal(5, k.Lo); }

    [Fact] public void Key_Range() { var k = TableLookup.ParseKey("3-5"); Assert.Equal(TableKeyKind.Range, k.Kind); Assert.Equal(3, k.Lo); Assert.Equal(5, k.Hi);
        var r = TableLookup.ParseKey("5-3"); Assert.Equal(3, r.Lo); Assert.Equal(5, r.Hi);
        var n = TableLookup.ParseKey("-2--1"); Assert.Equal(TableKeyKind.Range, n.Kind); Assert.Equal(-2, n.Lo); Assert.Equal(-1, n.Hi);
        Assert.Equal(TableKeyKind.Range, TableLookup.ParseKey("3 - 5").Kind); }

    [Fact] public void Key_EnDashRange() { foreach (var s in new[] { "3\u20135", "3 \u2014 5", "3\u22125" }) { var k = TableLookup.ParseKey(s); Assert.Equal(TableKeyKind.Range, k.Kind); Assert.Equal(3, k.Lo); Assert.Equal(5, k.Hi); } }

    [Fact] public void Key_AtLeast_Variants() { foreach (var s in new[] { "7+", "\u22657", ">=7" }) { var k = TableLookup.ParseKey(s); Assert.Equal(TableKeyKind.AtLeast, k.Kind); Assert.Equal(7, k.Lo); } }

    [Fact] public void Key_AtMost_Variants() { foreach (var s in new[] { "\u22642", "<=2", "2-" }) { var k = TableLookup.ParseKey(s); Assert.Equal(TableKeyKind.AtMost, k.Kind); Assert.Equal(2, k.Hi); } }

    [Fact] public void Key_NegativeNumber() { var k = TableLookup.ParseKey("-2"); Assert.Equal(TableKeyKind.Number, k.Kind); Assert.Equal(-2, k.Lo);
        Assert.Equal(-2, TableLookup.ParseKey("\u22122").Lo); }

    [Fact] public void Key_Text() { var k = TableLookup.ParseKey("Forest"); Assert.Equal(TableKeyKind.Text, k.Kind); Assert.Equal("Forest", k.Text);
        Assert.False(TableLookup.Matches(TableLookup.ParseKey("A"), Value.Of(1))); }

    [Fact] public void Lookup_OneD_Range()
    {
        var t = OneD("Rates", new[] { "1-2", "3-5", "6+" }, new[] { "1", "2", "3" });
        Assert.Equal(2, TableLookup.Lookup(t, Value.Of(4), null).Number);
        Assert.Equal(3, TableLookup.Lookup(t, Value.Of(9), Value.Of("ignored")).Number);
    }

    [Fact] public void Lookup_TwoD_TextColumn()
    {
        var t = new TableDef { Name = "Terrain", Kind = TableKind.TwoD,
            RowKeys = new() { "1-3", "4+" }, ColumnKeys = new() { "Foot", "Horse" },
            Cells = new() { new() { "1", "2" }, new() { "3", "4" } } };
        Assert.Equal(4, TableLookup.Lookup(t, Value.Of(5), Value.Of("horse")).Number);
        Assert.Equal(1, TableLookup.Lookup(t, Value.Of(2), Value.Of(" FOOT ")).Number);
    }

    [Fact] public void Lookup_NoMatch_ErrorMessage()
    {
        var t = OneD("Rates", new[] { "1-2" }, new[] { "1" });
        var e = Assert.Throws<EvalError>(() => TableLookup.Lookup(t, Value.Of(7), null));
        Assert.Equal("No row for 7 in Rates", e.Message);
    }

    [Fact] public void Lookup_TwoD_MissingColumn_Error()
    {
        var t = new TableDef { Name = "Terrain", Kind = TableKind.TwoD,
            RowKeys = new() { "1" }, ColumnKeys = new() { "Foot" }, Cells = new() { new() { "1" } } };
        var e1 = Assert.Throws<EvalError>(() => TableLookup.Lookup(t, Value.Of(1), null));
        Assert.Equal("Table Terrain needs a column", e1.Message);
        var e2 = Assert.Throws<EvalError>(() => TableLookup.Lookup(t, Value.Of(1), Value.Of("Boat")));
        Assert.Equal("No column for Boat in Terrain", e2.Message);
    }

    [Fact] public void Lookup_CellText_ReturnsText()
    {
        var t = OneD("Rates", new[] { "1" }, new[] { " Yes " });
        var v = TableLookup.Lookup(t, Value.Of(1), null);
        Assert.Equal(ValueKind.Text, v.Kind); Assert.Equal("Yes", v.Text);
        var m = OneD("Rates", new[] { "1" }, new[] { "\u22123" });
        Assert.Equal(-3, TableLookup.Lookup(m, Value.Of(1), null).Number);
    }

    [Fact] public void Paste_Tsv_WithHeaderAndKeys()
    {
        var g = TablePaste.Parse("\uFEFF\tFoot\tHorse\r\n1-3\t1\t2\r\n\r\n4+\t3\t4\r\n", true, true);
        Assert.Equal(new[] { "Foot", "Horse" }, g.ColumnKeys);
        Assert.Equal(new[] { "1-3", "4+" }, g.RowKeys);
        Assert.Equal(new[] { "3", "4" }, g.Cells[1]);
    }

    [Fact] public void Paste_Csv_Quoted()
    {
        var g = TablePaste.Parse("Key,Value\n\"a,b\",\"say \"\"hi\"\"\"\nc,d", true, true);
        Assert.Equal(new[] { "Value" }, g.ColumnKeys);
        Assert.Equal(new[] { "a,b", "c" }, g.RowKeys);
        Assert.Equal("say \"hi\"", g.Cells[0][0]);
    }

    [Fact] public void Paste_SpaceColumns()
    {
        var g = TablePaste.Parse("Big Name  5  6\nOther one  7  8", false, true);
        Assert.Equal(new[] { "Big Name", "Other one" }, g.RowKeys);
        Assert.Equal(new[] { "5", "6" }, g.Cells[0]);
    }

    [Fact] public void Paste_RaggedRowsPadded()
    {
        var g = TablePaste.Parse("a\tb\tc\nd\te", false, false);
        Assert.Equal(new[] { "d", "e", "" }, g.Cells[1]);
        var h = TablePaste.Parse("x\ty\t\n1\t2\t", false, false);
        Assert.Equal(2, h.Cells[0].Count);
    }
}
