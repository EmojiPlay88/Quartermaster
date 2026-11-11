using Quartermaster.Render.Ocr;
using SkiaSharp;

namespace Quartermaster.Documents.Tests;

/// <summary>Runs alone: <see cref="ThreadPool.ThreadCount"/> is process-wide, so tests running in parallel would grow it (seen as +6 on macos-latest, PR #3).</summary>
[CollectionDefinition(nameof(ThreadPoolSensitive), DisableParallelization = true)]
public sealed class ThreadPoolSensitive { }

[Collection(nameof(ThreadPoolSensitive))]
public class TesseractPoolTests
{
    [Fact]
    public async Task Queued_reads_hold_no_pool_threads()
    {
        using var t = new TesseractRecognizer();
        using var img = InventedTables.Line("Alpha", 30f);
        var hold = await t.HoldGateAsync();
        int before = ThreadPool.ThreadCount;
        var reads = Enumerable.Range(0, 64).Select(_ => t.RecognizeAsync(img, OcrLayout.Block, "eng", CancellationToken.None)).ToArray();
        await Task.Delay(400);
        int grown = ThreadPool.ThreadCount - before;
        Assert.True(grown <= 4, $"pool grew by {grown}");
        var fresh = Task.Run(() => 1);
        Assert.Same(fresh, await Task.WhenAny(fresh, Task.Delay(500)));
        Assert.All(reads, r => Assert.False(r.IsCompleted));
        hold.Dispose();
        var all = await Task.WhenAll(reads);
        Assert.Equal(64, all.Length);
    }
}
