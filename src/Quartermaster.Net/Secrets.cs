using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Quartermaster.Net;

public static class Secrets
{
    public const int Iterations = 200_000;
    private const int NonceSize = 12, TagSize = 16;

    private static byte[] Derive(string password, string salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt),
            Iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Relay-facing proof of the password (lower-case hex). Different salt from the key.</summary>
    public static string AuthVerifier(string password, string code) =>
        Convert.ToHexString(Derive(password, $"qm-auth:{code}")).ToLowerInvariant();

    /// <summary>End-to-end key; never leaves the clients.</summary>
    public static byte[] GameKey(string password, string code) => Derive(password, $"qm-key:{code}");

    /// <summary>nonce(12) || ciphertext || tag(16); plaintext is gzipped first.</summary>
    public static byte[] Seal(byte[] key, byte[] plain)
    {
        byte[] packed;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(plain);
            packed = ms.ToArray();
        }
        var result = new byte[NonceSize + packed.Length + TagSize];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, packed, result.AsSpan(NonceSize, packed.Length), result.AsSpan(NonceSize + packed.Length, TagSize));
        return result;
    }

    /// <summary>Throws CryptographicException on tamper, wrong key or short input.</summary>
    public static byte[] Open(byte[] key, byte[] sealed_)
    {
        if (sealed_.Length < NonceSize + TagSize) throw new CryptographicException("Sealed data is too short.");
        int n = sealed_.Length - NonceSize - TagSize;
        var packed = new byte[n];
        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(sealed_.AsSpan(0, NonceSize), sealed_.AsSpan(NonceSize, n),
                sealed_.AsSpan(NonceSize + n, TagSize), packed);
        try
        {
            using var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return outMs.ToArray();
        }
        catch (InvalidDataException e) { throw new CryptographicException("Sealed data is corrupt.", e); }
    }
}
