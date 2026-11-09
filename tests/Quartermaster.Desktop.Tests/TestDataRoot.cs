using System.Runtime.CompilerServices;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Tests;

/// <summary>Points the app data root (layout memory, phase-boundary autosaves) at a per-run temp folder, so windows built without a layout
/// root never write into the real profile.</summary>
static class TestDataRoot
{
    public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qm-desktop-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataRoot.EnvVar)))
            Environment.SetEnvironmentVariable(AppDataRoot.EnvVar, Path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }
}
