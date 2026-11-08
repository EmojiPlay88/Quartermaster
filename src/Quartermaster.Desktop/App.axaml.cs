using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Quartermaster.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Per-OS Tahoma fallback chain (the XAML default is plain Tahoma).
        Resources["Classic.FontFamily"] = new Avalonia.Media.FontFamily(Theme.ClassicFonts.Current);
        Resources["Classic.FontSize"] = Theme.ClassicFonts.SizePx;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
