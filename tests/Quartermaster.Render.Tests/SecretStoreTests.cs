using System.Text;

namespace Quartermaster.Render.Tests;

/// <summary>The mailbox password in an OS secret store: fake stores only, except the opt-in <see cref="SecretStoreFactAttribute"/> test.</summary>
public class SecretStoreTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-secrets-" + Guid.NewGuid().ToString("N"));

    public SecretStoreTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    /// <summary>In-memory store; <see cref="Refuse"/> makes every write fail.</summary>
    private sealed class FakeStore : ISecretStore
    {
        public readonly Dictionary<string, string> Entries = new();
        public bool Refuse { get; init; }

        public bool TrySet(string account, string secret)
        {
            if (Refuse) return false;
            Entries[account] = secret;
            return true;
        }

        public string? Get(string account) => Entries.TryGetValue(account, out var s) ? s : null;
        public void Remove(string account) => Entries.Remove(account);
    }

    private static readonly byte[] Prefix = Encoding.UTF8.GetBytes("qm-secret:v1:");

    private TurnIdentity Load(string gameId, IPasswordProtector protector) => TurnIdentity.Load(gameId, _tmp, protector);

    [Fact]
    public void Store_protector_round_trips_through_identity_save_and_load()
    {
        var store = new FakeStore();
        var gameId = Guid.NewGuid().ToString("N");
        var id = Load(gameId, new SecretStoreProtector(store));
        Assert.False(id.HasStoredPassword);
        Assert.True(id.SetPassword("hunter2"));
        Assert.True(id.HasStoredPassword);
        Assert.StartsWith(Encoding.UTF8.GetString(Prefix), Encoding.UTF8.GetString(id.ProtectedPassword!));
        id.Save();

        var text = File.ReadAllText(id.FilePath);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("hunter2")), text);
        Assert.Contains(Convert.ToBase64String(id.ProtectedPassword!), text);
        Assert.Equal("hunter2", Assert.Single(store.Entries).Value);

        var back = Load(gameId, new SecretStoreProtector(store));
        Assert.True(back.HasStoredPassword);
        Assert.Equal("hunter2", back.GetPassword());
    }

    [Fact]
    public void Replacing_password_forgets_old_entry()
    {
        var store = new FakeStore();
        var id = Load(Guid.NewGuid().ToString("N"), new SecretStoreProtector(store));
        Assert.True(id.SetPassword("first"));
        var oldAccount = Assert.Single(store.Entries).Key;
        Assert.True(id.SetPassword("second"));
        Assert.Equal("second", Assert.Single(store.Entries).Value);
        Assert.Null(store.Get(oldAccount));
        Assert.Equal("second", id.GetPassword());

        Assert.True(id.SetPassword(""));   // clearing forgets too
        Assert.Empty(store.Entries);
        Assert.False(id.HasStoredPassword);
    }

    [Fact]
    public void Missing_store_falls_back_to_session()
    {
        var refusing = Load(Guid.NewGuid().ToString("N"), new SecretStoreProtector(new FakeStore { Refuse = true }));
        Assert.False(refusing.SetPassword("pw"));
        Assert.Null(refusing.ProtectedPassword);
        Assert.Null(refusing.GetPassword());

        var session = new SessionSecretStore();
        Assert.False(session.TrySet("acct", "kept"));
        Assert.Equal("kept", session.Get("acct"));

        var gameId = Guid.NewGuid().ToString("N");
        var id = Load(gameId, new SecretStoreProtector(session));
        Assert.False(id.SetPassword("hunter2"));   // not stored: only kept for this session
        Assert.True(id.HasStoredPassword);
        Assert.Equal("hunter2", id.GetPassword());
        id.Save();
        Assert.DoesNotContain("hunter2", File.ReadAllText(id.FilePath));
        Assert.Equal("hunter2", Load(gameId, new SecretStoreProtector(session)).GetPassword());

        var nextRun = Load(gameId, new SecretStoreProtector(new SessionSecretStore()));
        Assert.True(nextRun.HasStoredPassword);
        Assert.Null(nextRun.GetPassword());
        Assert.Same(SessionSecretStore.Instance, SessionSecretStore.Instance);
    }

    [Fact]
    public void Dpapi_blob_off_windows_reads_as_no_password()
    {
        var store = new FakeStore();
        var protector = new SecretStoreProtector(store);
        var id = Load(Guid.NewGuid().ToString("N"), protector);
        var blob = new byte[178];
        new Random(7).NextBytes(blob);
        id.ProtectedPassword = blob;
        Assert.True(id.HasStoredPassword);
        Assert.Null(id.GetPassword());
        protector.Forget(blob);   // foreign bytes: nothing to forget, no throw

        id.ProtectedPassword = Encoding.UTF8.GetBytes("qm-secret:v1:" + Guid.NewGuid().ToString("N"));   // a handle the store no longer has
        Assert.Null(id.GetPassword());
        id.ProtectedPassword = Encoding.UTF8.GetBytes("qm-secret:v1:../../x");
        Assert.Null(id.GetPassword());
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(blob));
    }

    [WindowsFact]
    public void Windows_default_is_dpapi()
    {
        Assert.Null(PasswordProtectors.Override);
        Assert.Same(DpapiProtector.Instance, PasswordProtectors.ForThisOs());
        var id = TurnIdentity.Load(Guid.NewGuid().ToString("N"), _tmp, null);
        Assert.True(id.SetPassword("secret"));
        Assert.False(id.ProtectedPassword!.AsSpan().StartsWith(Prefix));   // a DPAPI blob, not a store handle
        Assert.Equal("secret", id.GetPassword());
    }

    [Fact]
    public void Override_wins_while_set()
    {
        var fake = new SecretStoreProtector(new FakeStore());
        try
        {
            PasswordProtectors.Override = fake;
            Assert.Same(fake, PasswordProtectors.ForThisOs());
        }
        finally { PasswordProtectors.Override = null; }
    }

    [SecretStoreFact]
    public void Real_store_round_trip()
    {
        ISecretStore store = OperatingSystem.IsMacOS() ? new MacKeychainStore() : new LinuxSecretServiceStore();
        var account = "qm-test-" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(store.TrySet(account, "hunter2 é"));
            Assert.Equal("hunter2 é", store.Get(account));
            Assert.True(store.TrySet(account, "replaced"));
            Assert.Equal("replaced", store.Get(account));
        }
        finally { store.Remove(account); }
        Assert.Null(store.Get(account));
        store.Remove(account);   // already gone: no throw
    }
}

/// <summary>Runs only when QUARTERMASTER_SECRET_STORE_TESTS=1 on macOS or Linux (it touches the real OS secret store).</summary>
public sealed class SecretStoreFactAttribute : FactAttribute
{
    public SecretStoreFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("QUARTERMASTER_SECRET_STORE_TESTS") != "1")
            Skip = "Real OS secret store: set QUARTERMASTER_SECRET_STORE_TESTS=1.";
        else if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            Skip = "macOS Keychain or Linux Secret Service only.";
    }
}
