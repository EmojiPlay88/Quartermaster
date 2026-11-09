using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Quartermaster.Desktop.Documents;
using Quartermaster.Desktop.Input;
using Quartermaster.Documents;
using Quartermaster.Render;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quartermaster.Desktop.Tests;

/// <summary>Stage 3b task 8: the viewer's toolbar mode (document list, images, tool items, Used on this page), headless.</summary>
[Collection("MacFlag")]
public sealed class DocumentViewerToolbarTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-docbar-" + Guid.NewGuid().ToString("N"));
    readonly List<Window> _windows = new();
    readonly List<DocumentViewer> _viewers = new();
    readonly List<ModuleSession> _sessions = new();
    readonly List<FixtureModule> _fixtures = new();

    public DocumentViewerToolbarTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        foreach (var v in _viewers) v.Dispose();
        foreach (var s in _sessions) s.Dispose();
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static byte[] Pdf(string text)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        b.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(40, 700), font);
        return b.Build();
    }

    static string Help(string title, string file) =>
        $"<VASSAL.build.module.documentation.HelpFile title=\"{title}\" fileName=\"{file}\" fileType=\"archive\"/>";

    ModuleSession Session(bool withDocs)
    {
        var xml = FixtureModule.MinimalBuildFile();
        if (withDocs)
            xml = xml.Replace("</VASSAL.build.GameModule>",
                "<VASSAL.build.module.Documentation>" + Help("Rules", "rules.pdf") + Help("Notes", "notes.txt") + Help("Appendix", "app.pdf") +
                "</VASSAL.build.module.Documentation></VASSAL.build.GameModule>");
        var fx = new FixtureModule().WithBuildFile(xml);
        if (withDocs)
            fx.AddFile("rules.pdf", Pdf("Invented rules")).AddFile("notes.txt", Encoding.UTF8.GetBytes("notes")).AddFile("app.pdf", Pdf("Invented appendix"));
        _fixtures.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "cache"), null);
        _sessions.Add(s);
        return s;
    }

    (DocumentViewer V, ModuleSession S) Host(bool toolbar = true, bool withDocs = true)
    {
        var s = Session(withDocs);
        var v = new DocumentViewer(s, toolbar, new FakeDialogs());
        _viewers.Add(v);
        var w = new Window { Width = 800, Height = 600, Content = v };
        _windows.Add(w);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        return (v, s);
    }

    static DocumentDef Doc(ModuleSession s, string title) => s.Model.Documents.Single(d => d.Title == title);

    static void Select(DocumentViewer v, int index)
    {
        v.DocumentList!.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Toolbar_lists_only_pdf_documents()
    {
        var (v, s) = Host();
        Assert.Equal(new[] { "Rules", "Appendix" }, v.PdfDocuments.Select(d => d.Title).ToArray());
        Assert.Equal(new object?[] { "Rules", "Appendix" }, v.DocumentList!.Items.ToArray());
        Assert.Equal(180, v.DocumentList.Width);
        Assert.Same(v.ToolRow, v.DocumentList.Parent);
        Assert.True(v.ToolRow!.Children.IndexOf(v.DocumentList) < v.ToolRow.Children.IndexOf(v.NavigationBar));
        Assert.Equal(-1, v.DocumentList.SelectedIndex);
        Assert.Contains(s.Model.Documents, d => d.Kind != DocumentKind.Pdf);
    }

    [AvaloniaFact]
    public void Choosing_document_opens_it()
    {
        var (v, s) = Host();
        var opened = 0;
        v.DocumentOpened += () => opened++;
        Select(v, 1);
        Assert.Same(Doc(s, "Appendix"), v.Document);
        Assert.Equal(1, opened);
        Assert.Equal(1, v.DocumentList!.SelectedIndex);
        v.Open(Doc(s, "Rules"));
        Assert.Equal(0, v.DocumentList.SelectedIndex);
    }

    [AvaloniaFact]
    public void Empty_module_text()
    {
        var (v, _) = Host(withDocs: false);
        Assert.Empty(v.PdfDocuments);
        Assert.Equal(new[] { "This module has no PDF documents." }, v.BodyTexts);
    }

    [AvaloniaFact]
    public void Choose_document_text()
    {
        var (v, _) = Host();
        Assert.Equal(new[] { "Choose a document above." }, v.BodyTexts);
    }

    [AvaloniaFact]
    public void Images_after_separator_raise_image_chosen_and_snap_back()
    {
        var (v, s) = Host();
        v.SetImageDocuments(new[] { "Map scan", "Counter sheet" });
        Assert.Equal("──── Images ────", DocumentViewer.ImagesSeparator);
        Assert.Equal(new object?[] { "Rules", "Appendix", DocumentViewer.ImagesSeparator, "Map scan", "Counter sheet" }, v.DocumentList!.Items.ToArray());
        v.Open(Doc(s, "Appendix"));
        var chosen = new List<int>();
        v.ImageDocumentChosen += chosen.Add;
        Select(v, 4);
        Assert.Equal(new[] { 1 }, chosen);
        Assert.Equal(1, v.DocumentList.SelectedIndex);
        Assert.Same(Doc(s, "Appendix"), v.Document);
        v.SetImageDocuments(Array.Empty<string>());
        Assert.Equal(2, v.DocumentList.Items.Count);
        Assert.Equal(1, v.DocumentList.SelectedIndex);
    }

    [AvaloniaFact]
    public void Separator_cannot_be_chosen()
    {
        var (v, s) = Host();
        v.SetImageDocuments(new[] { "Map scan" });
        v.Open(Doc(s, "Rules"));
        var chosen = new List<int>();
        v.ImageDocumentChosen += chosen.Add;
        Select(v, 2);
        Assert.Empty(chosen);
        Assert.Equal(0, v.DocumentList!.SelectedIndex);
        Assert.Same(Doc(s, "Rules"), v.Document);
    }

    [AvaloniaFact]
    public void Add_tool_item_appends()
    {
        var (v, _) = Host();
        var b = new Button { Content = "Scan" };
        v.AddToolItem(b);
        Assert.Same(b, v.ToolRow!.Children[^1]);
        Assert.True(v.ToolRow.Children.IndexOf(b) > v.ToolRow.Children.IndexOf(v.NavigationBar));
    }

    [AvaloniaFact]
    public void Used_on_page_hidden_until_enabled()
    {
        var (v, _) = Host();
        Assert.False(v.UsedPanel!.IsVisible);
        Assert.Equal(50, v.UsedPanel.Height);
        Assert.Equal("Used on this page", v.UsedTitle!.Text);
        Assert.Equal(FontWeight.Bold, v.UsedTitle.FontWeight);
        v.EnableUsedOnPage();
        Assert.True(v.UsedPanel.IsVisible);
    }

    [AvaloniaFact]
    public void Used_on_page_empty_texts_grey()
    {
        var (v, s) = Host();
        v.EnableUsedOnPage();
        v.SetUsedOnPage(Array.Empty<(string, string, string)>());
        Assert.Equal(new object?[] { "No document open." }, v.UsedList!.Items.ToArray());
        Assert.Empty(v.UsedOnPageLines);
        var grey = ((SolidColorBrush)v.UsedList.Foreground!).Color;
        Assert.Equal(Color.FromRgb(0x6D, 0x6D, 0x6D), grey);
        v.Open(Doc(s, "Rules"));
        v.SetUsedOnPage(Array.Empty<(string, string, string)>());
        Assert.Equal(new object?[] { "Nothing in the rules pack points here." }, v.UsedList.Items.ToArray());
        v.SetUsedOnPage(new[] { ("Formula", "f1", "Combat odds") });
        Assert.Equal(new[] { "Combat odds" }, v.UsedOnPageLines.ToArray());
        Assert.NotEqual(grey, (v.UsedList.Foreground as SolidColorBrush)?.Color);
    }

    [AvaloniaFact]
    public void Used_item_double_click_and_enter_activate()
    {
        var (v, _) = Host();
        v.EnableUsedOnPage();
        v.SetUsedOnPage(new[] { ("Formula", "f1", "Combat odds"), ("Table", "t7", "Terrain") });
        var got = new List<(string, string)>();
        v.UsedItemActivated += (k, i) => got.Add((k, i));
        v.UsedList!.SelectedIndex = 1;
        v.UsedList.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, new PointerEventArgs(InputElement.PointerReleasedEvent, v.UsedList,
            new Pointer(0, PointerType.Mouse, true), v.UsedList, default, 0, new PointerPointProperties(), KeyModifiers.None)));
        Assert.Equal(new[] { ("Table", "t7") }, got.ToArray());
        v.UsedList.SelectedIndex = 0;
        v.UsedList.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(("Formula", "f1"), got[^1]);
        Assert.Equal(2, got.Count);
        // the empty-note line is not activatable
        v.SetUsedOnPage(Array.Empty<(string, string, string)>());
        v.UsedList.SelectedIndex = 0;
        v.UsedList.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(2, got.Count);
    }

    [AvaloniaFact]
    public void Toolbar_false_has_no_document_list()
    {
        var (v, _) = Host(toolbar: false);
        Assert.Null(v.DocumentList);
        Assert.Null(v.UsedPanel);
        Assert.Empty(v.PdfDocuments);
        Assert.Empty(v.UsedOnPageLines);
        v.AddToolItem(new Button());
        v.SetImageDocuments(new[] { "x" });
        v.EnableUsedOnPage();
        v.SetUsedOnPage(new[] { ("Formula", "f", "t") });
        Assert.Empty(v.BodyTexts);
    }

    static IBrush? RowBrush(DocumentViewer v, string text)
    {
        Dispatcher.UIThread.RunJobs();
        var tb = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v.UsedList!).OfType<TextBlock>().Single(t => t.Text == text);
        return tb.Foreground;
    }

    [AvaloniaFact]
    public void Used_rows_render_grey_only_for_notes()
    {
        var (v, s) = Host();
        v.EnableUsedOnPage();
        v.Open(Doc(s, "Rules"));
        v.SetUsedOnPage(Array.Empty<(string, string, string)>());
        var grey = Color.FromRgb(0x6D, 0x6D, 0x6D);
        Assert.Equal(grey, ((SolidColorBrush)RowBrush(v, "Nothing in the rules pack points here.")!).Color);
        v.SetUsedOnPage(new[] { ("Formula", "f1", "Combat odds") });
        var normal = RowBrush(v, "Combat odds");
        Assert.NotNull(normal);
        Assert.NotEqual(grey, (normal as SolidColorBrush)?.Color);
        v.SetUsedOnPage(Array.Empty<(string, string, string)>());
        Assert.Equal(grey, ((SolidColorBrush)RowBrush(v, "Nothing in the rules pack points here.")!).Color);
    }
}
