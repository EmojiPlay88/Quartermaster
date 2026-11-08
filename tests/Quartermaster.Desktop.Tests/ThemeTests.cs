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
        var chain = ClassicFonts.FamilyFor(OSPlatform.Linux);
        Size Measure(string family)
        {
            var text = new TextBlock { Text = "Quartermaster", FontFamily = new FontFamily(family), FontSize = ClassicFonts.SizePx };
            var window = new Window { Width = 200, Height = 60, Content = text };
            window.Show();
            text.Measure(new Size(200, 60));
            var size = text.DesiredSize;
            window.Close();
            return size;
        }
        // The first family does not exist anywhere: layout must skip it and land on the same face as the plain chain.
        var withFake = Measure("Qm Not A Real Font 7731, " + chain);
        var plain = Measure(chain);
        Assert.True(withFake.Width > 0 && withFake.Height > 0);
        Assert.Equal(plain, withFake);
    }

    [AvaloniaTheory]
    [InlineData("Face")] [InlineData("Window")] [InlineData("Text")] [InlineData("GrayText")] [InlineData("Highlight")]
    [InlineData("HighlightText")] [InlineData("Border")] [InlineData("ButtonFace")] [InlineData("ButtonBorder")] [InlineData("ButtonHover")]
    [InlineData("Checked")] [InlineData("Separator")] [InlineData("Page")] [InlineData("PopupBorder")] [InlineData("PopupFace")]
    [InlineData("ScrollThumb")] [InlineData("ScrollGlyph")] [InlineData("TreeBox")] [InlineData("TreeGlyph")] [InlineData("TreeLine")]
    [InlineData("CheckOn")] [InlineData("CheckBorder")]
    public void Brushes_match_their_colour_keys(string name)
    {
        var app = Application.Current!;
        Assert.True(app.TryFindResource("Classic." + name, app.ActualThemeVariant, out var c), name);
        Assert.True(app.TryFindResource("Classic." + name + "Brush", app.ActualThemeVariant, out var b), name + "Brush");
        Assert.Equal((Color)c!, Assert.IsAssignableFrom<ISolidColorBrush>(b).Color);
    }

    static string ShotDir() => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Src())!, "..", "..", "artifacts", "port"));

    static void Shoot(Control content, string name, double w, double h, Action<Window>? before = null)
    {
        var window = new MainWindow { Width = w, Height = h, Content = content };
        window.Show();
        before?.Invoke(window);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        TestHelpers.CaptureToPng(window, Path.Combine(ShotDir(), name));
        window.Close();
        Assert.True(File.Exists(Path.Combine(ShotDir(), name)));
    }

    [AvaloniaFact]
    public void Theme_sample_screenshot()
    {
        var menu = new Menu { ItemsSource = new[] { new MenuItem { Header = "File" }, new MenuItem { Header = "Edit" }, new MenuItem { Header = "Help" } } };
        var panel = new StackPanel { Margin = new Thickness(8), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "Autosave every (minutes, 0 = off):" });
        panel.Children.Add(new TextBox { Text = "5", Width = 120, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        panel.Children.Add(new CheckBox { Content = "Ask before deleting pieces", IsChecked = true });
        panel.Children.Add(new CheckBox { Content = "Game master mode" });
        panel.Children.Add(new Button { Content = "Restore defaults", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        panel.Children.Add(new Button { Content = "Disabled", IsEnabled = false, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        panel.Children.Add(new ComboBox { ItemsSource = new[] { "Map", "Side" }, SelectedIndex = 0, Width = 120 });
        panel.Children.Add(new ListBox { ItemsSource = new[] { "NL 1 Rifles", "NL 2 Rifles", "NL 3 Rifles" }, SelectedIndex = 1, Height = 70 });
        var root = new DockPanel();
        DockPanel.SetDock(menu, Avalonia.Controls.Dock.Top);
        root.Children.Add(menu);
        var status = new Border { Child = new TextBlock { Text = "Riverland 1.0    Roll: 1d6 = 4" }, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(4, 2) };
        status.Classes.Add("status");
        DockPanel.SetDock(status, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(panel);
        Shoot(root, "theme-sample.png", 420, 400);
    }

    [AvaloniaFact]
    public void Theme_tabs_screenshot()
    {
        var tabs = new TabControl { Margin = new Thickness(10), ItemsSource = new[] {
            new TabItem { Header = "General", Content = new TextBlock { Text = "page content", Margin = new Thickness(8) } },
            new TabItem { Header = "Game" }, new TabItem { Header = "Display" }, new TabItem { Header = "Map" } } };
        Shoot(tabs, "theme-tabs.png", 420, 140);
    }

    [AvaloniaFact]
    public void Theme_open_menu_screenshot()
    {
        var undo = new MenuItem { Header = "Undo Next Phase", InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.Z, Avalonia.Input.KeyModifiers.Control) };
        var redo = new MenuItem { Header = "Redo", IsEnabled = false, InputGesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.Y, Avalonia.Input.KeyModifiers.Control) };
        var back = new MenuItem { Header = "Step Back to Start of Phase" };
        var edit = new MenuItem { Header = "Edit", ItemsSource = new object[] { undo, redo, new Separator(), back, new MenuItem { Header = "Step Back To..." } } };
        var menu = new Menu { ItemsSource = new[] { new MenuItem { Header = "File" }, edit, new MenuItem { Header = "Help" } } };
        Shoot(menu, "theme-menu-open.png", 320, 160, _ => { edit.Open(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); undo.Focus(); });
    }

    [AvaloniaFact]
    public void Theme_tree_scroll_split_screenshot()
    {
        var root = new TreeViewItem { Header = "Riverland", IsExpanded = true };
        var maps = new TreeViewItem { Header = "Maps", IsExpanded = true, ItemsSource = new[] { "Riverland Map", "Overview" } };
        root.ItemsSource = new object[] { maps, new TreeViewItem { Header = "Charts" }, new TreeViewItem { Header = "Rulebooks", ItemsSource = new[] { "Rules" } }, "Setups" };
        var tree = new TreeView { ItemsSource = new object[] { root }, SelectedItem = maps.Items[1] };
        var lines = Enumerable.Range(1, 40).Select(i => "Row " + i).ToArray();
        var list = new ListBox { ItemsSource = lines, Height = 120 };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,*"), Margin = new Thickness(8) };
        grid.Children.Add(tree);
        var split = new GridSplitter { [Grid.ColumnProperty] = 1 };
        grid.Children.Add(split);
        list[Grid.ColumnProperty] = 2;
        grid.Children.Add(list);
        Shoot(grid, "theme-tree-scroll-splitter.png", 420, 150);
    }

    static string Src([System.Runtime.CompilerServices.CallerFilePath] string p = "") => p;
}
