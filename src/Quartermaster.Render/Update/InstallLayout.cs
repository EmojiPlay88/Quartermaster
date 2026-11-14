namespace Quartermaster.Render.Update;

/// <summary>
/// Where an installed copy lives and what an update replaces. <c>AppDir</c> holds the executable and the marker; <c>UnitPath</c> is the
/// single thing swapped for bundle-like kinds (the macOS <c>.app</c>, the AppImage file) and null otherwise.
/// </summary>
public sealed record InstallLayout(PackageKind Kind, Version Version, string AppDir, string? UnitPath, string ExePath)
{
    /// <summary>False for system packages (deb, rpm), which the package manager updates.</summary>
    public bool SelfUpdates => Kind is not (PackageKind.LinuxDeb or PackageKind.LinuxRpm);

    /// <summary>
    /// Reads the marker in <paramref name="baseDir"/> and derives the layout; null when there is no valid marker or the layout does not
    /// fit the kind (a Mac marker outside a <c>.app</c>, an AppImage without a usable <c>APPIMAGE</c>). The executable name follows the
    /// package kind, not the running OS, so this is testable anywhere.
    /// </summary>
    public static InstallLayout? Detect(string baseDir, Func<string, string?> env)
    {
        var info = PackageFile.Read(baseDir);
        if (info is null) return null;
        switch (info.Kind)
        {
            case PackageKind.MacApp:
            {
                var full = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var bundle = Path.GetDirectoryName(Path.GetDirectoryName(full));
                if (bundle is null || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
                return new InstallLayout(info.Kind, info.Version, baseDir, bundle, Path.Combine(baseDir, "Quartermaster"));
            }
            case PackageKind.LinuxAppImage:
            {
                var img = env("APPIMAGE");
                if (string.IsNullOrWhiteSpace(img) || !File.Exists(img)) return null;
                return new InstallLayout(info.Kind, info.Version, baseDir, img, img);
            }
            default:
            {
                var win = info.Kind is PackageKind.WinZip or PackageKind.WinSetup;
                return new InstallLayout(info.Kind, info.Version, baseDir, null, Path.Combine(baseDir, win ? "Quartermaster.exe" : "Quartermaster"));
            }
        }
    }
}
