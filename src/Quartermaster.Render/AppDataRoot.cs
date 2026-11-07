namespace Quartermaster.Render;

/// <summary>
/// The one folder Quartermaster keeps its own files in: %LOCALAPPDATA%\Quartermaster, or the folder named by
/// <see cref="EnvVar"/> (tests and smokes). Every per-user path (log, cache, layout, settings, rules index, autosaves, rejoin tokens,
/// WebView2 data) is under it, so with the variable set nothing touches the real profile.
/// </summary>
public static class AppDataRoot
{
    public const string EnvVar = "QUARTERMASTER_LAYOUT_ROOT";

    /// <summary>The root for this override (null/blank = the real profile folder).</summary>
    public static string Resolve(string? overrideRoot) =>
        !string.IsNullOrWhiteSpace(overrideRoot) ? overrideRoot
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster");

    /// <summary>The root now (reads <see cref="EnvVar"/> on every call).</summary>
    public static string Path => Resolve(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>A sub-folder of <see cref="Path"/>.</summary>
    public static string Sub(string name) => System.IO.Path.Combine(Path, name);
}
