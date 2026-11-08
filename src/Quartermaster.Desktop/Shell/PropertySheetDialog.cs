using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// A piece property sheet as WinForms PropertySheetDialog: one row per sheet field (name, a 250-wide text box, multi-line when the value has
/// line breaks, and "(managed by ledger: X)" with a read-only box for a row a rules-pack property owns), OK / Cancel at the bottom right.
/// OK closes with the values in row order (a managed row's text unchanged, so writing back keeps it); Cancel closes with null.
/// </summary>
sealed class PropertySheetDialog : Window
{
    readonly List<TextBox> _boxes = new();

    public PropertySheetDialog(string title, IReadOnlyList<(string Name, string Value)> rows, Func<string, string?>? managedValue = null)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { Margin = new Thickness(8), ColumnDefinitions = new ColumnDefinitions("Auto,260,Auto") };
        for (var i = 0; i < rows.Count; i++)
        {
            var (name, value) = rows[i];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var multi = value.Contains('\n');
            var box = new TextBox
            {
                Text = value,
                Width = 250,
                HorizontalAlignment = HorizontalAlignment.Left,
                AcceptsReturn = multi,
                TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Height = multi ? 60 : double.NaN,
                Margin = new Thickness(0, 2),
            };
            var managed = managedValue?.Invoke(name);
            if (managed != null) box.IsReadOnly = true;   // the ledger owns this value; the text stays as it was so OK writes it back unchanged
            _boxes.Add(box);
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 8, 0) };
            var note = new TextBlock
            {
                Text = managed == null ? "" : $"(managed by ledger: {managed})",
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 6, 0, 0),
            };
            Grid.SetRow(label, i); Grid.SetColumn(label, 0);
            Grid.SetRow(box, i); Grid.SetColumn(box, 1);
            Grid.SetRow(note, i); Grid.SetColumn(note, 2);
            grid.Children.Add(label);
            grid.Children.Add(box);
            grid.Children.Add(note);
        }

        var ok = new Button { Content = "OK", Width = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Cancel", Width = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Close(Values);
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(8, 4, 8, 8) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(grid);
        Content = panel;
        // WinForms focuses the first control in tab order: the first row's text box.
        Opened += (_, _) => { if (_boxes.Count > 0) _boxes[0].Focus(); else ok.Focus(); };
    }

    /// <summary>The boxes' texts in row order.</summary>
    public IReadOnlyList<string> Values => _boxes.Select(b => b.Text ?? "").ToList();
}
