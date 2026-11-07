using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Quartermaster.Net;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Quartermaster.Render.Search;

/// <summary>One page's extracted text (1-based page number; lines separated by '\n', spaces collapsed).</summary>
public sealed record PageText(int Page, string Text);

/// <summary>
/// A rulebook PDF in the index. <see cref="Searchable"/> is false when the file could not be read or has no text on any page
/// (a scan); <see cref="Pages"/> then lists the pages with empty text when the page count is known, else nothing.
/// </summary>
public sealed record IndexedDocument(string Entry, string Title, string Sha256, bool Searchable, IReadOnlyList<PageText> Pages);

/// <summary>A page that matches a query: <see cref="Marks"/> are the hit ranges inside <see cref="Snippet"/>.</summary>
public sealed record SearchHit(string Entry, string Title, int Page, string Snippet, IReadOnlyList<(int Start, int Length)> Marks);

/// <summary>
/// Page-by-page text of a module's rulebook PDFs (PdfPig), cached per document under <c>AppDataRoot/search/{moduleSha}/</c> as
/// <c>{documentSha}.json.gz</c> and rebuilt when a document's SHA-256 changes. Searches run in memory on a normalised copy of
/// every page (lower-case, accents stripped). Nothing here throws for a bad PDF or a bad cache file.
/// </summary>
public sealed class RulebookIndex
{
    internal const int CacheFormat = 1;
    /// <summary>Changes when the extractor changes (PdfPig version or our clean-up), so old cached text is rebuilt.</summary>
    internal static readonly string ExtractorVersion = "pdfpig-" + typeof(PdfDocument).Assembly.GetName().Version + "/clean-1";
    internal const int MaxCacheBytes = 64 * 1024 * 1024;
    /// <summary>Largest non-seekable PDF copied into memory; bigger ones are listed as not searchable (not cached).</summary>
    internal static long MaxCopyBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan StaleTmpAge = TimeSpan.FromMinutes(10);
    internal const int SnippetMax = 200;
    private const int SnippetLead = 60;

    public static RulebookIndex Empty { get; } = new(Array.Empty<IndexedDocument>());

    public IReadOnlyList<IndexedDocument> Documents { get; }

    private readonly NormPage[][] _norm;

    private sealed record NormPage(int Page, string Original, string Norm, int[] Map);

    internal RulebookIndex(IReadOnlyList<IndexedDocument> docs)
    {
        Documents = Array.AsReadOnly(docs.Select(d => d with { Pages = Array.AsReadOnly(d.Pages.ToArray()) }).ToArray());
        docs = Documents;
        _norm = docs.Select(d => d.Searchable
            ? d.Pages.Select(p =>
            {
                var (text, map) = TextNormaliser.NormaliseWithMap(p.Text, collapseSpaces: true);
                return new NormPage(p.Page, p.Text, text, map);
            }).ToArray()
            : Array.Empty<NormPage>()).ToArray();
    }

    /// <summary>
    /// Indexes the PDFs off the caller's thread, reusing cached text for documents whose SHA-256 is unchanged.
    /// Progress goes 0..1; cancellation throws <see cref="OperationCanceledException"/> and leaves the cache consistent.
    /// </summary>
    public static Task<RulebookIndex> BuildAsync(string moduleSha, IReadOnlyList<(string Entry, string Title, Func<Stream> Open)> pdfs,
        IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() => Build(moduleSha, pdfs, progress, ct), ct);

    private static RulebookIndex Build(string moduleSha, IReadOnlyList<(string Entry, string Title, Func<Stream> Open)> pdfs,
        IProgress<double>? progress, CancellationToken ct)
    {
        progress?.Report(0);
        var folder = CacheFolder(moduleSha);
        SweepStaleTemps(folder);
        var docs = new List<IndexedDocument>(pdfs.Count);
        for (var i = 0; i < pdfs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (entry, title, open) = pdfs[i];
            var lo = (double)i / pdfs.Count;
            var span = 1.0 / pdfs.Count;
            docs.Add(IndexOne(folder, entry, title, open, f => progress?.Report(lo + span * f), ct));
            progress?.Report((double)(i + 1) / pdfs.Count);
        }
        progress?.Report(1);
        return new RulebookIndex(docs);
    }

    /// <summary>Failures that may go away next time (file locked, disk, memory): never cached.</summary>
    private static bool IsTransient(Exception e) => e is IOException or UnauthorizedAccessException or OutOfMemoryException;

    private static IndexedDocument Unreadable(string entry, string title) => new(entry, title, "", false, Array.Empty<PageText>());

    /// <summary>
    /// Opens the PDF once: a seekable stream is hashed then rewound and parsed; a non-seekable one is copied (≤ <see cref="MaxCopyBytes"/>)
    /// while hashing, and the copy is parsed, so the SHA is always of the bytes parsed. Opening/reading failures are not cached;
    /// deterministic outcomes (PdfPig cannot parse it, or no page has text) are.
    /// </summary>
    private static IndexedDocument IndexOne(string folder, string entry, string title, Func<Stream> open, Action<double> report, CancellationToken ct)
    {
        Stream? raw = null;
        MemoryStream? copy = null;
        try
        {
            string sha;
            Stream parse;
            try
            {
                raw = open();
                if (raw.CanSeek)
                {
                    raw.Position = 0;
                    sha = Hash(raw, null, long.MaxValue, ct)!;
                    raw.Position = 0;
                    parse = raw;
                }
                else
                {
                    copy = new MemoryStream();
                    var h = Hash(raw, copy, MaxCopyBytes, ct);
                    if (h == null) return Unreadable(entry, title); // too big to copy
                    sha = h;
                    copy.Position = 0;
                    parse = copy;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Unreadable(entry, title);
            }

            var cached = TryReadCache(folder, sha);
            if (cached != null) return new IndexedDocument(entry, title, sha, cached.Value.Searchable, cached.Value.Pages);

            bool searchable;
            IReadOnlyList<PageText> pages;
            try
            {
                (searchable, pages) = Extract(parse, report, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (IsTransient(e)) { return Unreadable(entry, title); }
            catch (Exception)
            {
                (searchable, pages) = (false, Array.Empty<PageText>()); // PdfPig cannot read it (corrupt, encrypted, …)
            }
            WriteCache(folder, sha, searchable, pages);
            return new IndexedDocument(entry, title, sha, searchable, pages);
        }
        finally
        {
            copy?.Dispose();
            try { raw?.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>SHA-256 (lower hex) of the rest of the stream, copying it to <paramref name="copy"/> if given; null past <paramref name="max"/> bytes.</summary>
    private static string? Hash(Stream s, Stream? copy, long max, CancellationToken ct)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long total = 0;
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += n;
            if (total > max) return null;
            h.AppendData(buf, 0, n);
            copy?.Write(buf, 0, n);
        }
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    private static (bool Searchable, IReadOnlyList<PageText> Pages) Extract(Stream stream, Action<double> report, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(stream);
        var count = pdf.NumberOfPages;
        var pages = new List<PageText>(count);
        var any = false;
        for (var n = 1; n <= count; n++)
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try
            {
                var page = pdf.GetPage(n);
                string rawText;
                try { rawText = ContentOrderTextExtractor.GetText(page, false); }
                catch (Exception e) when (e is not OperationCanceledException && !IsTransient(e)) { rawText = page.Text; }
                text = CleanPageText(rawText);
            }
            catch (Exception e) when (e is not OperationCanceledException && !IsTransient(e)) { text = ""; }
            if (text.Length > 0) any = true;
            pages.Add(new PageText(n, text));
            report((double)n / count);
        }
        return (any, pages.AsReadOnly());
    }

    /// <summary>Deletes cache temp files left by a crashed write (older than a few minutes, so a concurrent write is not hit).</summary>
    private static void SweepStaleTemps(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder, "*.tmp"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > StaleTmpAge) File.Delete(f);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    private static readonly Regex Spaces = new(@"[^\S\n]+", RegexOptions.Compiled);

    /// <summary>Line breaks kept; other whitespace runs become one space; lines trimmed; blank lines dropped.</summary>
    internal static string CleanPageText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\0', ' ').Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var l in lines)
        {
            var t = Spaces.Replace(l, " ").Trim();
            if (t.Length > 0) kept.Add(t);
        }
        return string.Join('\n', kept);
    }

    // ---- cache ----

    private sealed class CacheDto
    {
        public int Format { get; set; }
        public string Extractor { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public bool Searchable { get; set; }
        public List<CachePage> Pages { get; set; } = new();
    }

    private sealed class CachePage
    {
        public int Page { get; set; }
        public string Text { get; set; } = "";
    }

    private static readonly Regex SafeName = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

    /// <summary><c>AppDataRoot/search/{moduleSha}</c>; a module SHA that is not a plain file name is hashed first.</summary>
    internal static string CacheFolder(string moduleSha)
    {
        var name = SafeName.IsMatch(moduleSha ?? "") ? moduleSha!
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(moduleSha ?? "")));
        return Path.Combine(AppDataRoot.Sub("search"), name);
    }

    internal static string CachePath(string folder, string docSha) => Path.Combine(folder, docSha + ".json.gz");

    internal static (bool Searchable, IReadOnlyList<PageText> Pages)? TryReadCache(string folder, string docSha)
    {
        try
        {
            var path = CachePath(folder, docSha);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxCacheBytes) return null;
            var json = Gzip.DecompressBounded(File.ReadAllBytes(path), MaxCacheBytes);
            var dto = JsonSerializer.Deserialize<CacheDto>(json);
            if (dto == null || dto.Format != CacheFormat || dto.Extractor != ExtractorVersion || dto.Sha256 != docSha || dto.Pages == null) return null;
            var pages = dto.Pages.Select(p => new PageText(p.Page, p.Text ?? "")).ToList();
            for (var i = 0; i < pages.Count; i++) if (pages[i].Page != i + 1) return null;
            return (dto.Searchable, pages.AsReadOnly());
        }
        catch (Exception) { return null; }
    }

    internal static void WriteCache(string folder, string docSha, bool searchable, IReadOnlyList<PageText> pages)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(folder);
            var dto = new CacheDto
            {
                Format = CacheFormat, Extractor = ExtractorVersion, Sha256 = docSha, Searchable = searchable,
                Pages = pages.Select(p => new CachePage { Page = p.Page, Text = p.Text }).ToList(),
            };
            var bytes = Gzip.Compress(JsonSerializer.SerializeToUtf8Bytes(dto));
            var path = CachePath(folder, docSha);
            tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            tmp = null;
        }
        catch (Exception) { /* cache is an optimisation; the index still works */ }
        finally
        {
            if (tmp != null) try { File.Delete(tmp); } catch (Exception) { }
        }
    }

    // ---- search ----

    /// <summary>
    /// Pages containing every word and phrase of the query, each at the start of a word ("die" matches "dies", not "soldier"),
    /// grouped by document (index order) and ordered by page.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, int max = 200)
    {
        var hits = new List<SearchHit>();
        if (max <= 0) return hits;
        var terms = SearchQuery.NormalisedTerms(query);
        if (terms.Count == 0) return hits;
        for (var d = 0; d < Documents.Count; d++)
        {
            var doc = Documents[d];
            foreach (var p in _norm[d])
            {
                var first = int.MaxValue;
                var all = true;
                foreach (var t in terms)
                {
                    var at = TextNormaliser.IndexOfWordStart(p.Norm, t, 0);
                    if (at < 0) { all = false; break; }
                    if (at < first) first = at;
                }
                if (!all) continue;
                var (snippet, marks) = MakeSnippet(p, terms, first);
                hits.Add(new SearchHit(doc.Entry, doc.Title, p.Page, snippet, marks));
                if (hits.Count >= max) return hits;
            }
        }
        return hits;
    }

    private static (string Snippet, IReadOnlyList<(int, int)> Marks) MakeSnippet(NormPage p, IReadOnlyList<string> terms, int firstNorm)
    {
        var text = p.Original;
        var anchor = p.Map.Length == 0 ? 0 : p.Map[Math.Min(firstNorm, p.Map.Length - 1)];
        var start = Math.Max(0, anchor - SnippetLead);
        var prefix = start > 0 ? "…" : "";
        var end = Math.Min(text.Length, start + SnippetMax - prefix.Length);
        var suffix = "";
        if (end < text.Length) { end--; suffix = "…"; }
        if (end > start && char.IsHighSurrogate(text[end - 1])) end--;
        if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start])) start++;
        var body = text[start..end].Replace('\n', ' ');
        var snippet = prefix + body + suffix;

        var ranges = new List<(int S, int E)>();
        foreach (var t in terms)
        {
            var at = TextNormaliser.IndexOfWordStart(p.Norm, t, 0);
            while (at >= 0)
            {
                var s = p.Map[at];
                var e = p.Map[at + t.Length - 1] + 1;
                if (e < text.Length && char.IsHighSurrogate(text[e - 1]) && char.IsLowSurrogate(text[e])) e++; // keep pairs whole
                if (e > start && s < end) ranges.Add((Math.Max(s, start) - start + prefix.Length, Math.Min(e, end) - start + prefix.Length));
                if (s >= end) break;
                at = TextNormaliser.IndexOfWordStart(p.Norm, t, at + 1);
            }
        }
        ranges.Sort();
        var marks = new List<(int, int)>();
        var cs = -1; var ce = -1;
        foreach (var (s, e) in ranges)
        {
            if (cs < 0) { cs = s; ce = e; continue; }
            if (s <= ce) { ce = Math.Max(ce, e); continue; }
            marks.Add((cs, ce - cs)); cs = s; ce = e;
        }
        if (cs >= 0) marks.Add((cs, ce - cs));
        return (snippet, marks);
    }

    /// <summary>
    /// "Probably defined on page N": the first page of the document where the term appears in a short line (≤ 8 words, a likely
    /// heading) or is followed by ':' (a glossary entry); else the first page where it appears at all; null when it never does.
    /// </summary>
    public int? ProbablyDefinedOn(string term, string entry)
    {
        var t = TextNormaliser.Normalise(term ?? "").Trim();
        if (t.Length == 0) return null;
        var d = -1;
        for (var i = 0; i < Documents.Count; i++) if (Documents[i].Entry == entry) { d = i; break; }
        if (d < 0) return null;
        int? firstAny = null;
        foreach (var p in _norm[d])
        {
            if (TextNormaliser.IndexOfWordStart(p.Norm, t, 0) < 0) continue;
            firstAny ??= p.Page;
            foreach (var line in p.Original.Split('\n'))
            {
                var nl = TextNormaliser.Normalise(line);
                var at = TextNormaliser.IndexOfWordStart(nl, t, 0);
                if (at < 0) continue;
                var words = nl.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (words <= 8) return p.Page;
                while (at >= 0)
                {
                    var after = nl.AsSpan(at + t.Length).TrimStart(' ');
                    if (after.Length > 0 && after[0] == ':') return p.Page;
                    at = TextNormaliser.IndexOfWordStart(nl, t, at + 1);
                }
            }
        }
        return firstAny;
    }
}
