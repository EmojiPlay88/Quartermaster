using Quartermaster.Render.Ocr;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Render.Tests;

public class OcrGridTests
{
    // Words are 10 tall, columns start at x=0,100,200; rows at y=0,20,40,60.
    private static OcrWord W(string t, double x, double y, double w = 30) => new(t, x, y, w, 10);

    private static List<OcrWord> Table(params string[][] rows)
    {
        var list = new List<OcrWord>();
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
                if (rows[r][c].Length > 0) list.Add(W(rows[r][c], c * 100, r * 20));
        return list;
    }

    [Fact]
    public void Grid_AlignedTable_3x4()
    {
        var words = Table(new[] { "Roll", "Red", "Blue" }, new[] { "1", "10", "20" },
            new[] { "2", "11", "21" }, new[] { "3", "12", "22" });
        words.Reverse();
        var g = OcrResultModel.BuildGrid(words);
        Assert.Equal(4, g.Length);
        Assert.All(g, r => Assert.Equal(3, r.Length));
        Assert.Equal(new[] { "Roll", "Red", "Blue" }, g[0]);
        Assert.Equal(new[] { "3", "12", "22" }, g[3]);
    }

    [Fact]
    public void Grid_RaggedRows_PadsEmpty()
    {
        var g = OcrResultModel.BuildGrid(Table(new[] { "a", "b", "c" }, new[] { "d", "", "f" }, new[] { "g" }));
        Assert.Equal(new[] { "d", "", "f" }, g[1]);
        Assert.Equal(new[] { "g", "", "" }, g[2]);
    }

    [Fact]
    public void Grid_MultiWordCell_StaysOneCell()
    {
        var words = new List<OcrWord>
        {
            W("Red", 0, 0), W("Wall", 36, 0), W("5", 200, 0),
            W("Blue", 0, 20), W("Gate", 36, 20), W("6", 200, 20),
        };
        var g = OcrResultModel.BuildGrid(words);
        Assert.Equal(new[] { "Red Wall", "5" }, g[0]);
        Assert.Equal(new[] { "Blue Gate", "6" }, g[1]);
    }

    [Fact]
    public void Grid_ForcedColumns_SplitsAtWidestGaps()
    {
        var words = new List<OcrWord>
        {
            W("a", 0, 0), W("b", 40, 0), W("c", 300, 0),
            W("d", 0, 20), W("e", 40, 20), W("f", 300, 20),
        };
        var two = OcrResultModel.BuildGrid(words, 2);
        Assert.Equal(new[] { "a b", "c" }, two[0]);
        var one = OcrResultModel.BuildGrid(words, 1);
        Assert.Equal(new[] { "a b c" }, one[0]);
        var three = OcrResultModel.BuildGrid(words, 3);
        Assert.Equal(new[] { "a", "b", "c" }, three[0]);
        Assert.Empty(OcrResultModel.BuildGrid(new List<OcrWord>()));
        Assert.Empty(OcrResultModel.BuildGrid(new List<OcrWord> { W("  ", 0, 0) }));
    }

    [Fact]
    public void Cleanup_OnlyNumericLooking()
    {
        Assert.Equal("10", OcrCleanup.Fix("1O"));
        Assert.Equal("Oil", OcrCleanup.Fix("Oil"));
        Assert.Equal("2d6+1", OcrCleanup.Fix("2d6+l"));
        Assert.Equal("3-5", OcrCleanup.Fix("3–5"));
        Assert.Equal("S", OcrCleanup.Fix("S"));
        Assert.Equal("1000", OcrCleanup.Fix("1 000"));
        Assert.Equal("-15", OcrCleanup.Fix("—l5"));
        Assert.False(OcrCleanup.IsNumericLooking("OO"));
        Assert.True(OcrCleanup.IsNumericLooking("5S%"));
    }

    [Fact]
    public void Cleanup_NotAppliedToEditedCells()
    {
        var st = new OcrGridState(Table(new[] { "k", "1O" }), true);
        Assert.Equal("10", st.Cells[0][1]);
        st.Edit(0, 1, "1O");
        Assert.Equal("1O", st.Cells[0][1]);
        Assert.True(st.IsEdited(0, 1));
        Assert.False(st.IsEdited(0, 0));
        Assert.Contains("1O", st.ToTsv());

        var off = new OcrGridState(Table(new[] { "k", "1O" }), false);
        Assert.Equal("1O", off.Cells[0][1]);
    }

    [Fact]
    public void State_HasEdits_AndRebuildDropsEdits()
    {
        var words = new List<OcrWord> { W("a", 0, 0), W("b", 40, 0), W("c", 300, 0) };
        var st = new OcrGridState(words, true);
        Assert.False(st.HasEdits);
        Assert.Equal(new[] { "a b", "c" }, st.Cells[0]);
        st.Edit(0, 0, "zz");
        Assert.True(st.HasEdits);
        st.SplitColumn(0);
        Assert.False(st.HasEdits);
        Assert.Equal(new[] { "a", "b", "c" }, st.Cells[0]);
        st.Edit(0, 2, "x");
        st.MergeColumns(0);
        Assert.False(st.HasEdits);
        Assert.Equal(new[] { "a b", "c" }, st.Cells[0]);
        st.Edit(0, 0, "q");
        st.SetColumns(1);
        Assert.False(st.HasEdits);
        Assert.Equal(new[] { "a b c" }, st.Cells[0]);
    }

    [Fact]
    public void Tsv_RoundTripsThroughTablePaste()
    {
        var grid = new[]
        {
            new[] { "Roll", "Red", "Blue" },
            new[] { "1", "10", "" },
            new[] { "2", "11\t2", "22\nx" },
        };
        var tsv = OcrResultModel.ToTsv(grid);
        Assert.Equal("Roll\tRed\tBlue\n1\t10\t\n2\t11 2\t22 x", tsv);
        var p = TablePaste.Parse(tsv, true, true);
        Assert.Equal(new[] { "Red", "Blue" }, p.ColumnKeys);
        Assert.Equal(new[] { "1", "2" }, p.RowKeys);
        Assert.Equal(new[] { "10", "" }, p.Cells[0]);
        Assert.Equal(new[] { "11 2", "22 x" }, p.Cells[1]);
    }

    [Fact]
    public void ToText_JoinsLinesAndWords()
    {
        var words = new List<OcrWord> { W("b", 40, 0), W("a", 0, 0), W("c", 0, 20) };
        Assert.Equal("a b\nc", OcrResultModel.ToText(words));
    }
}
