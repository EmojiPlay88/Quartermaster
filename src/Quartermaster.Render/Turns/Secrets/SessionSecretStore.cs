using System.Collections.Concurrent;

namespace Quartermaster.Render;

/// <summary>
/// The fallback when the OS has no usable secret store: secrets live in memory for this process only. <see cref="TrySet"/> keeps the
/// secret but returns false (it was not stored), while <see cref="Get"/> still finds it until the app closes.
/// </summary>
public sealed class SessionSecretStore : ISecretStore
{
    /// <summary>The one process-wide session store.</summary>
    public static readonly SessionSecretStore Instance = new();

    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public bool TrySet(string account, string secret)
    {
        if (account == null || secret == null) return false;
        _secrets[account] = secret;
        return false;
    }

    public string? Get(string account) => account != null && _secrets.TryGetValue(account, out var s) ? s : null;

    public void Remove(string account)
    {
        if (account != null) _secrets.TryRemove(account, out _);
    }
}
