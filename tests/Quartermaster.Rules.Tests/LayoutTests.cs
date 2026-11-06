using Quartermaster.Rules.Expr;

namespace Quartermaster.Rules.Tests;

public class LayoutTests
{
    private static RulesPack Pack() => new()
    {
        Properties = new() { new PropertyDef { Id = "p1", Name = "MP", Scope = PropertyScope.Pieces } },
        Holders = new() { new HolderDef { Id = "h1", Name = "Depot" } },
        Tables = new()
        {
            new TableDef { Id = "t1", Name = "Rates", Kind = TableKind.OneD, RowKeys = new() { "1", "2" }, ColumnKeys = new() { "Value" },
                Cells = new() { new() { "1" }, new() { "2" } } },
            new TableDef { Id = "t2", Name = "Grid", Kind = TableKind.TwoD, RowKeys = new() { "1" }, ColumnKeys = new() { "A" },
                Cells = new() { new() { "1" } } },
        },
    };

    private static FormulaDef F(string text, params VariableBinding[] b) =>
        new() { Id = "f1", Name = "F", Text = text, Bindings = b.ToList() };

    private static VariableBinding Bind(string v, BindingKind k) => new() { Variable = v, Kind = k };

    [Fact]
    public void Layout_BoxesInSourceOrder_WithOperatorsAsText()
    {
        var f = F("MP * Rate", Bind("MP", BindingKind.Unit) with { Property = "p1" }, Bind("Rate", BindingKind.Input));
        var m = FormulaLayout.Build(f, Pack());
        Assert.Equal(3, m.Main.Count);
        Assert.Equal("MP", Assert.IsType<BoxItem>(m.Main[0]).Variable);
        Assert.Equal(" × ", Assert.IsType<TextItem>(m.Main[1]).Text);
        Assert.Equal("Rate", Assert.IsType<BoxItem>(m.Main[2]).Variable);
        Assert.Empty(m.SubRows);
    }

    [Fact]
    public void Layout_RepeatedVariable_MirrorAfterFirst()
    {
        var m = FormulaLayout.Build(F("(A + A) / 2", Bind("A", BindingKind.Input)), Pack());
        Assert.Equal(new TextItem("("), m.Main[0]);
        Assert.IsType<BoxItem>(m.Main[1]);
        Assert.Equal(new TextItem(" + "), m.Main[2]);
        Assert.Equal(new MirrorItem("A"), m.Main[3]);
        Assert.Equal(new TextItem(") ÷ 2"), m.Main[4]);
    }

    [Fact]
    public void Layout_InputIsTyped_OthersAuto()
    {
        var f = F("A + B + C + D + E + G",
            Bind("A", BindingKind.Input), Bind("B", BindingKind.Roll), Bind("C", BindingKind.Location),
            Bind("D", BindingKind.Global), Bind("E", BindingKind.Unit), Bind("G", BindingKind.Holder));
        var boxes = FormulaLayout.Build(f, Pack()).Main.OfType<BoxItem>().ToList();
        Assert.False(boxes[0].Auto);
        Assert.All(boxes.Skip(1), b => Assert.True(b.Auto));
    }

    [Fact]
    public void Layout_TableBindingWithVariableRow_HasSubRow()
    {
        var f = F("MP * Rate",
            Bind("MP", BindingKind.Input),
            Bind("Rate", BindingKind.Table) with { TableId = "t1", RowExpr = "MP" });
        var m = FormulaLayout.Build(f, Pack());
        var sub = Assert.Single(m.SubRows);
        Assert.Equal("Rate = Rates[…]", sub.Label);
        Assert.Equal(new MirrorItem("MP"), Assert.Single(sub.Items));
    }

    [Fact]
    public void Layout_TableBinding_TwoD_And_UnparsableExpr()
    {
        var f = F("Rate", Bind("Rate", BindingKind.Table) with { TableId = "t2", RowExpr = "X", ColExpr = "Y" });
        var sub = Assert.Single(FormulaLayout.Build(f, Pack()).SubRows);
        Assert.Equal(3, sub.Items.Count);
        Assert.Equal(new TextItem(", "), sub.Items[1]);
        var bad = F("Rate", Bind("Rate", BindingKind.Table) with { TableId = "t1", RowExpr = "1 +" });
        var s2 = Assert.Single(FormulaLayout.Build(bad, Pack()).SubRows);
        Assert.Equal(new TextItem("1 +"), Assert.Single(s2.Items));
    }

    [Fact]
    public void Layout_InlineLookup_SubRow()
    {
        var f = F("1 + lookup(Rates, N)", Bind("N", BindingKind.Input));
        var m = FormulaLayout.Build(f, Pack());
        Assert.Equal(new TextItem("1 + Rates[…]"), Assert.Single(m.Main));
        var sub = Assert.Single(m.SubRows);
        Assert.Equal("Rates[…]", sub.Label);
        Assert.Equal("N", Assert.IsType<BoxItem>(Assert.Single(sub.Items)).Variable);
    }

    [Fact]
    public void Layout_InlineIf_SubRow()
    {
        var f = F("if(A > 1, B, 0)", Bind("A", BindingKind.Input), Bind("B", BindingKind.Input));
        var m = FormulaLayout.Build(f, Pack());
        Assert.Equal(new TextItem("if(…)"), Assert.Single(m.Main));
        var sub = Assert.Single(m.SubRows);
        Assert.Equal("if", sub.Label);
        Assert.Equal(new TextItem(" > 1, "), sub.Items[1]);
        Assert.Equal(new TextItem(", 0"), sub.Items[3]);
    }

    [Fact]
    public void Layout_CaptionDefaults_And_Override()
    {
        var f = F("A + B + C + D + E + G + H + I",
            Bind("A", BindingKind.Unit) with { Property = "p1" },
            Bind("B", BindingKind.Global) with { Property = "p1" },
            Bind("C", BindingKind.Holder) with { Property = "p1", HolderId = "h1" },
            Bind("D", BindingKind.Table) with { TableId = "t1" },
            Bind("E", BindingKind.Location),
            Bind("G", BindingKind.Input) with { Min = 1, Max = 6 },
            Bind("H", BindingKind.Roll) with { DiceCount = 2, DiceSides = 6 },
            Bind("I", BindingKind.Input) with { Caption = "Custom" });
        var c = FormulaLayout.Build(f, Pack()).Main.OfType<BoxItem>().Select(b => b.Caption).ToList();
        Assert.Equal(new[] { "MP (unit)", "MP (global)", "MP (Depot)", "Rates", "location", "G 1–6", "2d6", "Custom" }, c);
    }

    [Fact]
    public void Layout_RenamedProperty_CaptionShowsNewName()
    {
        var pack = Pack();
        pack = pack with { Properties = new() { pack.Properties[0] with { Name = "Movement" } } };
        var f = F("A", Bind("A", BindingKind.Unit) with { Property = "p1" });
        Assert.Equal("Movement (unit)", FormulaLayout.Build(f, pack).Main.OfType<BoxItem>().Single().Caption);
    }

    [Fact]
    public void Layout_Unparsable_SingleTextItem()
    {
        var m = FormulaLayout.Build(F("1 +"), Pack());
        Assert.Equal(new TextItem("1 +"), Assert.Single(m.Main));
    }
}
