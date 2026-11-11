namespace Quartermaster.Render.Search;

/// <summary>The outcome of <see cref="RulebookSearchSession.Run"/>: the panel title, the rows (null while <see cref="Pending"/>).</summary>
public sealed record SearchResult(string Title, IReadOnlyList<SearchRow>? Rows, bool Pending);

/// <summary>
/// The rulebook index of one module and its status, built in the background (port of the WinForms workbench's StartIndex /
/// BuildIndexAsync / RunSearch). <see cref="Rebuild"/> supersedes a running build; the old index stays usable until the new one is
/// ready. <see cref="Changed"/> may fire on any thread. Disposing cancels the build quietly (no event, no unobserved exception).
/// </summary>
public sealed class RulebookSearchSession : IDisposable
{
    /// <summary>The index builder (a test seam: the real one is <see cref="RulebookIndex.BuildAsync(string, IReadOnlyList{ValueTuple{string, string, Func{Stream}}}, IProgress{double}?, CancellationToken, RulebookIndex?, IReadOnlyCollection{string}?)"/>).</summary>
    internal delegate Task<RulebookIndex> IndexBuilder(string sha, IReadOnlyList<(string Entry, string Title, Func<Stream> Open)> pdfs,
        IProgress<double>? progress, CancellationToken ct, RulebookIndex? previous, IReadOnlyCollection<string>? memoryOnly);

    public const string NoPdfsText = "This module has no PDF rulebooks to search.";

    private readonly object _lock = new();
    private readonly string _sha;
    private readonly IReadOnlyList<(string Entry, string Title, Func<Stream> Open)> _modulePdfs;
    private readonly IndexBuilder _builder;
    private CancellationTokenSource? _build;
    private RulebookIndex? _index;
    private string _status = "";
    private int _gen;
    private bool _disposed;

    public RulebookSearchSession(string archiveSha, IReadOnlyList<(string Entry, string Title, Func<Stream> Open)> modulePdfs)
        : this(archiveSha, modulePdfs, RulebookIndex.BuildAsync) { }

    internal RulebookSearchSession(string archiveSha, IReadOnlyList<(string Entry, string Title, Func<Stream> Open)> modulePdfs, IndexBuilder builder)
    {
        _sha = archiveSha;
        _modulePdfs = modulePdfs;
        _builder = builder;
        Rebuild(Array.Empty<(string, string, Func<Stream>)>());
    }

    /// <summary>The index; null until the first build is ready (an older index stays here while a rebuild runs).</summary>
    public RulebookIndex? Index { get { lock (_lock) return _index; } }

    /// <summary>"Indexing rulebooks… n %", the result of the last build, or the failure text.</summary>
    public string Status { get { lock (_lock) return _status; } }

    /// <summary>Status or index changed. May be raised on any thread; never after <see cref="Dispose"/>.</summary>
    public event Action? Changed;

    /// <summary>Indexes the module's PDFs plus <paramref name="memoryOnly"/> ones (opened for this session; their text stays in memory).</summary>
    public void Rebuild(IReadOnlyList<(string Key, string Title, Func<Stream> Open)> memoryOnly)
    {
        var pdfs = _modulePdfs.ToList();
        pdfs.AddRange(memoryOnly);
        var keys = memoryOnly.Select(m => m.Key).ToList();
        CancellationTokenSource cts;
        RulebookIndex? previous;
        int gen;
        lock (_lock)
        {
            if (_disposed) return;
            try { _build?.Cancel(); }
            catch (ObjectDisposedException) { }   // that build already finished
            gen = ++_gen;
            previous = _index;
            if (pdfs.Count == 0)
            {
                _build = null;
                _index = RulebookIndex.Empty;
                _status = NoPdfsText;
                cts = null!;
            }
            else
            {
                _status = "Indexing rulebooks… 0 %";
                cts = _build = new CancellationTokenSource();
            }
        }
        if (cts == null) { Changed?.Invoke(); return; }
        Changed?.Invoke();
        _ = Task.Run(() => RunBuild(pdfs, keys, previous, gen, cts));
    }

    private async Task RunBuild(IReadOnlyList<(string, string, Func<Stream>)> pdfs, IReadOnlyCollection<string> keys, RulebookIndex? previous, int gen, CancellationTokenSource cts)
    {
        try
        {
            var last = -1;
            var progress = new DirectProgress(f =>
            {
                var pct = (int)Math.Clamp(f * 100, 0, 100);
                lock (_lock)
                {
                    if (_disposed || gen != _gen || pct == last) return;
                    last = pct;
                    _status = $"Indexing rulebooks… {pct} %";
                }
                Changed?.Invoke();
            });
            RulebookIndex ix;
            string? failed = null;
            try { ix = await _builder(_sha, pdfs, progress, cts.Token, previous, keys).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.WriteLine("rulebook index failed: " + ex);
                ix = RulebookIndex.Empty;
                failed = "The rulebooks could not be indexed: " + ex.Message;
            }
            lock (_lock)
            {
                if (_disposed || cts.IsCancellationRequested || gen != _gen) return;
                _index = ix;
                var bad = ix.Documents.Count(d => !d.Searchable);
                _status = failed ?? $"Rulebooks indexed: {ix.Documents.Count - bad} searchable" + (bad > 0 ? $", {bad} {SearchPanelModel.NotSearchableText}" : "") + ".";
            }
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("rulebook search session: " + ex);   // a Changed handler threw: never an unobserved exception
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_build, cts)) _build = null;
                cts.Dispose();
            }
        }
    }

    /// <summary>Searches the index. Before it is ready the result is <see cref="SearchResult.Pending"/> (the caller runs it again on <see cref="Changed"/>).</summary>
    public SearchResult Run(string query)
    {
        query = (query ?? "").Trim();
        var ix = Index;
        if (query.Length == 0) return new SearchResult("", Array.Empty<SearchRow>(), false);
        if (ix == null) return new SearchResult($"Rulebook search: {query} (runs when indexing finishes)", null, true);
        var rows = SearchPanelModel.Build(ix, query);
        var pages = rows.Count(r => r.Kind == SearchRowKind.Hit);
        return new SearchResult($"Rulebook search: {query} ({pages} page{(pages == 1 ? "" : "s")})", rows, false);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _build?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private sealed class DirectProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
