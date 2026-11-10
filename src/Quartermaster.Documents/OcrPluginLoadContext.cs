using System.Reflection;
using System.Runtime.Loader;

namespace Quartermaster.Documents;

/// <summary>
/// Load context of the Windows OCR plugin (ocr-windows/Quartermaster.OcrWindows.dll). The plugin's own files (its dll, the
/// Windows SDK projection, WinRT.Runtime) load here; Quartermaster.Render, SkiaSharp and anything the app has already loaded come
/// from the default context, so <c>IOcrRecognizer</c> and <c>SKBitmap</c> keep one identity across the boundary.
/// </summary>
internal sealed class OcrPluginLoadContext(string pluginDll) : AssemblyLoadContext("Quartermaster.OcrWindows")
{
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase) { "Quartermaster.Render", "SkiaSharp" };

    private readonly AssemblyDependencyResolver _resolver = new(pluginDll);

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is null || Shared.Contains(name.Name)) return null;
        if (Default.Assemblies.Any(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))) return null;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null && File.Exists(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
