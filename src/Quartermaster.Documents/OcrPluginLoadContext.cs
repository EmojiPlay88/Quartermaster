using System.Reflection;
using System.Runtime.Loader;

namespace Quartermaster.Documents;

/// <summary>
/// Load context of the Windows OCR plugin (ocr-windows/Quartermaster.OcrWindows.dll). Exactly the plugin's private assemblies
/// (<see cref="PluginPrivate"/>: the plugin itself, the Windows SDK projection, WinRT.Runtime) load here from the plugin folder,
/// whatever the app has loaded before. Everything else, Quartermaster.Render and SkiaSharp included, comes from the default
/// context, so <c>IOcrRecognizer</c> and <c>SKBitmap</c> keep one identity across the boundary.
/// </summary>
internal sealed class OcrPluginLoadContext(string pluginDll) : AssemblyLoadContext("Quartermaster.OcrWindows")
{
    internal static readonly IReadOnlySet<string> PluginPrivate =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Quartermaster.OcrWindows", "WinRT.Runtime", "Microsoft.Windows.SDK.NET" };

    private readonly AssemblyDependencyResolver _resolver = new(pluginDll);

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is null || !PluginPrivate.Contains(name.Name)) return null; // the default context's copy
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null && File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
