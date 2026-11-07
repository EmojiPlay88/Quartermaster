using System.Security.Cryptography;

namespace Quartermaster.Render.Ocr;

/// <summary>
/// Module PDFs and images live inside the .vmod; Windows.Data.Pdf and the image loader need a file path. Entries are copied to
/// <c>AppDataRoot/scan-cache/{sha256}{ext}</c> (honours QUARTERMASTER_LAYOUT_ROOT) and reused when the same bytes are there.
/// </summary>
public static class ScanCache
{
    public static string Folder => AppDataRoot.Sub("scan-cache");

    /// <summary>Copies the stream to the cache (hashing as it goes) and returns the cached file's path.</summary>
    public static string Extract(Func<Stream> open, string extension, CancellationToken ct = default)
    {
        var ext = string.IsNullOrEmpty(extension) ? "" : (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant();
        if (ext.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) ext = "";
        Directory.CreateDirectory(Folder);
        var tmp = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
        string sha;
        try
        {
            using (var src = open())
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buf = new byte[81920];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hash.AppendData(buf, 0, n);
                    dst.Write(buf, 0, n);
                }
                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            var path = Path.Combine(Folder, sha + ext);
            if (File.Exists(path)) File.Delete(tmp);
            else File.Move(tmp, path);
            return path;
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
