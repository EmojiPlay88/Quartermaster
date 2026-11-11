using System.Diagnostics;
using Quartermaster.Render;
using Quartermaster.Render.Search;
using Quartermaster.Rules;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Quartermaster.Render.Tests;

/// <summary>Rulebook index and search over invented PDFs generated at test time.</summary>
[Collection(AppDataRootEnv.Name)]
public class RulebookSearchTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-search-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);

    public RulebookSearchTests()
    {
        Directory.CreateDirectory(_tmp);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _tmp);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldRoot);
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    // ---- helpers ----

    internal static byte[] Pdf(params string[][] pages)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var pg = b.AddPage(PageSize.A4);
            double y = 800;
            foreach (var l in lines)
            {
                pg.AddText(l, 10, new PdfPoint(40, y), font);
                y -= 14;
            }
        }
        return b.Build();
    }

    private static (string, string, Func<Stream>) Doc(string entry, string title, byte[] bytes, Action? onOpen = null) =>
        (entry, title, () => { onOpen?.Invoke(); return new MemoryStream(bytes, writable: false); });

    private static Task<RulebookIndex> Build(string moduleSha, params (string, string, Func<Stream>)[] docs) =>
        RulebookIndex.BuildAsync(moduleSha, docs, null, CancellationToken.None);

    private static readonly byte[] Rules = Pdf(
        new[] { "Basic Rules", "Supply lines run from the depot to each unit." },
        new[] { "Movement", "A unit spends movement points to enter a hex.", "Desert hexes cost two points." },
        new[] { "Combat", "The attacker rolls one die on the combat table.", "Supply affects the die roll." });

    // ---- query parsing ----

    [Fact]
    public void Parse_WordsAndPhrases()
    {
        var (w, p) = SearchQuery.Parse("  supply \"combat   table\" Supply \"die\" \"open ended");
        Assert.Equal(new[] { "supply", "die" }, w);
        Assert.Equal(new[] { "combat table", "open ended" }, p);
        Assert.Empty(SearchQuery.Parse("   ").Words);
    }

    // ---- search ----

    [Fact]
    public async Task Search_Words_AllMustAppearOnPage()
    {
        var ix = await Build("mod1", Doc("rules.pdf", "Rules", Rules));
        Assert.True(ix.Documents[0].Searchable);
        Assert.Equal(3, ix.Documents[0].Pages.Count);
        var hits = ix.Search("supply die");
        Assert.Equal(new[] { 3 }, hits.Select(h => h.Page));
        Assert.Equal(new[] { 1, 3 }, ix.Search("supply").Select(h => h.Page));
        Assert.Empty(ix.Search("supply desert"));
        Assert.Empty(ix.Search(""));
    }

    [Fact]
    public async Task Search_Phrase_Exact()
    {
        var ix = await Build("mod1", Doc("rules.pdf", "Rules", Rules));
        Assert.Equal(new[] { 3 }, ix.Search("\"combat table\"").Select(h => h.Page));
        Assert.Empty(ix.Search("\"table combat\""));
        // Phrase across a line break still matches (line breaks count as spaces).
        Assert.Equal(new[] { 2 }, ix.Search("\"a hex. desert\"").Select(h => h.Page));
    }

    [Fact]
    public async Task Search_CaseAndDiacriticInsensitive()
    {
        // From a generated PDF (PdfPig's Standard-14 writer only round-trips ASCII): accented/upper-case query, plain text.
        var pdf = Pdf(new[] { "The Cafe Regime applies to ELITE units." }, new[] { "Plain cafe text." });
        var ix = await Build("mod1", Doc("d.pdf", "D", pdf));
        Assert.Equal(new[] { 1, 2 }, ix.Search("CAFE").Select(h => h.Page));
        Assert.Equal(new[] { 1, 2 }, ix.Search("cafÉ").Select(h => h.Page));
        Assert.Equal(new[] { 1 }, ix.Search("\"élite Units\"").Select(h => h.Page));

        // Accented page text (as a real rulebook would extract), plain query; the mark covers the original accented word.
        var mem = new RulebookIndex(new[]
        {
            new IndexedDocument("m.pdf", "M", "x", true, new[]
            {
                new PageText(1, "The Café régime applies\nto ÉLITE units, ﬁre too."),
                new PageText(2, "Nothing."),
            }),
        });
        Assert.Single(mem.Search("cafe"));
        Assert.Single(mem.Search("\"elite units\""));
        Assert.Single(mem.Search("fire"));
        var h1 = mem.Search("REGIME").Single();
        var (s, l) = h1.Marks.Single();
        Assert.Equal("régime", h1.Snippet.Substring(s, l));
        var h2 = mem.Search("\"applies to elite\"").Single();
        Assert.Equal("applies to ÉLITE", h2.Snippet.Substring(h2.Marks[0].Start, h2.Marks[0].Length));
    }

    [Fact]
    public async Task Search_ResultsGroupedOrdered_SnippetMarked()
    {
        var other = Pdf(new[] { "Nothing here." }, new[] { "Supply tables for the second book." });
        var longLine = string.Join(' ', Enumerable.Repeat("filler", 40)) + " supply marker " + string.Join(' ', Enumerable.Repeat("padding", 40));
        var third = Pdf(new[] { longLine });
        var ix = await Build("mod1", Doc("a.pdf", "Book A", Rules), Doc("b.pdf", "Book B", other), Doc("c.pdf", "Book C", third));
        var hits = ix.Search("supply");
        Assert.Equal(new[] { ("a.pdf", 1), ("a.pdf", 3), ("b.pdf", 2), ("c.pdf", 1) }, hits.Select(h => (h.Entry, h.Page)));
        Assert.Equal("Book B", hits[2].Title);
        foreach (var h in hits)
        {
            Assert.True(h.Snippet.Length <= 200, h.Snippet);
            Assert.NotEmpty(h.Marks);
            foreach (var (s, l) in h.Marks) Assert.Equal("supply", h.Snippet.Substring(s, l), ignoreCase: true);
            Assert.DoesNotContain('\n', h.Snippet);
        }
        var c = hits[3];
        Assert.StartsWith("…", c.Snippet);
        Assert.EndsWith("…", c.Snippet);
        // Two words both marked, in order, no overlaps.
        var both = ix.Search("die supply").Single();
        Assert.Equal(3, both.Page);
        Assert.True(both.Marks.Count >= 3);
        for (var i = 1; i < both.Marks.Count; i++) Assert.True(both.Marks[i].Start >= both.Marks[i - 1].Start + both.Marks[i - 1].Length);
        Assert.Single(ix.Search("supply", max: 1));
    }

    // ---- index / cache ----

    [Fact]
    public async Task Index_Cache_ReusedWhenShaSame_RebuiltWhenChanged()
    {
        var opens = 0;
        var progress = new List<double>();
        var ix1 = await RulebookIndex.BuildAsync("modC", new[] { Doc("r.pdf", "R", Rules, () => opens++) },
            new SyncProgress(progress.Add), CancellationToken.None);
        Assert.Equal(1, opens); // hashed and parsed from one open
        Assert.Equal(0, progress.First());
        Assert.Equal(1, progress.Last());
        var sha = ix1.Documents[0].Sha256;
        var folder = Path.Combine(_tmp, "search", "modC");
        var cacheFile = Path.Combine(folder, sha + ".json.gz");
        Assert.True(File.Exists(cacheFile));

        // Tamper the cache: the same SHA reuses it (no extraction), proving the cache is read.
        RulebookIndex.WriteCache(folder, sha, true, new[] { new PageText(1, "cachedmarker only") });
        opens = 0;
        var ix2 = await Build("modC", Doc("r.pdf", "R", Rules, () => opens++));
        Assert.Equal(1, opens);
        Assert.Single(ix2.Search("cachedmarker"));

        // A changed document gets a new SHA and a fresh extraction.
        var changed = Pdf(new[] { "Revised rules text." });
        opens = 0;
        var ix3 = await Build("modC", Doc("r.pdf", "R", changed, () => opens++));
        Assert.Equal(1, opens);
        Assert.NotEqual(sha, ix3.Documents[0].Sha256);
        Assert.Single(ix3.Search("revised"));
        Assert.Empty(ix3.Search("cachedmarker"));

        // A corrupt cache file is rebuilt silently.
        File.WriteAllBytes(cacheFile, new byte[] { 1, 2, 3, 4, 5 });
        var ix4 = await Build("modC", Doc("r.pdf", "R", Rules));
        Assert.Single(ix4.Search("depot"));
        Assert.NotNull(RulebookIndex.TryReadCache(folder, sha));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));

        // A cache from another extractor version is ignored and rebuilt.
        var oldJson = "{\"Format\":" + RulebookIndex.CacheFormat + ",\"Extractor\":\"pdfpig-0.0.1/clean-0\",\"Sha256\":\"" + sha +
                      "\",\"Searchable\":true,\"Pages\":[{\"Page\":1,\"Text\":\"oldextractormarker\"}]}";
        File.WriteAllBytes(cacheFile, Quartermaster.Net.Gzip.Compress(System.Text.Encoding.UTF8.GetBytes(oldJson)));
        var ix5 = await Build("modC", Doc("r.pdf", "R", Rules));
        Assert.Empty(ix5.Search("oldextractormarker"));
        Assert.Single(ix5.Search("depot"));
    }

    [Fact]
    public async Task Index_TransientFailures_NotCached_StaleTempsSwept()
    {
        var folder = Path.Combine(_tmp, "search", "modT");
        Directory.CreateDirectory(folder);
        var stale = Path.Combine(folder, "x.json.gz.old.tmp");
        var fresh = Path.Combine(folder, "y.json.gz.new.tmp");
        File.WriteAllText(stale, "");
        File.WriteAllText(fresh, "");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        var ix = await Build("modT",
            ("locked.pdf", "Locked", () => throw new IOException("in use")),
            ("broken.pdf", "Broken", () => new FailingStream(Rules, failAfter: 100)));
        Assert.All(ix.Documents, d => { Assert.False(d.Searchable); Assert.Equal("", d.Sha256); });
        Assert.Empty(Directory.GetFiles(folder, "*.json.gz"));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.EndsWith("(missing)", RuleRefLinks.Describe(new RuleRef { DocumentEntry = "locked.pdf", Page = 1 }, ix.Documents));

        // Once readable again, it indexes normally.
        var ok = await Build("modT", Doc("locked.pdf", "Locked", Rules));
        Assert.True(ok.Documents[0].Searchable);
    }

    [Fact]
    public async Task Index_NonSeekable_CopiedUpToCap()
    {
        var old = RulebookIndex.MaxCopyBytes;
        try
        {
            var ix = await Build("modN", ("s.pdf", "S", () => new NonSeekable(Rules)));
            Assert.True(ix.Documents[0].Searchable);
            Assert.Equal(ix.Documents[0].Sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Rules)));

            var other = Pdf(new[] { "Another invented book." });
            RulebookIndex.MaxCopyBytes = other.Length - 1;
            var big = await Build("modN", ("b.pdf", "B", () => new NonSeekable(other)));
            Assert.False(big.Documents[0].Searchable);
            Assert.Single(Directory.GetFiles(Path.Combine(_tmp, "search", "modN"), "*.json.gz")); // only the first
        }
        finally { RulebookIndex.MaxCopyBytes = old; }
    }

    [Fact]
    public async Task Search_WordStartOnly()
    {
        var pdf = Pdf(
            new[] { "The soldier will return to base." },
            new[] { "Units that die are removed; the dies are rolled each turn." },
            new[] { "Each player controls one army." },
            new[] { "The attacker rolls one die." });
        var ix = await Build("modW", Doc("w.pdf", "W", pdf));
        Assert.Equal(new[] { 2, 4 }, ix.Search("die").Select(h => h.Page));
        Assert.Equal(new[] { 2 }, ix.Search("turn").Select(h => h.Page));
        Assert.Equal(new[] { 4 }, ix.Search("\"rolls one\"").Select(h => h.Page));
        Assert.Equal(new[] { 2 }, ix.Search("dies").Select(h => h.Page));
        var p2 = ix.Search("die").First();
        Assert.Equal(2, p2.Marks.Count); // "die" and the start of "dies"
        Assert.Equal(2, ix.ProbablyDefinedOn("turn", "w.pdf"));
        Assert.Null(ix.ProbablyDefinedOn("ldier", "w.pdf"));
    }

    [Fact]
    public void Search_MarksKeepSurrogatePairsWhole_DocumentsReadOnly()
    {
        var ix = new RulebookIndex(new List<IndexedDocument>
        {
            new("m.pdf", "M", "x", true, new List<PageText> { new(1, "One \U0001D538lpha and alph\U0001D538 here.") }),
        });
        var h = ix.Search("alpha").Single();
        var marked = h.Marks.Select(m => h.Snippet.Substring(m.Start, m.Length)).ToArray();
        Assert.Equal(new[] { "\U0001D538lpha", "alph\U0001D538" }, marked);
        Assert.Throws<NotSupportedException>(() => ((IList<IndexedDocument>)ix.Documents).Add(ix.Documents[0]));
        Assert.Throws<NotSupportedException>(() => ((IList<PageText>)ix.Documents[0].Pages).Add(new PageText(2, "")));
    }

    [Fact]
    public async Task Index_CorruptAndImageOnlyPdf_NotSearchable_NoThrow()
    {
        var b = new PdfDocumentBuilder();
        b.AddPage(PageSize.A4).DrawRectangle(new PdfPoint(50, 50), 200, 100);
        b.AddPage(PageSize.A4).DrawRectangle(new PdfPoint(60, 60), 100, 100);
        var imageOnly = b.Build();
        var junk = new byte[4000];
        new Random(7).NextBytes(junk);
        var ix = await Build("modX",
            Doc("junk.pdf", "Junk", junk),
            Doc("scan.pdf", "Scan", imageOnly),
            ("gone.pdf", "Gone", () => throw new FileNotFoundException("gone")),
            Doc("rules.pdf", "Rules", Rules));
        Assert.Equal(4, ix.Documents.Count);
        Assert.False(ix.Documents[0].Searchable);
        Assert.False(ix.Documents[1].Searchable);
        Assert.Equal(2, ix.Documents[1].Pages.Count);
        Assert.False(ix.Documents[2].Searchable);
        Assert.True(ix.Documents[3].Searchable);
        Assert.Equal(new[] { "rules.pdf" }, ix.Search("supply").Select(h => h.Entry).Distinct());
        // Second build with the unsearchable results cached: still no throw, same answers.
        var again = await Build("modX", Doc("junk.pdf", "Junk", junk), Doc("scan.pdf", "Scan", imageOnly));
        Assert.All(again.Documents, d => Assert.False(d.Searchable));
        Assert.Empty(RulebookIndex.Empty.Search("x"));
    }

    [Fact]
    public async Task Index_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RulebookIndex.BuildAsync("modK", new[] { Doc("r.pdf", "R", Rules) }, null, cts.Token));
    }

    [Fact]
    public async Task Index_ExtractorHangs_TimesOut_NotSearchable_MarkerSkipsNextBuild()
    {
        using var release = new ManualResetEventSlim(false); // stands in for "forever"; released at the end so no thread leaks
        var calls = 0;
        RulebookIndex.TextExtractor hang = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            release.Wait();
            throw new InvalidOperationException("released");
        };
        try
        {
            var docs = new[] { Doc("hang.pdf", "Hang", Rules), };
            var sw = Stopwatch.StartNew();
            var ix = await RulebookIndex.BuildAsync("modH", docs, null, CancellationToken.None, null, null, hang, TimeSpan.FromMilliseconds(300));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
            Assert.Equal(1, calls);
            var d = Assert.Single(ix.Documents);
            Assert.False(d.Searchable);
            Assert.NotEqual("", d.Sha256);
            Assert.Contains(ix.Documents, x => x.Entry == "hang.pdf");
            Assert.Equal(SearchPanelModel.NotSearchableText, SearchPanelModel.Build(ix, "supply").Single(r => r.Kind == SearchRowKind.NotSearchable).Text.Split(" — ")[1]);
            var cached = RulebookIndex.TryReadCache(RulebookIndex.CacheFolder("modH"), d.Sha256);
            Assert.NotNull(cached);
            Assert.False(cached!.Value.Searchable);

            // The marker is used: a later build never calls the extractor for that document.
            var again = await RulebookIndex.BuildAsync("modH", docs, null, CancellationToken.None, null, null, hang, TimeSpan.FromMilliseconds(300));
            Assert.Equal(1, calls);
            Assert.False(again.Documents[0].Searchable);

            // The marker expires after 7 days (fake clock): the document is tried again.
            var stillFresh = DateTime.UtcNow + RulebookIndex.MarkerMaxAge - TimeSpan.FromHours(1);
            await RulebookIndex.BuildAsync("modH", docs, null, CancellationToken.None, null, null, hang, TimeSpan.FromMilliseconds(300), () => stillFresh);
            Assert.Equal(1, calls);
            var expired = DateTime.UtcNow + RulebookIndex.MarkerMaxAge + TimeSpan.FromHours(1);
            await RulebookIndex.BuildAsync("modH", docs, null, CancellationToken.None, null, null, hang, TimeSpan.FromMilliseconds(300), () => expired);
            Assert.Equal(2, calls);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Cancel_while_extractor_also_cancels_leaves_no_unobserved_exception()
    {
        // Closing during a build cancels the token the extraction also watches: whichever side notices first, the extraction's
        // own exception must be observed (it used to reach the finalizer when the extraction finished first; PR #2 CI).
        const string Tag = "rulebook-cancel-race-probe";
        var unobserved = 0;
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message == Tag)) Interlocked.Increment(ref unobserved);
        }
        RulebookIndex.TextExtractor waitsForCancel = (_, _, ct) =>
        {
            ct.WaitHandle.WaitOne();
            throw new OperationCanceledException(Tag, ct);
        };
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            for (var i = 0; i < 60; i++)
            {
                using var cts = new CancellationTokenSource();
                var build = RulebookIndex.BuildAsync("modC" + i, new[] { Doc("c.pdf", "C", Rules) }, null, cts.Token, null, new[] { "c.pdf" },
                    waitsForCancel, TimeSpan.FromSeconds(30));
                await Task.Delay(5);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
            }
            await Task.Delay(100);   // let abandoned extractions end and their continuations observe them
            for (var k = 0; k < 3; k++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
        Assert.Equal(0, unobserved);
    }

    [Fact]
    public async Task Index_SlowExtractor_PageEvery100ms_StallLimit800ms_Completes()
    {
        RulebookIndex.TextExtractor slow = (_, report, _) =>
        {
            var pages = new List<PageText>();
            // 1.6 s in all: twice the stall limit, but never 800 ms without a page (300 ms was too tight for a busy CI runner, PR #3)
            for (var p = 1; p <= 16; p++)
            {
                Thread.Sleep(100);
                pages.Add(new PageText(p, $"Slow page {p} about supply"));
                report(p / 16.0);
            }
            return (true, pages);
        };
        var ix = await RulebookIndex.BuildAsync("modS", new[] { Doc("slow.pdf", "Slow", Rules) }, null, CancellationToken.None, null, null,
            slow, TimeSpan.FromMilliseconds(800));
        Assert.True(ix.Documents[0].Searchable);
        Assert.Equal(16, ix.Search("supply").Count);
    }

    [Fact]
    public async Task Index_StallAfterSomePages_NotSearchable_NotCached()
    {
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        RulebookIndex.TextExtractor stallLater = (_, report, _) =>
        {
            Interlocked.Increment(ref calls);
            report(0.5); // one page finished, then stuck
            release.Wait();
            throw new InvalidOperationException("released");
        };
        try
        {
            var docs = new[] { Doc("half.pdf", "Half", Rules) };
            var ix = await RulebookIndex.BuildAsync("modP", docs, null, CancellationToken.None, null, null, stallLater, TimeSpan.FromMilliseconds(300));
            Assert.False(ix.Documents[0].Searchable);
            Assert.Null(RulebookIndex.TryReadCache(RulebookIndex.CacheFolder("modP"), ix.Documents[0].Sha256)); // treated as transient
            await RulebookIndex.BuildAsync("modP", docs, null, CancellationToken.None, null, null, stallLater, TimeSpan.FromMilliseconds(300));
            Assert.Equal(2, calls); // retried
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Index_MemoryOnlyEntries_NotCached_ReusedFromPreviousIndex()
    {
        var calls = 0;
        RulebookIndex.TextExtractor counting = (s, r, ct) =>
        {
            Interlocked.Increment(ref calls);
            return (true, new[] { new PageText(1, "Opened supply rules") });
        };
        var opened = Path.Combine(_tmp, "elsewhere", "opened.pdf");
        var docs = new[] { Doc(opened, "opened.pdf", Rules) };
        var ix = await RulebookIndex.BuildAsync("modM", docs, null, CancellationToken.None, null, new[] { opened }, counting, RulebookIndex.StallLimit);
        Assert.True(ix.Documents[0].Searchable);
        Assert.Equal(1, calls);
        var folder = RulebookIndex.CacheFolder("modM");
        Assert.True(!Directory.Exists(folder) || Directory.GetFiles(folder).Length == 0); // text kept in memory only
        // A rebuild (another file opened) reuses the text the last index read.
        var again = await RulebookIndex.BuildAsync("modM", docs, null, CancellationToken.None, ix, new[] { opened }, counting, RulebookIndex.StallLimit);
        Assert.Equal(1, calls);
        Assert.Single(again.Search("supply"));
        // Without a previous index it is read again (nothing on disk).
        await RulebookIndex.BuildAsync("modM", docs, null, CancellationToken.None, null, new[] { opened }, counting, RulebookIndex.StallLimit);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PruneCache_DeletesOtherModulesUnusedFor90Days_NeverCurrent()
    {
        await Build("modOld", Doc("r.pdf", "R", Rules));
        await Build("modNew", Doc("r.pdf", "R", Rules));
        await Build("modCur", Doc("r.pdf", "R", Rules));
        var root = Path.Combine(_tmp, "search");
        var old = DateTime.UtcNow.AddDays(-120);
        foreach (var name in new[] { "modOld", "modCur" })
        {
            var dir = Path.Combine(root, name);
            foreach (var f in Directory.GetFiles(dir)) File.SetLastWriteTimeUtc(f, old);
            Directory.SetLastWriteTimeUtc(dir, old);
        }
        Assert.Equal(1, RulebookIndex.PruneCache("modCur"));
        Assert.False(Directory.Exists(Path.Combine(root, "modOld")));
        Assert.True(Directory.Exists(Path.Combine(root, "modNew")));
        Assert.True(Directory.Exists(Path.Combine(root, "modCur"))); // the open module's cache is never pruned
        // Using a module's cache marks it fresh.
        await Build("modCur", Doc("r.pdf", "R", Rules));
        Assert.True(DateTime.UtcNow - Directory.GetLastWriteTimeUtc(Path.Combine(root, "modCur")) < TimeSpan.FromDays(1));
        Assert.Equal(0, RulebookIndex.PruneCache("other"));
    }

    [Fact]
    public async Task Index_500Pages_QueryUnder200ms()
    {
        var rnd = new Random(42);
        string[] vocab = { "unit", "hex", "supply", "attack", "defend", "river", "road", "turn", "phase", "point", "leader", "morale", "column", "table", "result", "retreat" };
        var pages = new string[500][];
        for (var p = 0; p < 500; p++)
        {
            pages[p] = Enumerable.Range(0, 30)
                .Select(_ => string.Join(' ', Enumerable.Range(0, 10).Select(_ => vocab[rnd.Next(vocab.Length)])))
                .ToArray();
        }
        pages[321] = pages[321].Append("the zebra crossing rule").ToArray();
        var ix = await Build("mod500", Doc("big.pdf", "Big", Pdf(pages)));
        Assert.Equal(500, ix.Documents[0].Pages.Count);
        Assert.Equal(new[] { 322 }, ix.Search("\"zebra crossing\" rule").Select(h => h.Page));

        var best = long.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            ix.Search("supply morale");
            ix.Search("\"retreat result\" leader");
            ix.Search("nonexistentword");
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        Assert.True(best < 200, $"best of 3: {best} ms");
    }

    [Fact]
    public async Task DefinedOn_HeadingLine()
    {
        var pdf = Pdf(
            new[] { "Units may not enter a zone of control without stopping first, says the long rule text here." },
            new[] { "Some other text on this page without the term." },
            new[] { "Zone of Control", "A zone of control is the six hexes around a unit." },
            new[] { "Glossary", "Overrun is when a stack keeps moving after combat in the movement phase: see below." });
        var ix = await Build("modD", Doc("r.pdf", "R", pdf));
        Assert.Equal(3, ix.ProbablyDefinedOn("zone of control", "r.pdf"));
        Assert.Equal(1, ix.ProbablyDefinedOn("stopping", "r.pdf")); // only in long lines: first occurrence
        Assert.Equal(4, ix.ProbablyDefinedOn("movement phase", "r.pdf")); // followed by ':'
        Assert.Null(ix.ProbablyDefinedOn("absent", "r.pdf"));
        Assert.Null(ix.ProbablyDefinedOn("zone", "other.pdf"));
    }

    // ---- rule ref links ----

    private static RulesPack Pack() => new()
    {
        RuleRefs =
        {
            new RuleRef { Id = "r1", Label = "Supply", DocumentEntry = "rules.pdf", Page = 3 },
            new RuleRef { Id = "r2", Label = "Combat", DocumentEntry = "rules.pdf", Page = 2 },
            new RuleRef { Id = "r3", Label = "Lost", DocumentEntry = "nowhere.pdf", Page = 1 },
        },
        Formulas = { new FormulaDef { Id = "f1", Name = "Supply check", RuleRefId = "r1" }, new FormulaDef { Id = "f2", Name = "Odds", RuleRefId = "r2" } },
        Tables = { new TableDef { Id = "t1", Name = "Supply table", RuleRefId = "r1" } },
        Phases = { new PhaseDef { Id = "p1", Name = "Supply phase", RuleRefId = "r1" }, new PhaseDef { Id = "p2", Name = "No ref" } },
    };

    [Fact]
    public void UsedOnPage_ListsFormulasTablesPhases()
    {
        var used = RuleRefLinks.UsedOnPage(Pack(), "rules.pdf", 3);
        Assert.Equal(new[] { ("Formula", "f1", "Supply check"), ("Table", "t1", "Supply table"), ("Phase", "p1", "Supply phase") }, used);
        Assert.Equal(new[] { ("Formula", "f2", "Odds") }, RuleRefLinks.UsedOnPage(Pack(), "rules.pdf", 2));
        Assert.Empty(RuleRefLinks.UsedOnPage(Pack(), "rules.pdf", 1));
        Assert.Empty(RuleRefLinks.UsedOnPage(Pack(), "other.pdf", 3));
    }

    [Fact]
    public async Task RuleRef_MissingDocOrPage_Describe()
    {
        var ix = await Build("modR", Doc("rules.pdf", "Basic Rules", Rules));
        var docs = ix.Documents;
        var ok = RuleRefLinks.Describe(new RuleRef { Label = "Supply", DocumentEntry = "rules.pdf", Page = 3 }, docs);
        Assert.Equal("Supply — Basic Rules, p. 3", ok);
        Assert.EndsWith("(missing)", RuleRefLinks.Describe(new RuleRef { Label = "Far", DocumentEntry = "rules.pdf", Page = 9 }, docs));
        Assert.EndsWith("(missing)", RuleRefLinks.Describe(new RuleRef { Label = "Zero", DocumentEntry = "rules.pdf", Page = 0 }, docs));
        var lost = RuleRefLinks.Describe(new RuleRef { Label = "Lost", DocumentEntry = "nowhere.pdf", Page = 1 }, docs);
        Assert.Equal("Lost — nowhere.pdf, p. 1 (missing)", lost);
        Assert.Equal("Basic Rules, p. 1", RuleRefLinks.Describe(new RuleRef { DocumentEntry = "rules.pdf", Page = 1 }, docs));
    }

    /// <summary>A seekable stream whose reads fail with an IOException once <c>failAfter</c> bytes have been read.</summary>
    private sealed class FailingStream(byte[] data, int failAfter) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= failAfter) throw new IOException("read failed");
            return base.Read(buffer, offset, Math.Min(count, failAfter - (int)Position));
        }

        public override int Read(Span<byte> buffer) => throw new IOException("read failed");
    }

    /// <summary>A forward-only stream (CanSeek false).</summary>
    private sealed class NonSeekable(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) { lock (this) report(value); }
    }
}
