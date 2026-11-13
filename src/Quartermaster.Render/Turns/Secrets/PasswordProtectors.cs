namespace Quartermaster.Render;

/// <summary>Chooses where this OS keeps the mailbox password.</summary>
public static class PasswordProtectors
{
    private static readonly Lazy<IPasswordProtector> Default = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    private static volatile IPasswordProtector? _override;

    /// <summary>When set, <see cref="ForThisOs"/> returns it (the test seam: tests never reach a real keychain).</summary>
    public static IPasswordProtector? Override
    {
        get => _override;
        set => _override = value;
    }

    /// <summary>
    /// <see cref="Override"/> when set; otherwise DPAPI on Windows, the login Keychain on macOS, the Secret Service on Linux when it is
    /// reachable, else a store that keeps the password for this session only. Chosen once per process.
    /// </summary>
    public static IPasswordProtector ForThisOs() => _override ?? Default.Value;

    private static IPasswordProtector Resolve()
    {
        if (OperatingSystem.IsWindows()) return DpapiProtector.Instance;
        if (OperatingSystem.IsMacOS()) return new SecretStoreProtector(new MacKeychainStore());
        if (OperatingSystem.IsLinux() && LinuxSecretServiceStore.IsAvailable()) return new SecretStoreProtector(new LinuxSecretServiceStore());
        return new SecretStoreProtector(SessionSecretStore.Instance);
    }
}
