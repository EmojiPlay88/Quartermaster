using Avalonia;
using Avalonia.Headless;
using Quartermaster.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Quartermaster.Desktop.Tests;

/// <summary>
/// Headless test host: the real <see cref="App"/> on Avalonia's headless platform with Skia drawing (not headless drawing),
/// so tests can capture rendered frames. No window ever reaches the screen.
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
