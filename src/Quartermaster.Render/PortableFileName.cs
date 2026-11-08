namespace Quartermaster.Render;

/// <summary>
/// File-name rules that are the same on every OS. <see cref="Path.GetInvalidFileNameChars"/> depends on the OS it runs on (on Linux and
/// macOS only '/' and NUL), but turn files and update assets move between Windows, Linux and macOS, so names are held to Windows' rules
/// everywhere: control characters and <c>" &lt; &gt; | : * ? \ /</c> are never part of a file name.
/// </summary>
public static class PortableFileName
{
    private static readonly char[] Invalid = Enumerable.Range(0, 32).Select(i => (char)i).Concat("\"<>|:*?\\/").ToArray();

    /// <summary>The characters no file name may hold on any supported OS (Windows' set, which includes '/' and '\').</summary>
    public static IReadOnlyList<char> InvalidChars => Invalid;

    public static bool IsInvalidChar(char c) => c < 32 || Array.IndexOf(Invalid, c) >= 0;

    /// <summary>True when <paramref name="name"/> holds a character no file name may hold on some OS (including either path separator).</summary>
    public static bool HasInvalidChars(string name) => name.IndexOfAny(Invalid) >= 0;
}
