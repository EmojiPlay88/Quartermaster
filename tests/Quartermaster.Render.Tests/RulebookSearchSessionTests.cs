using Quartermaster.Render.Search;

namespace Quartermaster.Render.Tests;

/// <summary>The background rulebook index of a module: status texts, pending queries, rebuilds, failure and a quiet dispose.</summary>
[Collection(AppDataRootEnv.Name)]
public class RulebookSearchSessionTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-session-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldRoot = Environment.GetEnvironmentVariable(AppDataRoot.EnvVar);

    public RulebookSearchSessionTests()
    {
        Directory.CreateDirectory(_tmp);
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _tmp);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, _oldRoot);
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private static readonly byte[] Rulebook = SmokeFixtureWriter.InventedRulebook();

    private static (string Entry, string Title, Func<Stream> Open) Doc(string entry = "invented-rules.pdf") =>
        (entry, "Invented rules", () => new MemoryStream(Rulebook, writable: false));

    private static bool WaitFor(Func<bool> cond, int ms = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            if (cond()) return true;
            Thread.Sleep(10);
        }
        return cond();
    }

    private static readonly (string, string, Func<Stream>)[] NoDocs = Array.Empty<(string, string, Func<Stream>)>();

    [Fact]
    public void No_pdfs_status()
    {
        using var s = new RulebookSearchSession(new string('a', 64), NoDocs);
        Assert.Equal("This module has no PDF rulebooks to search.", s.Status);
        Assert.NotNull(s.Index);
        Assert.Empty(s.Index!.Documents);
        var r = s.Run("supply");
        Assert.False(r.Pending);
        Assert.Equal("Rulebook search: supply (0 pages)", r.Title);
    }

    [Fact]
    public void Progress_then_indexed_status()
    {
        var statuses = new List<string>();
        var release = new TaskCompletionSource();
        var subscribed = new TaskCompletionSource();   // the build starts in the constructor: report only once the test listens
        RulebookSearchSession? s = null;
        s = new RulebookSearchSession(new string('b', 64), new[] { Doc() }, async (sha, pdfs, progress, ct, prev, mem) =>
        {
            await subscribed.Task;
            progress!.Report(0.5);
            await release.Task;
            return await RulebookIndex.BuildAsync(sha, pdfs, progress, ct, prev, mem);
        });
        s.Changed += () => { lock (statuses) statuses.Add(s!.Status); };
        subscribed.SetResult();
        using var _ = s;
        Assert.True(WaitFor(() => { lock (statuses) return statuses.Contains("Indexing rulebooks… 50 %"); }));
        Assert.Null(s.Index);
        release.SetResult();
        Assert.True(WaitFor(() => s.Index != null));
        Assert.Equal("Rulebooks indexed: 1 searchable.", s.Status);
    }

    [Fact]
    public void Query_before_ready_runs_when_ready()
    {
        var gate = new TaskCompletionSource();
        using var s = new RulebookSearchSession(new string('c', 64), new[] { Doc() }, async (sha, pdfs, progress, ct, prev, mem) =>
        {
            await gate.Task;
            return await RulebookIndex.BuildAsync(sha, pdfs, progress, ct, prev, mem);
        });
        var early = s.Run("supply");
        Assert.True(early.Pending);
        Assert.Null(early.Rows);
        Assert.Equal("Rulebook search: supply (runs when indexing finishes)", early.Title);

        SearchResult? later = null;
        s.Changed += () => { if (s.Index != null) later ??= s.Run("supply"); };
        gate.SetResult();
        Assert.True(WaitFor(() => later != null));
        Assert.False(later!.Pending);
        Assert.Contains(later.Rows!, r => r.Kind == SearchRowKind.Hit);
        Assert.StartsWith("Rulebook search: supply (", later.Title);
        Assert.EndsWith(" pages)", later.Title);
    }

    [Fact]
    public void Rebuild_supersedes_and_keeps_old_index_until_ready()
    {
        var builds = 0;
        var gates = new List<TaskCompletionSource>();
        var tokens = new List<CancellationToken>();
        using var s = new RulebookSearchSession(new string('d', 64), new[] { Doc() }, async (sha, pdfs, progress, ct, prev, mem) =>
        {
            var n = Interlocked.Increment(ref builds);
            if (n > 1)
            {
                var g = new TaskCompletionSource();
                lock (gates) { gates.Add(g); tokens.Add(ct); }
                await g.Task;
                ct.ThrowIfCancellationRequested();
            }
            return await RulebookIndex.BuildAsync(sha, pdfs, progress, ct, prev, mem);
        });
        Assert.True(WaitFor(() => s.Index != null));
        var first = s.Index;

        s.Rebuild(new[] { Doc("opened.pdf") });
        Assert.True(WaitFor(() => { lock (gates) return gates.Count == 1; }));
        Assert.Same(first, s.Index);                        // the old index is usable while the new one builds
        Assert.StartsWith("Indexing rulebooks…", s.Status);

        s.Rebuild(new[] { Doc("opened.pdf"), Doc("opened2.pdf") });   // supersedes the second build
        Assert.True(WaitFor(() => { lock (gates) return gates.Count == 2; }));
        lock (gates) Assert.True(tokens[0].IsCancellationRequested);
        lock (gates) gates[0].SetResult();                  // the superseded build ends: its result is dropped
        Thread.Sleep(100);
        Assert.Same(first, s.Index);
        lock (gates) gates[1].SetResult();
        Assert.True(WaitFor(() => !ReferenceEquals(s.Index, first)));
        Assert.Equal(3, s.Index!.Documents.Count);
        Assert.Equal("Rulebooks indexed: 3 searchable.", s.Status);
    }

    [Fact]
    public void Failure_status_and_empty_index()
    {
        using var s = new RulebookSearchSession(new string('e', 64), new[] { Doc() },
            (sha, pdfs, progress, ct, prev, mem) => throw new InvalidOperationException("disk on fire"));
        Assert.True(WaitFor(() => s.Index != null));
        Assert.Equal("The rulebooks could not be indexed: disk on fire", s.Status);
        Assert.Empty(s.Index!.Documents);
        Assert.False(s.Run("supply").Pending);
    }

    [Fact]
    public void Dispose_during_build_is_quiet()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? o, UnobservedTaskExceptionEventArgs e) { lock (unobserved) unobserved.Add(e.Exception); }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var started = new ManualResetEventSlim();
            var changes = 0;
            var s = new RulebookSearchSession(new string('f', 64), new[] { Doc() }, async (sha, pdfs, progress, ct, prev, mem) =>
            {
                started.Set();
                await Task.Delay(Timeout.Infinite, ct);
                return RulebookIndex.Empty;
            });
            s.Changed += () => Interlocked.Increment(ref changes);
            Assert.True(started.Wait(10000));
            var before = Volatile.Read(ref changes);
            s.Dispose();
            Thread.Sleep(200);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Equal(before, Volatile.Read(ref changes));   // no event after dispose
            Assert.Null(s.Index);
            lock (unobserved) Assert.Empty(unobserved);
            s.Rebuild(NoDocs);   // a no-op after dispose
            s.Dispose();
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }
}
