namespace Quartermaster.Render;

/// <summary>
/// An OS secret store (macOS Keychain, Linux Secret Service) holding one secret per account under the service
/// <see cref="SecretStoreProtector.Service"/>. Never throws: any failure (missing library, no D-Bus session, refused) is false or null.
/// </summary>
public interface ISecretStore
{
    /// <summary>Stores (or replaces) the secret of <paramref name="account"/>; false when it could not be stored.</summary>
    bool TrySet(string account, string secret);

    /// <summary>The secret of <paramref name="account"/>, or null when there is none or it cannot be read.</summary>
    string? Get(string account);

    /// <summary>Removes the secret of <paramref name="account"/> (nothing when there is none).</summary>
    void Remove(string account);
}
