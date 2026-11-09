using System.Reflection;
using System.Runtime.InteropServices;

namespace Quartermaster.Documents.Tesseract;

/// <summary>
/// Thin P/Invoke layer over Tesseract's stable C API (capi.h) in the native libtesseract shipped by the
/// Tesseract.Native.runtime.* packages. The real library file names differ per OS, so a resolver probes them.
/// </summary>
internal static unsafe partial class TessApi
{
    private const string Lib = "tesseract";

    internal const int RilWord = 3;
    internal const int PsmSingleBlock = 6;
    internal const int PsmSingleLine = 7;

    static TessApi() => NativeLibrary.SetDllImportResolver(typeof(TessApi).Assembly, Resolve);

    private static string Rid =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    private static readonly string[] LeptonicaNames =
        OperatingSystem.IsWindows() ? ["leptonica-1.87.0.dll", "leptonica.dll"]
        : OperatingSystem.IsMacOS() ? ["libleptonica.6.dylib", "libleptonica.dylib"]
        : ["libleptonica.so.6", "libleptonica.so"];

    private static readonly string[] TesseractNames =
        OperatingSystem.IsWindows() ? ["tesseract55.dll", "tesseract.dll"]
        : OperatingSystem.IsMacOS() ? ["libtesseract.5.5.dylib", "libtesseract.dylib"]
        : ["libtesseract.so.5.5", "libtesseract.so"];

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        string[] dirs = [AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", Rid, "native")];
        // Leptonica first, by full path, so libtesseract's dependency is already resolved wherever the loader looks.
        TryLoadFrom(dirs, LeptonicaNames);
        var h = TryLoadFrom(dirs, TesseractNames);
        if (h != IntPtr.Zero) return h;
        return NativeLibrary.TryLoad(Lib, asm, path, out var h2) ? h2 : IntPtr.Zero;
    }

    private static IntPtr TryLoadFrom(string[] dirs, string[] names)
    {
        foreach (var d in dirs)
            foreach (var n in names)
            {
                var full = Path.Combine(d, n);
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out var h)) return h;
            }
        return IntPtr.Zero;
    }

    [LibraryImport(Lib)] internal static partial IntPtr TessBaseAPICreate();
    [LibraryImport(Lib)] internal static partial void TessBaseAPIDelete(IntPtr api);
    [LibraryImport(Lib)] internal static partial void TessBaseAPIEnd(IntPtr api);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int TessBaseAPIInit3(IntPtr api, string datapath, string language);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int TessBaseAPISetVariable(IntPtr api, string name, string value);
    [LibraryImport(Lib)] internal static partial void TessBaseAPISetPageSegMode(IntPtr api, int mode);
    [LibraryImport(Lib)] internal static partial void TessBaseAPISetImage(IntPtr api, byte* data, int width, int height, int bytesPerPixel, int bytesPerLine);
    [LibraryImport(Lib)] internal static partial void TessBaseAPIClear(IntPtr api);
    [LibraryImport(Lib)] internal static partial int TessBaseAPIRecognize(IntPtr api, IntPtr monitor);
    [LibraryImport(Lib)] internal static partial IntPtr TessBaseAPIGetIterator(IntPtr api);
    [LibraryImport(Lib)] internal static partial IntPtr TessResultIteratorGetPageIterator(IntPtr it);
    [LibraryImport(Lib)] internal static partial int TessResultIteratorNext(IntPtr it, int level);
    [LibraryImport(Lib)] internal static partial IntPtr TessResultIteratorGetUTF8Text(IntPtr it, int level);
    [LibraryImport(Lib)] internal static partial void TessResultIteratorDelete(IntPtr it);
    [LibraryImport(Lib)] internal static partial int TessPageIteratorBoundingBox(IntPtr pageIt, int level, out int left, out int top, out int right, out int bottom);
    [LibraryImport(Lib)] internal static partial void TessDeleteText(IntPtr text);
}

/// <summary>Owns a TessBaseAPI*; releasing ends and deletes it.</summary>
internal sealed class TessApiHandle : SafeHandle
{
    public TessApiHandle() : base(IntPtr.Zero, true) { }
    public override bool IsInvalid => handle == IntPtr.Zero;
    internal static TessApiHandle Create()
    {
        var h = new TessApiHandle();
        h.SetHandle(TessApi.TessBaseAPICreate());
        return h;
    }
    protected override bool ReleaseHandle()
    {
        TessApi.TessBaseAPIEnd(handle);
        TessApi.TessBaseAPIDelete(handle);
        return true;
    }
}
