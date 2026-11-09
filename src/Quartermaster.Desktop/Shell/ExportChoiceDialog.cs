using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Shell;

/// <summary>The choice dialog's answer: the current view, or the whole map at the zoom of the combo's index (25 / 50 / 100 %).</summary>
public sealed record ExportChoice(bool WholeMap, int ZoomIndex)
{
    /// <summary>The zoom factor of a whole-map choice.</summary>
    public double Zoom => MapExportFlow.ZoomForChoice(ZoomIndex);
}

/// <summary>"Save map as picture" choice (WinForms ExportChoiceDialog): the map's name, then Current view or Whole map at 25 / 50 / 100 %. Closes with an <see cref="ExportChoice"/>, or null when cancelled.</summary>
sealed class ExportChoiceDialog : Window
{
    readonly RadioButton _view = new() { Content = "Current view", IsChecked = true, GroupName = "export" };
    readonly RadioButton _whole = new() { Content = "Whole map at", GroupName = "export" };
    readonly ComboBox _zoom = new() { Width = 70, IsEnabled = false, ItemsSource = new[] { "25%", "50%", "100%" }, SelectedIndex = 0 };

    public ExportChoiceDialog(string mapName)
    {
        Title = "Save map as picture";
        Width = 300;
        Height = 130;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _whole.IsCheckedChanged += (_, _) => _zoom.IsEnabled = _whole.IsChecked == true;
        var ok = new Button { Content = "OK", Width = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", Width = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Close(Choice);
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 7, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var wholeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        wholeRow.Children.Add(_whole);
        wholeRow.Children.Add(_zoom);
        var panel = new StackPanel { Margin = new Thickness(12, 10) };
        panel.Children.Add(new TextBlock { Text = mapName, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_view);
        panel.Children.Add(wholeRow);
        panel.Children.Add(buttons);
        Content = panel;
    }

    internal RadioButton ViewRadio => _view;
    internal RadioButton WholeRadio => _whole;
    internal ComboBox ZoomBox => _zoom;

    /// <summary>What OK answers now.</summary>
    internal ExportChoice Choice => new(_whole.IsChecked == true, Math.Max(0, _zoom.SelectedIndex));
}
