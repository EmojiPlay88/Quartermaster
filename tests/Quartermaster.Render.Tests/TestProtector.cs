using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Keeps every Render test away from the real OS secret store: <see cref="PasswordProtectors.Override"/> is an in-memory protector for the
/// whole test assembly, so a <c>TurnIdentity.Load(..., null)</c> anywhere never reaches DPAPI, the Keychain or the Secret Service.
/// </summary>
internal static class TestProtector
{
    /// <summary>A fresh in-memory protector (its own store and session fallback).</summary>
    public static SecretStoreProtector Create() => new(new MemoryStore(), new SessionSecretStore());

    [ModuleInitializer]
    internal static void Init() => PasswordProtectors.Override = Create();

    private sealed class MemoryStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public bool TrySet(string account, string secret)
        {
            _secrets[account] = secret;
            return true;
        }

        public string? Get(string account) => _secrets.TryGetValue(account, out var s) ? s : null;
        public void Remove(string account) => _secrets.TryRemove(account, out _);
    }
}
