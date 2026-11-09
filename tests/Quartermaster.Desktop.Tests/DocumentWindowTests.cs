using System.Drawing;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Quartermaster.Desktop.Documents;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3b task 7: document windows opened from the module tree and by rule references. Headless, invented PDFs.</summary>
[Collection("MacFlag")]
public sealed class DocumentWindowTests : IDisposable
{
    readonly bool _wasMac = ShortcutGestures.IsMac;
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-docwin-" + Guid.NewGuid().ToString("N"));
    readonly List<MainWindow> _windows = new();
    readonly List<FixtureModule> _fixtures = new();
    readonly FakeDialogs _dialogs = new();

    public DocumentWindowTests() { ShortcutGestures.IsMac = false; Directory.CreateDirectory(_tmp); }

    public void Dispose()
    {
        foreach (var w in _windows) w.ForceClose();
        foreach (var f in _fixtures) f.Dispose();
        ShortcutGestures.IsMac = _wasMac;
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string LayoutRoot => Path.Combine(_tmp, "layout");

    static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>An invented 4-page PDF.</summary>
    internal static byte[] Pdf(string name)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 1; i <= 4; i++)
        {
            var pg = b.AddPage(PageSize.A5);
            pg.AddText($"{name} page {i}", 12, new PdfPoint(30, 300), font);
        }
        return b.Build();
    }

    static string Help(string title, string file) =>
        $"<VASSAL.build.module.documentation.HelpFile title=\"{title}\" fileName=\"{file}\" fileType=\"archive\"/>";

    /// <summary>The module of this test: two PDF documents.</summary>
    internal static FixtureModule Module() =>
        new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile().Replace("</VASSAL.build.GameModule>",
            "<VASSAL.build.module.Documentation>" + Help("Rules", "rules.pdf") + Help("Annex", "annex.pdf") +
            "</VASSAL.build.module.Documentation></VASSAL.build.GameModule>"))
            .AddFile("rules.pdf", Pdf("Rules")).AddFile("annex.pdf", Pdf("Annex"));

    MainWindow Game()
    {
        var fx = Module();
        _fixtures.Add(fx);
        var w = new MainWindow(_dialogs, Path.Combine(_tmp, "cache"), null, LayoutRoot) { Width = 1024, Height = 768 };
        w.ScreensProvider = () => new[] { new ScreenInfo(new Rectangle(0, 0, 1920, 1080), 96) };
        _windows.Add(w);
        w.Show();
        Assert.True(w.OpenModule(fx.Build()));
        w.StartSetup(null);
        Pump();
        return w;
    }

    static WindowTreeNode Node(DocumentDef d) => new(d.Title, WindowNodeKind.Document, d, Array.Empty<WindowTreeNode>());

    static DocumentWindow Open(MainWindow w, int i = 0)
    {
        var d = w.Session!.Model.Documents[i];
        w.ModuleTree.Activate(Node(d));
        Pump();
        return w.DocumentWindows[d.EntryName];
    }

    [AvaloniaFact]
    public void Tree_document_node_opens_one_window_per_entry()
    {
        var w = Game();
        Assert.Equal(2, w.Session!.Model.Documents.Count);
        var a = Open(w, 0);
        var b = Open(w, 1);
        Assert.NotSame(a, b);
        Assert.Same(a, Open(w, 0));
        Assert.Equal(2, w.DocumentWindows.Count);
        Assert.Equal("Rules", a.Title);
        Assert.Equal("Annex", b.Title);
        Assert.Same(w, a.Owner);
        Assert.True(a.IsVisible);
        Assert.False(a.Viewer.Toolbar);
        Assert.Equal(1, a.Viewer.Page);
        a.Close();
        Pump();
        Assert.False(w.DocumentWindows.ContainsKey(a.Document.EntryName));
        Assert.NotSame(a, Open(w, 0));   // closed: a new one opens
    }

    [AvaloniaFact]
    public void Open_at_page_reuses_window_and_moves_page()
    {
        var w = Game();
        var d = w.Session!.Model.Documents[0];
        w.OpenDocumentAtPage(d.EntryName, 3);
        Pump();
        var a = w.DocumentWindows[d.EntryName];
        Assert.Equal(3, a.Viewer.Page);
        w.OpenDocumentAtPage(d.EntryName, 2);
        Pump();
        Assert.Same(a, w.DocumentWindows[d.EntryName]);
        Assert.Single(w.DocumentWindows);
        Assert.Equal(2, a.Viewer.Page);
        a.GoToPage(4);
        Assert.Equal(4, a.Viewer.Page);
        w.OpenDocument(d);   // no page: the window stays, still open
        Assert.Same(a, w.DocumentWindows[d.EntryName]);
    }

    [AvaloniaFact]
    public void Missing_entry_shows_rule_reference_error()
    {
        var w = Game();
        w.OpenDocumentAtPage("nope.pdf", 2);
        Pump();
        Assert.Empty(w.DocumentWindows);
        var warning = Assert.Single(_dialogs.Warnings);
        Assert.Equal("The rule reference points to 'nope.pdf', which is not in this module.", warning.Message);
    }

    [AvaloniaFact]
    public void Document_windows_close_with_session()
    {
        var w = Game();
        var a = Open(w, 0);
        var b = Open(w, 1);
        var other = new FixtureModule().WithBuildFile(FixtureModule.MinimalBuildFile());
        _fixtures.Add(other);
        Assert.True(w.OpenModule(other.Build()));   // replaces the session
        Pump();
        Assert.Empty(w.DocumentWindows);
        Assert.False(a.IsVisible);
        Assert.False(b.IsVisible);
        Assert.True(a.Viewer.Released);
    }

    [AvaloniaFact]
    public void Bounds_remembered_under_doc_key()
    {
        var w = Game();
        var d = w.Session!.Model.Documents[0];
        var sha = w.Session.Archive.Sha256;
        var a = Open(w);
        a.Position = new Avalonia.PixelPoint(210, 130);
        a.Width = 500;
        a.Height = 350;
        a.Close();
        Pump();
        var saved = new WindowLayoutStore(LayoutRoot).LoadModule(sha);
        Assert.True(saved.TryGetValue("doc:" + d.EntryName, out var sw));
        Assert.Equal(210, sw.Bounds.X);
        Assert.Equal(130, sw.Bounds.Y);
        Assert.Equal(500, sw.Bounds.Width);
        Assert.Equal(350, sw.Bounds.Height);
        Assert.False(saved.ContainsKey("doc:annex.pdf"));

        var again = Open(w);
        Assert.Equal(500, again.Width);
        Assert.Equal(350, again.Height);
        Assert.Equal(new Avalonia.PixelPoint(210, 130), again.Position);
    }
}
