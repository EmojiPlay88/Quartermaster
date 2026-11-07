using System.IO.Compression;
using System.Security.Cryptography;

namespace Quartermaster.Net;

public static class Gzip
{
    public static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(data);
        return ms.ToArray();
    }

    /// <summary>Inflates at most maxBytes; throws CryptographicException if exceeded or corrupt.</summary>
    public static byte[] DecompressBounded(byte[] data, int maxBytes)
    {
        try
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = gz.Read(buf, 0, buf.Length)) > 0)
            {
                if (output.Length + n > maxBytes) throw new CryptographicException("Decompressed data is too large.");
                output.Write(buf, 0, n);
            }
            return output.ToArray();
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new CryptographicException("Compressed data is corrupt.", e);
        }
    }
}
