using System.Reflection;

namespace Quartermaster.Render;

/// <summary>
/// The app version from <c>Directory.Build.props</c> (<c>&lt;Version&gt;</c>), read from the assembly's informational version with any
/// "+commit" / "-prerelease" suffix dropped. Every project shares the props, so this assembly's version is the app's.
/// </summary>
public static class AppVersion
{
    static readonly Lazy<Version> Value = new(Read);

    /// <summary>Major.Minor.Patch (three parts).</summary>
    public static Version Current => Value.Value;

    /// <summary>"0.1.0".</summary>
    public static string Text => Format(Current);

    /// <summary>A version as text with at most three parts ("0.2.0").</summary>
    public static string Format(Version v) => v.Build >= 0 ? v.ToString(3) : v.ToString();

    static Version Read()
    {
        var asm = typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info != null)
        {
            var cut = info.IndexOfAny(['+', '-']);
            if (cut >= 0) info = info[..cut];
            if (Version.TryParse(info, out var v)) return Three(v);
        }
        return Three(asm.GetName().Version ?? new Version(0, 0, 0));
    }

    static Version Three(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
