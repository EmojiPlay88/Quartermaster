using System.Security.Cryptography;
using System.Text;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>Extracts module documents into <c>&lt;cacheDir&gt;\docs</c> under collision-free, contained file names.</summary>
public static class DocumentCache
{
    /// <summary>Hash prefix of the full entry name plus the sanitized leaf name; never escapes <paramref name="cacheDir"/>\docs.</summary>
    public static string PathFor(string cacheDir, string entryName)
    {
        var bad = Path.GetInvalidFileNameChars();
        var leaf = entryName.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        var clean = new string(leaf.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (clean.Length == 0) clean = "document";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entryName)))[..8].ToLowerInvariant();
        return Path.Combine(cacheDir, "docs", hash + "_" + clean);
    }

    /// <summary>Extracts the entry once (atomic write) and returns the file path.</summary>
    public static string Extract(VmodArchive archive, string cacheDir, string entryName)
    {
        var path = PathFor(cacheDir, entryName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var tmp = path + ".tmp";
            using (var src = archive.OpenEntry(entryName))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                src.CopyTo(dst);
            File.Move(tmp, path, overwrite: true);
        }
        return path;
    }
}
