using Quartermaster.Render;
using Quartermaster.Render.Search;
using Quartermaster.Rules;

namespace Quartermaster.Render.Tests;

/// <summary>Results panel rows, snippet runs and rule-ref display over invented PDFs generated at test time.</summary>
[Collection(AppDataRootEnv.Name)]
public class SearchPanelModelTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-panel-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);

    public SearchPanelModelTests()
    {
        Directory.CreateDirectory(_tmp);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _tmp);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldRoot);
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly byte[] Book = RulebookSearchTests.Pdf(
        new[] { "Contents", "The rules about supply are explained in a later chapter of this book." },
        new[] { "Supply", "A unit traces supply to a depot." },
        new[] { "Combat", "Units out of supply halve their strength." });

    private static Task<RulebookIndex> Index(params (string Entry, string Title, byte[] Bytes)[] docs) =>
        RulebookIndex.BuildAsync("panel-mod", docs.Select(d => (d.Entry, d.Title, (Func<Stream>)(() => new MemoryStream(d.Bytes, false)))).ToList(),
            null, CancellationToken.None);

    [Fact]
    public async Task Build_GroupsPerDocument_WithHint_AndListsUnsearchable()
    {
        var ix = await Index(("a.pdf", "Book A", Book), ("bad.pdf", "Broken", new byte[] { 1, 2, 3 }), ("b.pdf", "Book B", Book));
        var rows = SearchPanelModel.Build(ix, "supply");
        Assert.Equal(new[]
        {
            SearchRowKind.Document, SearchRowKind.Hit, SearchRowKind.Hit, SearchRowKind.Hit,
            SearchRowKind.NotSearchable,
            SearchRowKind.Document, SearchRowKind.Hit, SearchRowKind.Hit, SearchRowKind.Hit,
        }, rows.Select(r => r.Kind));
        Assert.Equal("Book A — 3 pages · Probably defined on page 2", rows[0].Text);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Skip(1).Take(3).Select(r => r.Page));
        Assert.Equal("Broken — not searchable", rows[4].Text);
        Assert.Equal("b.pdf", rows[6].Entry);
        Assert.Contains(rows[2].Runs, r => r.Bold && r.Text == "Supply");
    }

    [Fact]
    public async Task Build_NoHits_SaysSo_EmptyQuery_NoRows()
    {
        var ix = await Index(("a.pdf", "Book A", Book));
        var rows = SearchPanelModel.Build(ix, "zeppelin");
        Assert.Single(rows);
        Assert.Equal(SearchRowKind.Message, rows[0].Kind);
        Assert.Empty(SearchPanelModel.Build(ix, "   "));
        // Several words: no glossary hint.
        Assert.Equal("Book A — 1 page", SearchPanelModel.Build(ix, "supply halve")[0].Text);
    }

    [Fact]
    public void Runs_MarksBecomeBold_ClampedAndMerged()
    {
        var runs = SearchPanelModel.Runs("one two three", new[] { (4, 3), (6, 3), (50, 4), (-2, 1) });
        Assert.Equal(new[] { new TextRun("one ", false), new TextRun("two t", true), new TextRun("hree", false) }, runs);
        Assert.Equal(new[] { new TextRun("plain", false) }, SearchPanelModel.Runs("plain", Array.Empty<(int, int)>()));
        Assert.Equal(new[] { new TextRun("", false) }, SearchPanelModel.Runs("", Array.Empty<(int, int)>()));
    }

    [Fact]
    public void Runs_LongLead_IsCutAtASpace()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 30)) + " target end";
        var at = text.IndexOf("target", StringComparison.Ordinal);
        var runs = SearchPanelModel.Runs(text, new[] { (at, 6) }, maxLead: 20);
        Assert.StartsWith("…word", runs[0].Text);
        Assert.True(runs[0].Text.Length <= 22);
        Assert.Equal(new TextRun("target", true), runs[1]);
        Assert.Equal(" end", runs[2].Text);
    }

    [Fact]
    public void Terms_ForGlossaryViewerAndFind()
    {
        Assert.Equal("supply", SearchPanelModel.GlossaryTerm(" supply "));
        Assert.Equal("zone of control", SearchPanelModel.GlossaryTerm("\"zone of control\""));
        Assert.Null(SearchPanelModel.GlossaryTerm("supply depot"));
        Assert.Equal("zone of control", SearchPanelModel.ViewerSearchTerm("hex \"zone of control\""));
        Assert.Equal("hex", SearchPanelModel.ViewerSearchTerm("hex depot"));
        Assert.Null(SearchPanelModel.ViewerSearchTerm(""));

        Assert.Equal("#page=3", SearchPanelModel.ViewerFragment(3, "  "));
        Assert.Equal("#page=2&search=zone%20of%20control", SearchPanelModel.ViewerFragment(2, " zone of control "));

        Assert.Equal("depot", SearchPanelModel.FindText("  depot\r\n", "Fuel for movement"));
        Assert.Equal("\"supply depot\"", SearchPanelModel.FindText("supply\n  \"depot\"", "x"));
        Assert.Equal("Fuel for movement", SearchPanelModel.FindText("", "  Fuel  for movement "));
        Assert.Equal("Fuel", SearchPanelModel.FindText(new string('a', 500), "Fuel"));
        Assert.Equal("", SearchPanelModel.FindText(null, null));
    }

    [Fact]
    public async Task DescribeRef_LinksAndMissing()
    {
        var ix = await Index(("a.pdf", "Book A", Book), ("bad.pdf", "Broken", new byte[] { 1 }));
        var module = new[] { ("a.pdf", "Book A"), ("bad.pdf", "Broken"), ("notes.txt", "Notes") };
        RuleRef R(string entry, int page, string label = "Supply") => new() { Id = "r", Label = label, DocumentEntry = entry, Page = page };

        Assert.Equal(new RefDisplay("(none)", false, false), SearchPanelModel.DescribeRef(null, module, ix.Documents));
        Assert.Equal(new RefDisplay("Supply — Book A, p. 2", true, false), SearchPanelModel.DescribeRef(R("a.pdf", 2), module, ix.Documents));
        Assert.Equal(new RefDisplay("Supply — Book A, p. 9 (missing)", false, true), SearchPanelModel.DescribeRef(R("a.pdf", 9), module, ix.Documents));
        Assert.True(SearchPanelModel.DescribeRef(R("gone.pdf", 1), module, ix.Documents).Missing);
        // Not indexed: a text document, or the index not built yet.
        Assert.Equal(new RefDisplay("Notes, p. 1", true, false), SearchPanelModel.DescribeRef(R("notes.txt", 1, ""), module, ix.Documents));
        Assert.True(SearchPanelModel.DescribeRef(R("a.pdf", 9), module, null).IsLink);
        Assert.True(SearchPanelModel.DescribeRef(R("gone.pdf", 1), module, null).Missing);
    }

    [Fact]
    public void UsedOnPageRows_KindAndName()
    {
        var pack = new RulesPack
        {
            RuleRefs = new() { new RuleRef { Id = "r1", DocumentEntry = "a.pdf", Page = 2 } },
            Formulas = new() { new FormulaDef { Id = "f1", Name = "Supply roll", RuleRefId = "r1" } },
            Phases = new() { new PhaseDef { Id = "p1", Name = "Supply", RuleRefId = "r1" } },
        };
        Assert.Equal(new[] { ("Formula", "f1", "Formula: Supply roll"), ("Phase", "p1", "Phase: Supply") }, SearchPanelModel.UsedOnPageRows(pack, "a.pdf", 2));
        Assert.Empty(SearchPanelModel.UsedOnPageRows(pack, "a.pdf", 3));
        Assert.Empty(SearchPanelModel.UsedOnPageRows(null, "a.pdf", 2));
    }

    [Fact]
    public void Opens_HitsAndNotSearchableRows_NotHeadingsOrMessages()
    {
        TextRun[] runs = { new("x", false) };
        Assert.True(SearchPanelModel.Opens(new SearchRow(SearchRowKind.Hit, "a.pdf", "A", 2, "p. 2", runs)));
        Assert.True(SearchPanelModel.Opens(new SearchRow(SearchRowKind.NotSearchable, "scan.pdf", "Scan", 0, "Scan — not searchable", runs)));
        Assert.False(SearchPanelModel.Opens(new SearchRow(SearchRowKind.Document, "a.pdf", "A", 0, "A — 1 page", runs)));
        Assert.False(SearchPanelModel.Opens(new SearchRow(SearchRowKind.Message, "", "", 0, "No pages match x.", runs)));
        Assert.False(SearchPanelModel.Opens(null));
        Assert.Equal("This PDF has no text; use Scan this page to read a page with OCR.", SearchPanelModel.NoTextStatus);
    }
}
