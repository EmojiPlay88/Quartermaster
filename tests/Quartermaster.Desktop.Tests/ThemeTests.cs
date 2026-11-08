using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Quartermaster.Desktop.Theme;

namespace Quartermaster.Desktop.Tests;

public class ThemeTests
{
    [Fact]
    public void Font_family_per_os()
    {
        Assert.Equal("Tahoma", ClassicFonts.FamilyFor(OSPlatform.Windows));
        Assert.Equal("Tahoma, Verdana, sans-serif", ClassicFonts.FamilyFor(OSPlatform.OSX));
        Assert.Equal("Tahoma, Verdana, DejaVu Sans, sans-serif", ClassicFonts.FamilyFor(OSPlatform.Linux));
        Assert.Equal(11.0, ClassicFonts.SizePx);
    }

    [AvaloniaTheory]
    [InlineData("Classic.Face", "#F0F0F0")]
    [InlineData("Classic.Window", "#FFFFFF")]
    [InlineData("Classic.Text", "#000000")]
    [InlineData("Classic.GrayText", "#6D6D6D")]
    [InlineData("Classic.Highlight", "#0078D4")]
    [InlineData("Classic.HighlightText", "#FFFFFF")]
    [InlineData("Classic.Border", "#ABADB3")]
    public void Theme_resources_have_sampled_colours(string key, string hex)
    {
        Assert.True(Application.Current!.TryFindResource(key, Application.Current!.ActualThemeVariant, out var v), key);
        Assert.Equal(Color.Parse(hex), Assert.IsType<Color>(v));
    }

    [AvaloniaFact]
    public void Missing_fonts_fall_back()
    {
        var text = new TextBlock { Text = "Quartermaster", FontFamily = new FontFamily(ClassicFonts.FamilyFor(OSPlatform.Linux)), FontSize = ClassicFonts.SizePx };
        var window = new Window { Width = 200, Height = 60, Content = text };
        window.Show();
        text.Measure(new Size(200, 60));
        Assert.True(text.DesiredSize.Width > 0 && text.DesiredSize.Height > 0);
        window.Close();
    }

    [AvaloniaFact]
    public void Theme_sample_screenshot()
    {
        var menu = new Menu { ItemsSource = new[] { new MenuItem { Header = "File", ItemsSource = new[] { new MenuItem { Header = "Open Module..." } } }, new MenuItem { Header = "Edit" }, new MenuItem { Header = "Help" } } };
        var panel = new StackPanel { Margin = new Thickness(8), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "Autosave every (minutes, 0 = off):" });
        panel.Children.Add(new TextBox { Text = "5", Width = 120, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        panel.Children.Add(new CheckBox { Content = "Ask before deleting pieces", IsChecked = true });
        panel.Children.Add(new CheckBox { Content = "Game master mode" });
        panel.Children.Add(new Button { Content = "Restore defaults", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        panel.Children.Add(new Button { Content = "Disabled", IsEnabled = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        panel.Children.Add(new ComboBox { ItemsSource = new[] { "Map", "Side" }, SelectedIndex = 0, Width = 120 });
        var list = new ListBox { ItemsSource = new[] { "NL 1 Rifles", "NL 2 Rifles", "NL 3 Rifles" }, SelectedIndex = 1, Height = 70 };
        panel.Children.Add(list);
        var tabs = new TabControl { Height = 60, ItemsSource = new[] { new TabItem { Header = "General", Content = "x" }, new TabItem { Header = "Game" } } };
        panel.Children.Add(tabs);
        var root = new DockPanel();
        DockPanel.SetDock(menu, Avalonia.Controls.Dock.Top);
        root.Children.Add(menu);
        var status = new Border { Child = new TextBlock { Text = "Riverland 1.0    Roll: 1d6 = 4" }, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(4, 2) };
        status.Classes.Add("status");
        DockPanel.SetDock(status, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(panel);
        var window = new MainWindow { Width = 420, Height = 460, Content = root };
        window.Show();
        var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Src())!, "..", "..", "artifacts", "port"));
        TestHelpers.CaptureToPng(window, Path.Combine(dir, "theme-sample.png"));
        window.Close();
        Assert.True(File.Exists(Path.Combine(dir, "theme-sample.png")));
    }

    static string Src([System.Runtime.CompilerServices.CallerFilePath] string p = "") => p;
}
