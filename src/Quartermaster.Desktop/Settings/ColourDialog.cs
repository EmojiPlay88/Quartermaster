using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

/// <summary>
/// A small colour chooser: the 48 basic colours of the Windows colour dialog (8 columns by 6 rows) and a #RRGGBB box. OK needs a valid
/// hex and closes with it upper-cased; Cancel closes with null.
/// </summary>
sealed class ColourDialog : Window
{
    /// <summary>The Windows basic-colour palette, row by row.</summary>
    static readonly string[] Basic =
    {
        "FF8080", "FFFF80", "80FF80", "00FF80", "80FFFF", "0080FF", "FF80C0", "FF80FF",
        "FF0000", "FFFF00", "80FF00", "00FF40", "00FFFF", "0080C0", "8080C0", "FF00FF",
        "804040", "FF8040", "00FF00", "008080", "004080", "8080FF", "800040", "FF0080",
        "800000", "FF8000", "008000", "008040", "0000FF", "0000A0", "800080", "8000FF",
        "400000", "804000", "004000", "004040", "000080", "000040", "400040", "400080",
        "000000", "808000", "808040", "808080", "408080", "C0C0C0", "400040", "FFFFFF",
    };

    public List<Button> Swatches { get; } = new();
    public TextBox HexBox { get; }
    public Button OkButton { get; }
    public Button CancelButton { get; }
    readonly Border _preview = new() { Width = 24, Height = 20, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };

    public ColourDialog(string initialHex)
    {
        Title = "Colour";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new UniformGrid { Columns = 8, Rows = 6 };
        foreach (var hex in Basic)
        {
            var b = new Button
            {
                Width = 26, Height = 22, Margin = new Thickness(1), Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse("#" + hex)), Tag = "#" + hex,
            };
            b.Click += (_, _) => HexBox!.Text = (string)b.Tag!;
            Swatches.Add(b);
            grid.Children.Add(b);
        }

        HexBox = new TextBox { Text = initialHex, Width = 100, MaxLength = 7 };
        OkButton = new Button { Content = "OK", MinWidth = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        CancelButton = new Button { Content = "Cancel", MinWidth = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        OkButton.Click += (_, _) => { if (Valid(out var hex)) Close(hex); };
        CancelButton.Click += (_, _) => Close(null);
        HexBox.OnTextSet(Refresh);
        Refresh();

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        hexRow.Children.Add(new TextBlock { Text = "Hex:", VerticalAlignment = VerticalAlignment.Center });
        hexRow.Children.Add(HexBox);
        hexRow.Children.Add(_preview);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(OkButton);
        buttons.Children.Add(CancelButton);
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(grid);
        panel.Children.Add(hexRow);
        panel.Children.Add(buttons);
        Content = panel;
        Opened += (_, _) => HexBox.Focus();
    }

    bool Valid(out string hex)
    {
        var text = HexBox.Text?.Trim();
        if (SettingsStore.TryParseColor(text, out _)) { hex = text!.ToUpperInvariant(); return true; }
        hex = "";
        return false;
    }

    void Refresh()
    {
        var ok = Valid(out var hex);
        OkButton.IsEnabled = ok;
        _preview.Background = ok ? new SolidColorBrush(Color.Parse(hex)) : Brushes.Transparent;
    }
}
