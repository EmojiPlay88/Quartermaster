using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

public sealed class DisplayPage : SettingsPage
{
    readonly SettingsContext _ctx;
    internal readonly ComboBox GridBox = new() { Width = 140, ItemsSource = new[] { "Module default", "Always show", "Never show" } };
    internal readonly NumericUpDown Label = Num(6, 20);
    internal readonly Border Swatch = new() { Width = 40, Height = 20, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    internal readonly Button Choose = new() { Content = "Choose..." };
    internal readonly NumericUpDown TabMin = Num(40, 400), TabMax = Num(60, 600);
    internal readonly CheckBox AlertBar = Check("Show alert bar");
    string _color = "#FFD700";

    public DisplayPage(SettingsContext ctx)
    {
        _ctx = ctx;
        Choose.Click += async (_, _) => await ChooseColourAsync();
        var colour = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        colour.Children.Add(Swatch);
        colour.Children.Add(Choose);
        var t = Table();
        Row(t, "Grid:", GridBox);
        Row(t, "Piece label size (8 = as the module says):", Label);
        Row(t, "Selection highlight colour:", colour);
        Row(t, "Document tab minimum width:", TabMin);
        Row(t, "Document tab maximum width:", TabMax);
        Full(t, AlertBar);
        Content = Host(t);
    }

    public override string Title => "Display";

    /// <summary>The highlight colour as "#RRGGBB".</summary>
    internal string Colour => _color;

    internal async Task ChooseColourAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            if (await _ctx.Dialogs.AskColourAsync(owner, _color) is { } hex) SetColour(hex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("settings display: " + ex);
        }
    }

    void SetColour(string hex)
    {
        _color = hex;
        if (SettingsStore.TryParseColor(hex, out var c)) Swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
    }

    public override void Load(Settings s)
    {
        GridBox.SelectedIndex = (int)s.GridDefault;
        Set(Label, s.LabelFontSize);
        SetColour(s.HighlightColor);
        Set(TabMin, s.TabMinWidth);
        Set(TabMax, s.TabMaxWidth);
        AlertBar.IsChecked = s.AlertBarVisible;
    }

    public override Settings Collect(Settings s)
    {
        var min = Int(TabMin);
        return s with
        {
            GridDefault = (GridMode)Math.Max(0, GridBox.SelectedIndex), LabelFontSize = Int(Label), HighlightColor = _color,
            TabMinWidth = min, TabMaxWidth = Math.Max(min, Int(TabMax)), AlertBarVisible = AlertBar.IsChecked == true,
        };
    }
}
