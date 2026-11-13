using System.Security.Cryptography;
using System.Text;

namespace Quartermaster.Render;

/// <summary>
/// Keeps the mailbox password in an <see cref="ISecretStore"/> under a fresh random account and returns only a handle,
/// <c>"qm-secret:v1:" + account</c> (UTF-8), as the protected bytes. Any other bytes (a DPAPI blob copied from Windows) or a handle the
/// store no longer has read back as no password.
/// </summary>
public sealed class SecretStoreProtector(ISecretStore store) : IPasswordProtector
{
    /// <summary>The service name the password is stored under.</summary>
    public const string Service = "Quartermaster turn mailbox";

    private const string HandlePrefix = "qm-secret:v1:";
    private const int AccountLength = 32;   // Guid "N"

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ISecretStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <exception cref="CryptographicException">The store refused the secret (<see cref="KeptForSessionException"/> when it is kept for this session only).</exception>
    public byte[] Protect(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var account = Guid.NewGuid().ToString("N");
        var secret = StrictUtf8.GetString(plain);
        var handle = Encoding.UTF8.GetBytes(HandlePrefix + account);
        if (_store.TrySet(account, secret)) return handle;
        if (_store.Get(account) == secret) throw new KeptForSessionException(handle);
        throw new CryptographicException("The password could not be stored.");
    }

    /// <exception cref="CryptographicException">Not a handle of this protector, or the store does not have it.</exception>
    public byte[] Unprotect(byte[] protectedBytes)
    {
        var account = AccountOf(protectedBytes) ?? throw new CryptographicException("Not a stored password handle.");
        var secret = _store.Get(account) ?? throw new CryptographicException("The stored password is not available.");
        return Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>Removes the stored secret behind a handle (anything else is ignored).</summary>
    public void Forget(byte[] blob)
    {
        if (AccountOf(blob) is { } account) _store.Remove(account);
    }

    private static string? AccountOf(byte[]? bytes)
    {
        if (bytes == null || bytes.Length != HandlePrefix.Length + AccountLength) return null;
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (ArgumentException) { return null; }
        if (!text.StartsWith(HandlePrefix, StringComparison.Ordinal)) return null;
        var account = text[HandlePrefix.Length..];
        return account.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? account : null;
    }
}

/// <summary>The store kept the password for this session only: <see cref="Handle"/> still reads it back until the app closes.</summary>
internal sealed class KeptForSessionException(byte[] handle) : CryptographicException("The password is kept for this session only.")
{
    public byte[] Handle { get; } = handle;
}
