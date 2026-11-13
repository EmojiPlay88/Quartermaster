using System.Runtime.CompilerServices;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Tests;

/// <summary>Points the app data root (layout memory, phase-boundary autosaves) at a per-run temp folder, so windows built without a layout
/// root never write into the real profile, and keeps mailbox passwords in memory, so no test reaches a real OS secret store.</summary>
static class TestDataRoot
{
    public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qm-desktop-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataRoot.EnvVar)))
            Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, Path);
        PasswordProtectors.Override = new SecretStoreProtector(new MemorySecretStore());   // never a real keychain
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }
}

/// <summary>An in-memory secret store that always stores (the tests' stand-in for the Keychain / Secret Service).</summary>
sealed class MemorySecretStore : ISecretStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _secrets = new();

    public bool TrySet(string account, string secret)
    {
        _secrets[account] = secret;
        return true;
    }

    public string? Get(string account) => _secrets.TryGetValue(account, out var s) ? s : null;
    public void Remove(string account) => _secrets.TryRemove(account, out _);
}
