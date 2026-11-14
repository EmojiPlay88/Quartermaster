using System.Text.Json;

namespace Quartermaster.Render.Update;

/// <summary>How this copy of Quartermaster was packaged; decides how an update is installed.</summary>
public enum PackageKind { WinZip, WinSetup, MacApp, LinuxTar, LinuxAppImage, LinuxDeb, LinuxRpm }

/// <summary>The packaged version and kind, as written into <see cref="PackageFile.FileName"/> at packaging time.</summary>
public sealed record PackageInfo(Version Version, PackageKind Kind);

/// <summary>Reads and writes the <c>quartermaster-package.json</c> marker that sits beside the app executable.</summary>
public static class PackageFile
{
    public const string FileName = "quartermaster-package.json";

    /// <summary>Reads the marker in <paramref name="dir"/>. Never throws: a missing, unreadable or malformed file, an unknown kind or a version that is not three-part gives null.</summary>
    public static PackageInfo? Read(string dir)
    {
        try
        {
            var path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("package", out var p) || p.ValueKind != JsonValueKind.String) return null;
            var text = v.GetString()!;
            if (text.Split('.').Length != 3 || !Version.TryParse(text, out var version)) return null;
            var kind = ParseKind(p.GetString()!);
            return kind is null ? null : new PackageInfo(version, kind.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the marker into <paramref name="dir"/> (used by tests and packaging).</summary>
    public static void Write(string dir, PackageInfo p)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["version"] = p.Version.ToString(3),
            ["package"] = KindText(p.Kind),
        });
        File.WriteAllText(Path.Combine(dir, FileName), json);
    }

    public static string KindText(PackageKind k) => k switch
    {
        PackageKind.WinZip => "win-zip",
        PackageKind.WinSetup => "win-setup",
        PackageKind.MacApp => "mac-app",
        PackageKind.LinuxTar => "linux-tar",
        PackageKind.LinuxAppImage => "linux-appimage",
        PackageKind.LinuxDeb => "linux-deb",
        PackageKind.LinuxRpm => "linux-rpm",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static PackageKind? ParseKind(string s) => s switch
    {
        "win-zip" => PackageKind.WinZip,
        "win-setup" => PackageKind.WinSetup,
        "mac-app" => PackageKind.MacApp,
        "linux-tar" => PackageKind.LinuxTar,
        "linux-appimage" => PackageKind.LinuxAppImage,
        "linux-deb" => PackageKind.LinuxDeb,
        "linux-rpm" => PackageKind.LinuxRpm,
        _ => null,
    };
}
