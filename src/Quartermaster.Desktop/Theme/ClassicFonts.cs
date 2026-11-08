using System.Runtime.InteropServices;

namespace Quartermaster.Desktop.Theme;

/// <summary>The WinForms look uses Tahoma 8.25pt (= 11px at 96 DPI); other OSes fall back to look-alike faces.</summary>
public static class ClassicFonts
{
    public const double SizePx = 11.0;

    public static string FamilyFor(OSPlatform os) =>
        os == OSPlatform.Windows ? "Tahoma"
        : os == OSPlatform.OSX ? "Tahoma, Verdana, sans-serif"
        : "Tahoma, Verdana, DejaVu Sans, sans-serif";

    public static string Current =>
        FamilyFor(OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux);
}
