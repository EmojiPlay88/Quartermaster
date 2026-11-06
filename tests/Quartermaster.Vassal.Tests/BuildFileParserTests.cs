using System.Text;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class BuildFileParserTests
{
    private static ModuleNode P(string xml) => BuildFileParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Fact]
    public void Descendants_FindsByShortName()
    {
        var root = P(FixtureModule.MinimalBuildFile());
        Assert.Equal("VASSAL.build.GameModule", root.ClassName);
        var boards = root.Descendants("Board").ToList();
        Assert.Single(boards);
        Assert.Equal("Main Board", boards[0].Attr("name"));
        Assert.Equal("missing", boards[0].Attr("zzz", "missing"));
        Assert.Same(root, boards[0].Parent!.Parent!.Parent);
        Assert.Single(root.Descendants("SquareGrid"));
    }

    [Fact]
    public void Text_ConcatenatesDirectTextIncludingCData()
    {
        var root = P("<a.B.R>x &amp; <![CDATA[<y>]]><a.B.C>inner</a.B.C> z</a.B.R>");
        Assert.Equal("x & <y> z", root.Text);
        Assert.Equal("inner", root.Children[0].Text);
    }

    [Fact]
    public void Parse_RejectsDtdWithoutResolvingIt()
    {
        var xml = "<!DOCTYPE r [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><r>&e;</r>";
        Assert.ThrowsAny<System.Xml.XmlException>(() => P(xml));
    }

    [Fact]
    public void Parse_IgnoresComments()
    {
        var root = P("<r><!-- c --><a.X/></r>");
        Assert.Single(root.Children);
    }
}
