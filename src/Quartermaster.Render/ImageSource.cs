using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render;

/// <summary>
/// Loads module images by name. Raster images decode once; SVGs render at a bucketed scale. Results sit in an LRU
/// capped at 512 MB of estimated pixels. A missing or undecodable image yields a 64x64 checker plus one diagnostic.
/// </summary>
public sealed class ImageSource(VmodArchive archive, string cacheDir, DiagnosticLog logSink)
{
    private static readonly float[] Buckets = { 0.25f, 0.5f, 1f, 2f, 4f };

    private sealed class Info
    {
        public bool Missing;
        public SKPicture? Svg;
        public SKSize Size;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Info> _infos = new(StringComparer.Ordinal);
    private readonly LruImageCache _cache = new(512L * 1024 * 1024);
    private SKImage? _placeholder;

    public DiagnosticLog Log { get; } = logSink;

    /// <summary>Folder that holds this module's on-disk caches.</summary>
    public string CacheDir { get; } = cacheDir;

    public static float Bucket(float scale)
    {
        foreach (var b in Buckets) if (scale <= b + 1e-4f) return b;
        return Buckets[^1];
    }

    /// <summary>The image at <paramref name="scale"/> (only SVGs depend on it). Never null.</summary>
    public SKImage Get(string imageName, float scale = 1f)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(imageName)) return Placeholder();
            var info = InfoOf(imageName);
            if (info.Missing) return Placeholder();
            if (info.Svg != null)
            {
                var bucket = Bucket(scale);
                var key = imageName + "@" + bucket.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var hit = _cache.TryGet(key);
                if (hit != null) return hit;
                using var bmp = ImageDecoder.RenderSvg(info.Svg, info.Size, bucket);
                var img = bmp == null ? Placeholder() : SKImage.FromBitmap(bmp);
                _cache.Put(key, img);
                return img;
            }
            return _cache.TryGet(imageName) ?? Reload(imageName, info);
        }
    }

    /// <summary>Natural size in pixels (SVG: its declared size at scale 1). The placeholder size when missing.</summary>
    public SKSizeI SizeOf(string imageName)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(imageName)) return new SKSizeI(64, 64);
            var info = InfoOf(imageName);
            if (info.Missing) return new SKSizeI(64, 64);
            return new SKSizeI((int)Math.Ceiling(info.Size.Width), (int)Math.Ceiling(info.Size.Height));
        }
    }

    private SKImage Placeholder() => _placeholder ??= ImageDecoder.Placeholder();

    private Info InfoOf(string name)
    {
        if (_infos.TryGetValue(name, out var cached)) return cached;
        var info = Load(name);
        _infos[name] = info;
        return info;
    }

    private byte[]? ReadBytes(string name)
    {
        var entry = VmodArchive.ImagePath(name);
        if (!archive.HasEntry(entry)) return null;
        try
        {
            using var s = archive.OpenEntry(entry);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    private Info Load(string name)
    {
        var data = ReadBytes(name);
        if (data == null) return Fail(name, $"Image '{name}' is missing from the module.");
        if (ImageDecoder.LooksLikeSvg(data))
        {
            if (ImageDecoder.LoadSvg(data) is { } svg) return new Info { Svg = svg.Picture, Size = svg.Size };
            return Fail(name, $"Image '{name}' is not a readable SVG.");
        }
        var size = ImageSize.Read(name, data);
        if (size is { } s) return PutDecoded(name, data, new Info { Size = new SKSize((float)s.W, (float)s.H) }) ?? Fail(name, $"Image '{name}' could not be decoded.");
        return PutDecoded(name, data, new Info()) ?? Fail(name, $"Image '{name}' could not be decoded.");
    }

    private Info? PutDecoded(string name, byte[] data, Info info)
    {
        using var bmp = ImageDecoder.Decode(data);
        if (bmp == null) return null;
        if (info.Size.Width <= 0) info.Size = new SKSize(bmp.Width, bmp.Height);
        _cache.Put(name, SKImage.FromBitmap(bmp));
        return info;
    }

    private SKImage Reload(string name, Info info)
    {
        var data = ReadBytes(name);
        using var bmp = data == null ? null : ImageDecoder.Decode(data);
        if (bmp == null)
        {
            info.Missing = true;
            return Placeholder();
        }
        var img = SKImage.FromBitmap(bmp);
        _cache.Put(name, img);
        return img;
    }

    private Info Fail(string name, string message)
    {
        Log.AddOnce("img:" + name, DiagnosticSeverity.Warning, "image", message, name);
        return new Info { Missing = true, Size = new SKSize(64, 64) };
    }
}
