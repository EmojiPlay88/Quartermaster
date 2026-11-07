using System.Security.Cryptography;
using System.Text;

namespace Quartermaster.Net;

/// <remarks>
/// Open lobbies use the empty password: anyone who sees the code, including the relay, can derive the key and read the game.
/// Never mix attacker-controlled text and secrets in one sealed payload (compressed length leaks content).
/// </remarks>
public static class Secrets
{
    public const int Iterations = 200_000;
    /// <summary>Largest plaintext Open will inflate (guards against gzip bombs).</summary>
    public const int MaxPlain = 64 * 1024 * 1024;
    private const int NonceSize = 12, TagSize = 16;

    private static void RequireKey(byte[] key)
    {
        if (key is not { Length: 32 }) throw new ArgumentException("The game key must be exactly 32 bytes.", nameof(key));
    }

    private static byte[] Derive(string password, string salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt),
            Iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Relay-facing proof of the password (lower-case hex). Code is normalised; different salt from the key.</summary>
    public static string AuthVerifier(string password, string code) =>
        Convert.ToHexString(Derive(password, $"qm-auth:{GameCode.Normalize(code)}")).ToLowerInvariant();

    /// <summary>Constant-time comparison of two hex verifiers (case-insensitive). False if either is not valid hex.</summary>
    public static bool VerifierEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b)); }
        catch (FormatException) { return false; }
    }

    /// <summary>A fresh host key for <see cref="CreateRoom.HostKey"/>: 32 random bytes, lower-case hex.</summary>
    public static string NewHostKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>End-to-end key; never leaves the clients.</summary>
    public static byte[] GameKey(string password, string code) => Derive(password, $"qm-key:{GameCode.Normalize(code)}");

    /// <summary>Turn mailbox: key behind every box id of a game. GameId is the random game GUID only players hold; empty password = none.</summary>
    public static byte[] MailboxBoxKey(string password, string gameId) => Derive(password, $"qm-mbox:{gameId}");

    /// <summary>Lower-hex SHA-256(box key ‖ UTF-8 player id): the relay-facing id of one player's box.</summary>
    public static string MailboxBoxId(byte[] boxKey, string playerId)
    {
        RequireKey(boxKey);
        var player = Encoding.UTF8.GetBytes(playerId);
        var data = new byte[boxKey.Length + player.Length];
        boxKey.CopyTo(data, 0);
        player.CopyTo(data, boxKey.Length);
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    /// <summary>Turn mailbox: key that seals the turn files (<see cref="Seal"/>); never leaves the clients.</summary>
    public static byte[] MailboxSealKey(string password, string gameId) => Derive(password, $"qm-mbox-key:{gameId}");

    /// <summary>nonce(12) || ciphertext || tag(16); plaintext is gzipped first.</summary>
    public static byte[] Seal(byte[] key, byte[] plain)
    {
        RequireKey(key);
        var packed = Gzip.Compress(plain);
        var result = new byte[NonceSize + packed.Length + TagSize];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, packed, result.AsSpan(NonceSize, packed.Length), result.AsSpan(NonceSize + packed.Length, TagSize));
        return result;
    }

    /// <summary>Throws CryptographicException on tamper, wrong key, short input, corrupt or over-large (MaxPlain) data.</summary>
    public static byte[] Open(byte[] key, byte[] sealed_) => Open(key, sealed_, MaxPlain);

    /// <summary>As <see cref="Open(byte[], byte[])"/>, inflating at most <paramref name="maxPlain"/> bytes (CryptographicException beyond).</summary>
    public static byte[] Open(byte[] key, byte[] sealed_, int maxPlain)
    {
        RequireKey(key);
        if (sealed_.Length < NonceSize + TagSize) throw new CryptographicException("Sealed data is too short.");
        int n = sealed_.Length - NonceSize - TagSize;
        var packed = new byte[n];
        using (var aes = new AesGcm(key, TagSize))
            aes.Decrypt(sealed_.AsSpan(0, NonceSize), sealed_.AsSpan(NonceSize, n),
                sealed_.AsSpan(NonceSize + n, TagSize), packed);
        return Gzip.DecompressBounded(packed, Math.Clamp(maxPlain, 0, MaxPlain));
    }
}
