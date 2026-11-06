using System.Text;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class ModuleModelTests
{
    private const string V = "VASSAL.build.";
    private const string BP = "VASSAL.build.module.map.boardPicker.";

    private static string Fixture() => $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="no"?>
        <{{V}}GameModule name="Fix" version="2.1" VassalVersion="3.7.1">
          <{{V}}module.GlobalOptions/>
          <{{V}}module.PrototypesContainer>
            <{{V}}module.PrototypeDefinition name="Proto">+/null/mark;Side/null</{{V}}module.PrototypeDefinition>
          </{{V}}module.PrototypesContainer>
          <{{V}}module.Documentation>
            <{{V}}module.documentation.BrowserPDFFile title="Rules" pdfFile="rules.pdf"/>
          </{{V}}module.Documentation>
          <{{V}}module.PlayerRoster><entry>Red</entry><entry>Blue</entry></{{V}}module.PlayerRoster>
          <{{V}}module.PieceWindow text="Units">
            <{{V}}widget.ListWidget entryName="Units">
              <{{V}}widget.PieceSlot entryName="Inf" gpid="7" height="30" width="40">+/null/piece;;;a.png;Inf/null;0;0;1</{{V}}widget.PieceSlot>
              <{{V}}widget.PieceSlot entryName="Tank" gpid="8" height="30" width="40">+/null/piece;;;b.png;Tank/null;0;0;2</{{V}}widget.PieceSlot>
            </{{V}}widget.ListWidget>
          </{{V}}module.PieceWindow>
          <{{V}}module.ChartWindow name="Charts Win">
            <{{V}}widget.TabWidget entryName="Tab 1">
              <{{V}}widget.WidgetMap mapName="Org A" backgroundcolor="10,20,30"/>
              <{{V}}widget.Chart chartName="C1" fileName="c1.png"/>
            </{{V}}widget.TabWidget>
          </{{V}}module.ChartWindow>
          <{{V}}module.PredefinedSetup name="Scenario 1" useFile="true" file="s1.vsav"/>
          <{{V}}module.PredefinedSetup name="Empty" useFile="false"/>
          <{{V}}module.SpecialDiceButton name="Roll"><{{V}}module.SpecialDie name="d6"><{{V}}module.SpecialDieFace text="one" value="1"/><{{V}}module.SpecialDieFace text="two" value="2"/></{{V}}module.SpecialDie></{{V}}module.SpecialDiceButton>
          <{{V}}module.Map mapName="Main Map" markMoved="Never" backgroundcolor="255,0,0">
            <{{V}}module.map.BoardPicker>
              <{{BP}}Board name="Left" image="left.png"/>
              <{{BP}}Board name="Right" image="right.png"/>
            </{{V}}module.map.BoardPicker>
            <{{V}}module.map.StackMetrics exSepX="9" exSepY="19" unexSepX="1" unexSepY="3" disabled="true"/>
            <{{V}}module.map.SetupStack name="Start" owningBoard="Left" x="5" y="6" location="" useGridLocation="false">
              <{{V}}widget.PieceSlot entryName="Inf" gpid="9" height="30" width="40">+/null/piece;;;a.png;Inf/null;0;0;1</{{V}}widget.PieceSlot>
            </{{V}}module.map.SetupStack>
            <{{V}}module.map.Zoomer/>
          </{{V}}module.Map>
          <com.example.Foo/>
          <com.example.Foo a="1"/>
        </{{V}}GameModule>
        """;

    private static (ModuleModel m, DiagnosticLog log, FixtureModule fx, VmodArchive a) Build(string? xml = null)
    {
        var fx = new FixtureModule().WithBuildFile(xml ?? Fixture()).AddImage("left.png", 200, 100).AddImage("right.png", 150, 80);
        fx.AddFile("readme.txt", Encoding.UTF8.GetBytes("hi")).AddFile("sub/inner.txt", [1]);
        var a = VmodArchive.Open(fx.Build());
        var log = new DiagnosticLog();
        return (ModuleModelBuilder.Build(a, log), log, fx, a);
    }

    [Fact]
    public void Build_FindsMapsAndOrgDisplays()
    {
        var (m, _, fx, a) = Build();
        using var _a = a; using var _f = fx;
        Assert.Equal(2, m.Maps.Count);
        var main = m.Maps.Single(x => !x.IsOrgDisplay);
        Assert.Equal("Main Map", main.Id);
        Assert.Equal("Never", main.MarkMoved);
        Assert.Equal(new RgbColor(255, 0, 0), main.Background);
        Assert.Equal(new StackMetricsDef(9, 19, 1, 3, true), main.Stacking);
        var org = m.Maps.Single(x => x.IsOrgDisplay);
        Assert.Equal("Org A", org.Name);
        Assert.Equal(new[] { "Charts Win", "Tab 1" }, org.NavPath);
        Assert.Equal(new RgbColor(10, 20, 30), org.Background);
        Assert.Equal("Fix", m.Name); Assert.Equal("2.1", m.Version); Assert.Equal("3.7.1", m.VassalVersion);
    }

    [Fact]
    public void Build_LaysOutBoardsSideBySide()
    {
        var (m, _, fx, a) = Build();
        using var _a = a; using var _f = fx;
        var b = m.Maps.Single(x => !x.IsOrgDisplay).Boards;
        Assert.Equal(2, b.Count);
        Assert.Equal(200, b[0].Width); Assert.Equal(100, b[0].Height);
        Assert.Equal(150, b[1].Width);
        Assert.Equal(new PointD(0, 0), b[0].Offset);
        Assert.Equal(200, b[1].Offset.X);
        Assert.Equal(0, b[1].Offset.Y);
    }

    [Fact]
    public void Build_BoardPickerSetup_PositionsRowsAndColumnsWithEdge()
    {
        var xml = Fixture().Replace("<VASSAL.build.module.map.BoardPicker>",
            "<VASSAL.build.module.map.BoardPicker><setup>Main MapBoardPicker\tLeft\t0\t0\tRight\t0\t1\tLeft\t1\t0</setup>")
            .Replace("markMoved=\"Never\"", "markMoved=\"Never\" edgeWidth=\"10\" edgeHeight=\"4\"");
        var (m, _, fx, a) = Build(xml);
        using var _a = a; using var _f = fx;
        var map = m.Maps.Single(x => !x.IsOrgDisplay);
        Assert.Equal((10, 4), (map.EdgeWidth, map.EdgeHeight));
        Assert.Equal(3, map.Boards.Count);
        Assert.Equal(new PointD(10, 4), map.Boards[0].Offset);
        Assert.Equal(new PointD(10, 104), map.Boards[1].Offset);   // row 1 under Left (height 100)
        Assert.Equal(new PointD(210, 4), map.Boards[2].Offset);    // col 1 right of Left (width 200)
    }

    [Fact]
    public void Build_GridGetsRealBoardSize()
    {
        const string Num = "VASSAL.build.module.map.boardPicker.board.mapgrid.SquareGridNumbering";
        var xml = Fixture().Replace("<VASSAL.build.module.map.boardPicker.Board name=\"Left\" image=\"left.png\"/>",
            "<VASSAL.build.module.map.boardPicker.Board name=\"Left\" image=\"left.png\"><VASSAL.build.module.map.boardPicker.board.SquareGrid dx=\"50\" dy=\"50\" x0=\"25\" y0=\"25\" visible=\"true\">" +
            $"<{Num} first=\"H\" hType=\"N\" hLeading=\"0\" hOff=\"0\" hDescend=\"true\" vType=\"N\" vLeading=\"0\" vOff=\"0\" vDescend=\"false\" sep=\"\" visible=\"true\"/>" +
            "</VASSAL.build.module.map.boardPicker.board.SquareGrid></VASSAL.build.module.map.boardPicker.Board>");
        var (m, _, fx, a) = Build(xml);
        using var _a = a; using var _f = fx;
        var g = m.Maps.Single(x => !x.IsOrgDisplay).Boards[0].Grid!;
        Assert.NotNull(g);
        // 200px board / 50px cells = 4 columns: descending numbering counts down from the right edge.
        Assert.Equal("40", g.LocationName(new PointD(25, 25)));
        Assert.Equal("10", g.LocationName(new PointD(175, 25)));
    }

    [Fact]
    public void Build_SlotSplitsTypeAndState()
    {
        var (m, _, fx, a) = Build();
        using var _a = a; using var _f = fx;
        Assert.Equal(2, m.Slots.Count);
        var s = m.SlotByGpid("7")!;
        Assert.Equal("Inf", s.EntryName);
        Assert.Equal("piece;;;a.png;Inf", s.TypeString);
        Assert.Equal("null;0;0;1", s.StateString);
        Assert.Equal((40, 30), (s.Width, s.Height));
        Assert.Equal(new[] { "Units", "Units" }, s.NavPath);
        Assert.Same(m.Slots[1], m.SlotByPath("Units/Units/Tank"));
        Assert.Null(m.SlotByGpid("nope"));
        Assert.Equal("Proto", m.Prototypes.Keys.Single());
        Assert.NotNull(m.Expander);
    }

    [Fact]
    public void Build_SetupStacksModeled()
    {
        var (m, _, fx, a) = Build();
        using var _a = a; using var _f = fx;
        var st = m.Maps.Single(x => !x.IsOrgDisplay).SetupStacks.Single();
        Assert.Equal("Start", st.Name); Assert.Equal("Left", st.OwningBoard);
        Assert.Equal((5, 6), (st.X, st.Y)); Assert.False(st.UseGridLocation);
        Assert.Equal("9", st.Pieces.Single().Gpid);
    }

    [Fact]
    public void Build_ChartsDocsSetupsDiceSides()
    {
        var (m, _, fx, a) = Build();
        using var _a = a; using var _f = fx;
        var c = Assert.Single(m.Charts);
        Assert.Equal(("C1", "c1.png"), (c.Name, c.FileName));
        Assert.Equal(new[] { "Charts Win", "Tab 1" }, c.NavPath);
        Assert.Contains(m.Documents, d => d.Title == "Rules" && d.EntryName == "rules.pdf" && d.Kind == DocumentKind.Pdf);
        Assert.Contains(m.Documents, d => d.EntryName == "readme.txt" && d.Kind == DocumentKind.Text);
        Assert.DoesNotContain(m.Documents, d => d.EntryName.Contains("inner"));
        Assert.Equal("s1.vsav", m.Setups.Single(s => s.Name == "Scenario 1").FileEntry);
        Assert.Null(m.Setups.Single(s => s.Name == "Empty").FileEntry);
        Assert.Equal("d6", m.Dice.Single().Name);
        Assert.Equal(new[] { "one", "two" }, m.Dice.Single().Faces);
    }

    [Fact]
    public void Build_TreeHasFiveRootFolders()
    {
        var (m, _, fx, a) = Build();
        using var _a = a; using var _f = fx;
        Assert.Equal(new[] { "Maps", "Organisation Displays", "Charts", "Rulebooks & Documents", "Setups" },
            m.Tree.Children.Select(c => c.Title).ToArray());
        Assert.All(m.Tree.Children, c => Assert.Equal(WindowNodeKind.Folder, c.Kind));
        Assert.Equal(WindowNodeKind.Map, m.Tree.Children[0].Children.Single().Kind);
        var org = m.Tree.Children[1].Children.Single();
        Assert.Equal("Charts Win", org.Title);
        Assert.Equal(WindowNodeKind.OrgDisplay, org.Children.Single().Children.Single().Kind);
    }

    [Fact]
    public void Build_UnknownElement_LogsOnceWithCount()
    {
        var (_, log, fx, a) = Build();
        using var _a = a; using var _f = fx;
        var d = Assert.Single(log.Items, x => x.Category == "component" && x.Context == "com.example.Foo");
        Assert.Equal(DiagnosticSeverity.Info, d.Severity);
        Assert.Contains("2", d.Message);
        Assert.DoesNotContain(log.Items, x => x.Category == "component" && x.Message.Contains("Zoomer"));
        Assert.DoesNotContain(log.Items, x => x.Category == "component" && x.Message.Contains("GlobalOptions"));
        Assert.DoesNotContain(log.Items, x => x.Category == "component" && x.Message.Contains("SpecialDiceButton"));
    }

    [Fact]
    public void Build_SidesFromRoster()
    {
        var (m, _, fx, a) = Build();
        using var _a = a; using var _f = fx;
        Assert.Equal(new[] { "Red", "Blue" }, m.Sides);
    }

    [Fact]
    public void Build_MalformedSlot_SkippedWithDiagnostic()
    {
        var xml = Fixture().Replace("+/null/piece;;;b.png;Tank/null;0;0;2", "garbage");
        var (m, log, fx, a) = Build(xml);
        using var _a = a; using var _f = fx;
        Assert.Single(m.Slots);
        Assert.Contains(log.Items, d => d.Category == "component" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Build_MissingImage_UsesAttributesOrZeroWithDiagnostic()
    {
        var xml = Fixture().Replace("image=\"right.png\"", "image=\"gone.png\" width=\"33\" height=\"44\"")
                           .Replace("image=\"left.png\"", "image=\"gone2.png\"");
        var (m, log, fx, a) = Build(xml);
        using var _a = a; using var _f = fx;
        var b = m.Maps.Single(x => !x.IsOrgDisplay).Boards;
        Assert.Equal((0, 0), (b[0].Width, b[0].Height));
        Assert.Equal((33, 44), (b[1].Width, b[1].Height));
        Assert.Contains(log.Items, d => d.Category == "board");
    }

    [Fact]
    public void ImageSize_ReadsPngJpegGifSvg()
    {
        Assert.Equal(new SizeD(7, 9), ImageSize.Read("a.png", Png(7, 9)));
        Assert.Equal(new SizeD(5, 6), ImageSize.Read("a.gif", new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 5, 0, 6, 0 }));
        var jpg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC2, 0, 11, 8, 0, 20, 0, 30, 3, 0, 0, 0 };
        Assert.Equal(new SizeD(30, 20), ImageSize.Read("a.jpg", jpg));
        Assert.Equal(new SizeD(120, 60), ImageSize.Read("a.svg", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='120px' height='60'/>")));
        Assert.Equal(new SizeD(40, 25), ImageSize.Read("a.svg", Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 25'/>")));
        Assert.Null(ImageSize.Read("a.png", new byte[] { 1, 2, 3 }));
    }

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52 }.CopyTo(b, 0);
        b[19] = (byte)w; b[23] = (byte)h;
        return b;
    }
}
