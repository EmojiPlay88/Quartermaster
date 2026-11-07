using System.Drawing;
using Quartermaster.Render.Ocr;
using Quartermaster.Render.Search;
using Quartermaster.Rules;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Render.Tests;

public class ScanSessionModelTests
{
    [Fact]
    public void ModuleImages_FiltersByExtension_SortedCaseInsensitive()
    {
        var list = ScanSessionModel.ModuleImages(new[]
        {
            "buildFile.xml", "images/b.PNG", "images/a.jpg", "docs/rules.pdf", "images/c.jpeg", "images/d.gif", "images/e.bmp",
            "images/f.tif", "images/", "images/a.jpg",
        });
        Assert.Equal(new[] { "images/a.jpg", "images/b.PNG", "images/c.jpeg", "images/d.gif", "images/e.bmp" }, list.Select(s => s.Key));
        Assert.All(list, s => Assert.Equal(ScanSourceKind.ModuleImage, s.Kind));
        Assert.All(list, s => Assert.False(s.IsPdf));
    }

    [Fact]
    public void ScanFiles_AcceptTiffAndPdf()
    {
        Assert.True(ScanSessionModel.IsScanFile(@"C:\x\scan.TIFF"));
        Assert.True(ScanSessionModel.IsScanFile("a.pdf"));
        Assert.False(ScanSessionModel.IsScanFile("a.txt"));
        Assert.True(ScanSessionModel.IsImageName("a.tif"));
        Assert.False(ScanSessionModel.IsImageName("a.pdf"));
        Assert.Contains("*.tiff", ScanSessionModel.OpenScanFilter);
    }

    [Fact]
    public void AddFile_DedupesByFullPath_RefEntryIsFileName()
    {
        var m = new ScanSessionModel();
        var a = m.AddFile(Path.Combine(Path.GetTempPath(), "Chart.png"));
        var b = m.AddFile(Path.Combine(Path.GetTempPath(), "chart.PNG"));
        var p = m.AddFile(Path.Combine(Path.GetTempPath(), "book.pdf"));
        Assert.Same(a, b);
        Assert.Equal(2, m.Opened.Count);
        Assert.Equal("Chart.png", a.RefEntry);
        Assert.Equal(ScanSourceKind.FileImage, a.Kind);
        Assert.Equal(ScanSourceKind.FilePdf, p.Kind);
        Assert.Equal(new[] { p }, m.OpenedPdfs);
    }

    [Fact]
    public void Regions_PerSourceAndPage_ClippedAndClearedOnSizeChange()
    {
        var m = new ScanSessionModel();
        var size = new Size(1000, 800);
        m.SetRegion("doc.pdf", 1, size, new Rectangle(900, 700, 300, 300));
        m.SetRegion("doc.pdf", 2, size, new Rectangle(10, 10, 50, 50));
        Assert.Equal(new Rectangle(900, 700, 100, 100), m.GetRegion("doc.pdf", 1, size));
        Assert.Equal(new Rectangle(10, 10, 50, 50), m.GetRegion("DOC.pdf", 2, size));
        Assert.Null(m.GetRegion("doc.pdf", 3, size));
        Assert.Null(m.GetRegion("doc.pdf", 1, new Size(1000, 801))); // image changed size: cleared
        Assert.Null(m.GetRegion("doc.pdf", 1, size));
        m.SetRegion("doc.pdf", 2, size, null);
        Assert.Null(m.GetRegion("doc.pdf", 2, size));
        m.SetRegion("doc.pdf", 2, size, new Rectangle(2000, 2000, 5, 5)); // outside: nothing stored
        Assert.Null(m.GetRegion("doc.pdf", 2, size));
    }

    [Fact]
    public void Reads_OneAtATime_StaleResultsDropped()
    {
        var m = new ScanSessionModel();
        Assert.False(m.IsReading);
        var first = m.BeginRead();
        Assert.True(m.IsReading && m.IsCurrent(first));
        var second = m.BeginRead();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(m.IsCurrent(first));
        Assert.True(m.IsCurrent(second));
        m.EndRead(first); // stale: no effect
        Assert.True(m.IsReading);
        m.EndRead(second);
        Assert.False(m.IsReading);
        Assert.False(m.IsCurrent(second));

        var third = m.BeginRead();
        m.CancelRead();
        Assert.True(third.Token.IsCancellationRequested);
        Assert.False(m.IsCurrent(third));
        Assert.False(m.IsReading);
    }

    [Fact]
    public void RuleRef_PdfHasPage_ImageHasNone()
    {
        Assert.Equal(("rules.pdf", 4), ScanSessionModel.RuleRefFor(ScanSource.ModulePdf("rules.pdf", "Rules"), 4));
        Assert.Equal(("images/crt.png", 0), ScanSessionModel.RuleRefFor(ScanSource.ModuleImage("images/crt.png"), 4));
        var file = ScanSource.File(Path.Combine(Path.GetTempPath(), "scan.jpg"));
        Assert.Equal(("scan.jpg", 0), ScanSessionModel.RuleRefFor(file, 1));
    }

    [Fact]
    public void ImageRefWithoutPage_IsALink()
    {
        var r = new RuleRef { Id = "r", Label = "CRT", DocumentEntry = "scan.png", Page = 0 };
        var shown = SearchPanelModel.DescribeRef(r, new[] { ("scan.png", "scan.png") }, null);
        Assert.True(shown.IsLink);
        Assert.Equal("CRT — scan.png", shown.Text);
        var pdf = SearchPanelModel.DescribeRef(r with { DocumentEntry = "a.pdf" }, new[] { ("a.pdf", "A") }, null);
        Assert.True(pdf.Missing);
    }

    [Fact]
    public void Import_TargetTextNameAndKind()
    {
        Assert.Equal("OK creates the new table \"T\".", ScanSessionModel.TargetDescription("T", newTable: true));
        Assert.Contains("replaces", ScanSessionModel.TargetDescription("T", newTable: false));
        Assert.Equal("Scanned table", ScanSessionModel.SuggestTableName(new[] { "CRT" }));
        Assert.Equal("Scanned table 3", ScanSessionModel.SuggestTableName(new[] { "scanned TABLE", "Scanned table 2" }));
        Assert.Equal(TableKind.TwoD, ScanSessionModel.KindFor(TablePaste.Parse("R\tA\tB\n1\t2\t3", true, true)));
        Assert.Equal(TableKind.OneD, ScanSessionModel.KindFor(TablePaste.Parse("R\tA\n1\t2", true, true)));
    }

    [Fact]
    public void Zoom_ClampedAndStepped()
    {
        Assert.Equal(0.25, ScanSessionModel.ClampZoom(0.01));
        Assert.Equal(4.0, ScanSessionModel.ClampZoom(9));
        Assert.Equal(1.0, ScanSessionModel.ClampZoom(double.NaN));
        Assert.Equal(0.5, ScanSessionModel.FitZoom(new Size(2000, 1000), new Size(1000, 900)));
        Assert.Equal(0.25, ScanSessionModel.FitZoom(new Size(20000, 20000), new Size(100, 100)));
        Assert.Equal(1.5, ScanSessionModel.StepZoom(1.0, +1));
        Assert.Equal(0.75, ScanSessionModel.StepZoom(1.0, -1));
        Assert.Equal(4.0, ScanSessionModel.StepZoom(4.0, +1));
        Assert.Equal(0.25, ScanSessionModel.StepZoom(0.25, -1));
        Assert.Equal(1.0, ScanSessionModel.StepZoom(0.8, +1));
    }

    [Fact]
    public void RegionEdit_DragHitTestAdjust()
    {
        var img = new Size(500, 400);
        Assert.Equal(Rectangle.FromLTRB(10, 20, 110, 220), RegionEdit.FromDrag(new Point(110, 220), new Point(10, 20), img));
        Assert.Null(RegionEdit.FromDrag(new Point(10, 10), new Point(12, 40), img)); // too thin: a click
        Assert.Equal(Rectangle.FromLTRB(450, 350, 500, 400), RegionEdit.FromDrag(new Point(450, 350), new Point(900, 900), img));

        var r = new Rectangle(100, 100, 200, 100);
        Assert.Equal(RegionHandle.NW, RegionEdit.HitTest(r, new PointF(101, 99), 4));
        Assert.Equal(RegionHandle.SE, RegionEdit.HitTest(r, new PointF(300, 200), 4));
        Assert.Equal(RegionHandle.E, RegionEdit.HitTest(r, new PointF(302, 150), 4));
        Assert.Equal(RegionHandle.Move, RegionEdit.HitTest(r, new PointF(200, 150), 4));
        Assert.Equal(RegionHandle.None, RegionEdit.HitTest(r, new PointF(50, 150), 4));

        Assert.Equal(Rectangle.FromLTRB(100, 100, 330, 200), RegionEdit.Adjust(r, RegionHandle.E, 30, 99, img));
        Assert.Equal(Rectangle.FromLTRB(300, 100, 350, 200), RegionEdit.Adjust(r, RegionHandle.W, 250, 0, img)); // crossed: normalised
        Assert.Equal(new Rectangle(300, 300, 200, 100), RegionEdit.Adjust(r, RegionHandle.Move, 900, 900, img)); // kept inside
        Assert.Equal(Rectangle.FromLTRB(0, 0, 300, 200), RegionEdit.Adjust(r, RegionHandle.NW, -500, -500, img));
    }
}
