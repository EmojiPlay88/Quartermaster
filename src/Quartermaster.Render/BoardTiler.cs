using System.Text.Json;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>Builds (once) and loads the on-disk tile pyramid for a board image.</summary>
public sealed class BoardTiler(VmodArchive archive, string cacheDir, DiagnosticLog log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _decodeCount;

    /// <summary>Number of full-image decodes this instance performed (test hook for cache hits).</summary>
    internal int DecodeCount => _decodeCount;

    private sealed record Marker(int Width, int Height, int Levels, string? Image);

    public async Task<TileSet> EnsureTilesAsync(BoardDef board, IProgress<double>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Ensure(board, progress, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The key under which a board's <see cref="TileSet"/> is stored and looked up: its image name (board names are only unique per map).</summary>
    public static string TileKey(BoardDef board) => board.Image ?? "";

    private static string FolderFor(string image)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(image)))[..12].ToLowerInvariant();
        return Sanitize(image) + "_" + hash;
    }

    private static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) || c == '.' ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "board" : s;
    }

    private static TileSet Placeholder(BoardDef b) => new("", Math.Max(b.Width, 1), Math.Max(b.Height, 1), 0, isPlaceholder: true);

    private TileSet Ensure(BoardDef board, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(board.Image)) return Placeholder(board);
        var root = Path.Combine(cacheDir, "tiles", FolderFor(board.Image));
        var donePath = Path.Combine(root, "done.json");

        var existing = ReadMarker(donePath);
        if (existing != null && existing.Image == board.Image && existing.Width > 0 && existing.Height > 0
            && existing.Levels == TileSet.LevelsFor(existing.Width, existing.Height))
        {
            progress?.Report(1);
            return new TileSet(root, existing.Width, existing.Height, existing.Levels);
        }

        var entry = VmodArchive.ImagePath(board.Image);
        if (!archive.HasEntry(entry))
        {
            log.AddOnce("img:" + board.Image, DiagnosticSeverity.Warning, "image", $"Board image '{board.Image}' is missing from the module.", board.Name);
            return Placeholder(board);
        }

        byte[] data;
        using (var s = archive.OpenEntry(entry))
        using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
        Interlocked.Increment(ref _decodeCount);
        using var full = ImageDecoder.Decode(data);
        data = Array.Empty<byte>();
        if (full == null)
        {
            log.AddOnce("img:" + board.Image, DiagnosticSeverity.Warning, "image", $"Board image '{board.Image}' could not be decoded.", board.Name);
            return Placeholder(board);
        }

        Directory.CreateDirectory(root);
        if (File.Exists(donePath)) File.Delete(donePath);
        var set = new TileSet(root, full.Width, full.Height, TileSet.LevelsFor(full.Width, full.Height));
        var total = 0;
        for (var l = 0; l <= set.Levels; l++) total += set.TilesX(l) * set.TilesY(l);
        var written = 0;

        var cur = full;
        SKBitmap? owned = null;
        try
        {
            for (var level = 0; level <= set.Levels; level++)
            {
                ct.ThrowIfCancellationRequested();
                if (level > 0)
                {
                    var next = cur.Resize(new SKImageInfo(set.LevelWidth(level), set.LevelHeight(level), cur.ColorType, cur.AlphaType),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    owned?.Dispose();
                    owned = next ?? throw new InvalidOperationException("Resize failed.");
                    cur = owned;
                }
                Directory.CreateDirectory(Path.Combine(root, level.ToString()));
                for (var ty = 0; ty < set.TilesY(level); ty++)
                    for (var tx = 0; tx < set.TilesX(level); tx++)
                    {
                        ct.ThrowIfCancellationRequested();
                        WriteTile(cur, set.TilePath(level, tx, ty), tx, ty);
                        progress?.Report(++written / (double)total);
                    }
            }
        }
        finally
        {
            owned?.Dispose();
        }

        WriteAtomic(donePath, JsonSerializer.SerializeToUtf8Bytes(new Marker(set.Width, set.Height, set.Levels, board.Image)));
        return set;
    }

    private static void WriteTile(SKBitmap src, string path, int tx, int ty)
    {
        var x = tx * TileSet.TileSize;
        var y = ty * TileSet.TileSize;
        var rect = SKRectI.Create(x, y, Math.Min(TileSet.TileSize, src.Width - x), Math.Min(TileSet.TileSize, src.Height - y));
        using var sub = new SKBitmap();
        if (!src.ExtractSubset(sub, rect)) throw new InvalidOperationException("Tile extraction failed.");
        using var img = SKImage.FromBitmap(sub);
        using var png = img.Encode(SKEncodedImageFormat.Png, 100);
        WriteAtomic(path, png.ToArray());
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            fs.Write(bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static Marker? ReadMarker(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
