using Quartermaster.Rules.Tables;

namespace Quartermaster.Rules.Tests;

public class TableEditTests
{
    private static TableDef Two() => new()
    {
        Id = "t", Name = "Rates", Kind = TableKind.TwoD,
        RowKeys = new() { "1", "2" }, ColumnKeys = new() { "A", "B" },
        Cells = new() { new() { "10", "20" }, new() { "30", "40" } },
    };

    [Fact] public void New_OneD_HasValueColumnAndOneRow()
    {
        var t = TableEdit.New("X", TableKind.OneD);
        Assert.False(string.IsNullOrEmpty(t.Id));
        Assert.Equal(new[] { "Value" }, t.ColumnKeys);
        Assert.Single(t.RowKeys);
        Assert.Single(t.Cells[0]);
    }

    [Fact] public void New_TwoD_HasOneRowOneColumn()
    {
        var t = TableEdit.New("X", TableKind.TwoD);
        Assert.Single(t.RowKeys); Assert.Single(t.ColumnKeys); Assert.Single(t.Cells[0]);
    }

    [Fact] public void AddRow_AppendsEmptyRow_OriginalUntouched()
    {
        var o = Two();
        var t = TableEdit.AddRow(o, "3");
        Assert.Equal(3, t.RowKeys.Count); Assert.Equal("3", t.RowKeys[2]);
        Assert.Equal(new[] { "", "" }, t.Cells[2]);
        Assert.Equal(2, o.RowKeys.Count);
    }

    [Fact] public void RemoveRow_Removes_AndIgnoresBadIndex()
    {
        var t = TableEdit.RemoveRow(Two(), 0);
        Assert.Equal(new[] { "2" }, t.RowKeys); Assert.Equal("30", t.Cells[0][0]);
        Assert.Equal(2, TableEdit.RemoveRow(Two(), 9).RowKeys.Count);
    }

    [Fact] public void AddColumn_TwoD_PadsEveryRow()
    {
        var t = TableEdit.AddColumn(Two(), "C");
        Assert.Equal(new[] { "A", "B", "C" }, t.ColumnKeys);
        Assert.All(t.Cells, r => Assert.Equal(3, r.Count));
    }

    [Fact] public void AddRemoveColumn_OneD_Ignored()
    {
        var o = TableEdit.New("X", TableKind.OneD);
        Assert.Single(TableEdit.AddColumn(o, "C").ColumnKeys);
        Assert.Single(TableEdit.RemoveColumn(o, 0).ColumnKeys);
    }

    [Fact] public void RemoveColumn_TwoD()
    {
        var t = TableEdit.RemoveColumn(Two(), 0);
        Assert.Equal(new[] { "B" }, t.ColumnKeys); Assert.Equal("20", t.Cells[0][0]);
    }

    [Fact] public void SetCell_SetKey_Work()
    {
        var t = TableEdit.SetCell(Two(), 1, 0, "99");
        Assert.Equal("99", t.Cells[1][0]);
        t = TableEdit.SetRowKey(t, 0, "5-6");
        Assert.Equal("5-6", t.RowKeys[0]);
        t = TableEdit.SetColumnKey(t, 1, "Z");
        Assert.Equal("Z", t.ColumnKeys[1]);
        Assert.Equal(t.Cells, TableEdit.SetCell(t, 7, 7, "x").Cells);
    }

    [Fact] public void SetColumnKeys_ResizesCells()
    {
        var t = TableEdit.SetColumnKeys(Two(), new[] { "Q" });
        Assert.Equal(new[] { "Q" }, t.ColumnKeys); Assert.All(t.Cells, r => Assert.Single(r));
        t = TableEdit.SetColumnKeys(Two(), new[] { "A", "B", "C" });
        Assert.All(t.Cells, r => Assert.Equal(3, r.Count));
    }

    [Fact] public void ChangeKind_OneDToTwoD_KeepsValueColumn()
    {
        var o = TableEdit.SetCell(TableEdit.New("X", TableKind.OneD), 0, 0, "7");
        var t = TableEdit.ChangeKind(o, TableKind.TwoD);
        Assert.Equal(TableKind.TwoD, t.Kind); Assert.Equal(new[] { "Value" }, t.ColumnKeys); Assert.Equal("7", t.Cells[0][0]);
    }

    [Fact] public void ChangeKind_TwoDToOneD_KeepsFirstColumnOnly()
    {
        var t = TableEdit.ChangeKind(Two(), TableKind.Dice);
        Assert.Equal(new[] { "Value" }, t.ColumnKeys);
        Assert.Equal("10", t.Cells[0][0]); Assert.Equal("30", t.Cells[1][0]);
        Assert.All(t.Cells, r => Assert.Single(r));
    }

    [Fact] public void LosesData_TwoDWithExtraColumns()
    {
        Assert.True(TableEdit.ChangeLosesData(Two(), TableKind.OneD));
        Assert.False(TableEdit.ChangeLosesData(Two(), TableKind.TwoD));
        Assert.False(TableEdit.ChangeLosesData(TableEdit.New("X", TableKind.OneD), TableKind.TwoD));
    }

    [Fact] public void ApplyPaste_TwoD_ReplacesGrid()
    {
        var g = TablePaste.Parse("x\tA\tB\n1\t5\t6\n2\t7\t8", true, true);
        var t = TableEdit.ApplyPaste(Two(), g);
        Assert.Equal(new[] { "A", "B" }, t.ColumnKeys); Assert.Equal(new[] { "1", "2" }, t.RowKeys);
        Assert.Equal("8", t.Cells[1][1]); Assert.Equal("Rates", t.Name);
    }

    [Fact] public void ApplyPaste_OneD_TakesFirstColumn_PadsMissingKeys()
    {
        var g = TablePaste.Parse("5\t6\n7\t8", false, false);
        var t = TableEdit.ApplyPaste(TableEdit.New("X", TableKind.OneD), g);
        Assert.Equal(new[] { "Value" }, t.ColumnKeys); Assert.Equal(new[] { "", "" }, t.RowKeys);
        Assert.Equal("7", t.Cells[1][0]);
    }

    [Theory]
    [InlineData("5", "5")]
    [InlineData("3-5", "3 to 5")]
    [InlineData("7+", "at least 7")]
    [InlineData("<=2", "at most 2")]
    [InlineData("Armor", "text \"Armor\"")]
    [InlineData("2.50", "2.5")]
    public void Hint_Describes(string raw, string expected) =>
        Assert.Equal(expected, KeyHint.Describe(TableLookup.ParseKey(raw)));

    [Fact] public void KeyHint_Empty_IsEmptyMarker() => Assert.Equal("(empty key)", KeyHint.Describe(TableLookup.ParseKey("")));
}
