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
            if (File.Exists(path))
            {
                File.Delete(tmp);
                Touch(path);
            }
            else File.Move(tmp, path);
            return path;
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Marks a cached file as used now (NTFS may not update the access time by itself); pruning goes by it.</summary>
    static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public const long MaxTotalBytes = 1L << 30;

    /// <summary>
    /// Deletes cached files not used for <see cref="MaxAge"/> (and leftover temps older than a day), then the least recently used
    /// ones until the folder is at most <see cref="MaxTotalBytes"/>. Files in use are skipped. Returns how many were deleted.
    /// </summary>
    public static int Prune(DateTime? nowUtc = null, TimeSpan? maxAge = null, long? maxTotalBytes = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var age = maxAge ?? MaxAge;
        var cap = maxTotalBytes ?? MaxTotalBytes;
        if (!Directory.Exists(Folder)) return 0;
        var deleted = 0;
        bool Delete(FileInfo f)
        {
            try { f.Delete(); deleted++; return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        var files = new List<FileInfo>();
        foreach (var f in new DirectoryInfo(Folder).EnumerateFiles())
        {
            var used = f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc;
            var isTmp = f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase);
            if (now - used > (isTmp ? TimeSpan.FromDays(1) : age)) { Delete(f); continue; }
            if (!isTmp) files.Add(f);
        }
        long total = files.Sum(f => f.Length);
        foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc))
        {
            if (total <= cap) break;
            var len = f.Length;
            if (Delete(f)) total -= len;
        }
        return deleted;
    }
}
