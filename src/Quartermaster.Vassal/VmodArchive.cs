using System.IO.Compression;
using System.Security.Cryptography;

namespace Quartermaster.Vassal;

/// <summary>Read-only view of a .vmod (zip). Holds the file open (shared) for its lifetime; thread-safe for entry access.</summary>
public sealed class VmodArchive : IDisposable
{
    private readonly FileStream _file;
    private readonly ZipArchive _zip;
    private readonly object _gate = new();
    private readonly Dictionary<string, ZipArchiveEntry> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZipArchiveEntry> _ignoreCase = new(StringComparer.OrdinalIgnoreCase);

    private VmodArchive(string path, FileStream file, ZipArchive zip, string sha256)
    {
        Path = path;
        _file = file;
        _zip = zip;
        Sha256 = sha256;
        foreach (var e in zip.Entries)
        {
            _exact.TryAdd(e.FullName, e);
            _ignoreCase.TryAdd(e.FullName, e);
        }
        EntryNames = _exact.Keys.ToArray();
        Root = null!;
    }

    public string Path { get; }
    public string Sha256 { get; }
    public ModuleNode Root { get; private set; }
    public IReadOnlyCollection<string> EntryNames { get; }

    public static string ImagePath(string imageName) => "images/" + imageName;

    static string Sha256Of(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

    /// <summary>The module hash <see cref="Sha256"/> would have, without opening the archive. Throws on I/O errors.</summary>
    public static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Sha256Of(fs);
    }

    /// <summary>Opens a module. Throws <see cref="InvalidDataException"/> if it is not a zip or has no buildFile.</summary>
    public static VmodArchive Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var sha = Sha256Of(fs);
            fs.Position = 0;
            var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
            try
            {
                var a = new VmodArchive(path, fs, zip, sha);
                var name = a.HasEntry("buildFile.xml") ? "buildFile.xml"
                         : a.HasEntry("buildFile") ? "buildFile"
                         : throw new InvalidDataException("Not a VASSAL module: no buildFile.xml");
                using var s = a.OpenEntry(name);
                a.Root = BuildFileParser.Parse(s);
                return a;
            }
            catch
            {
                zip.Dispose();
                throw;
            }
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    private ZipArchiveEntry? Find(string name) =>
        _exact.TryGetValue(name, out var e) || _ignoreCase.TryGetValue(name, out e) ? e : null;

    public bool HasEntry(string name)
    {
        lock (_gate) return Find(name) != null;
    }

    /// <summary>Returns a seekable in-memory copy of the entry; the caller may dispose it freely.</summary>
    public Stream OpenEntry(string name)
    {
        lock (_gate)
        {
            var e = Find(name) ?? throw new FileNotFoundException("Entry not found in module: " + name, name);
            var ms = new MemoryStream((int)Math.Min(e.Length, int.MaxValue));
            using (var s = e.Open()) s.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _zip.Dispose();
            _file.Dispose();
        }
    }
}
