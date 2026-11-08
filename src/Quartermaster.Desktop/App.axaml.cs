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
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            // Command line [<vmod>] [--setup <index>|--new]; applied once the window is up, so its dialogs have an owner.
            var args = Shell.StartupArgs.Parse(desktop.Args ?? Array.Empty<string>(), m => System.Diagnostics.Trace.WriteLine(m));
            if (args.ModulePath != null)
            {
                void Once(object? sender, EventArgs e)
                {
                    window.Opened -= Once;
                    window.ApplyStartup(args);
                }
                window.Opened += Once;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
