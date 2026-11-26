using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Quartermaster.Desktop.Workbench;

/// <summary>"Make block" (WinForms PhasesTab.BlockDialog): a repeat count 1-99 (default 2) or "Until a player stops" (count 0), and a label.</summary>
sealed class BlockDialog : Window
{
    public NumericUpDown CountBox { get; }
    public CheckBox UntilStoppedBox { get; }
    public TextBox LabelBox { get; }
    public Button OkButton { get; }
    public Button CancelButton { get; }

    public BlockDialog()
    {
        Title = "Make block";
        Width = 300;
        Height = 140;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CountBox = new NumericUpDown { Minimum = 1, Maximum = 99, Value = 2, Increment = 1, FormatString = "0", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        UntilStoppedBox = new CheckBox { Content = "Until a player stops" };
        ToolTip.SetTip(UntilStoppedBox, "Repeat these phases until the acting player chooses Continue at the last one.");
        UntilStoppedBox.IsCheckedChanged += (_, _) => CountBox.IsEnabled = UntilStoppedBox.IsChecked != true;
        LabelBox = new TextBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        OkButton = new Button { Content = "OK", Width = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        CancelButton = new Button { Content = "Cancel", Width = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        OkButton.Click += (_, _) => Close(Result());
        CancelButton.Click += (_, _) => Close(null);
        var grid = new Grid { Margin = new Thickness(12, 8), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("88,*"), RowSpacing = 6 };
        void Row(int r, string text, Control c)
        {
            var l = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, r);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            grid.Children.Add(l);
            grid.Children.Add(c);
        }
        Row(0, "Repeat count:", CountBox);
        Row(1, "", UntilStoppedBox);
        Row(2, "Label:", LabelBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        buttons.Children.Add(OkButton);
        buttons.Children.Add(CancelButton);
        Grid.SetRow(buttons, 3);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
    }

    public (int Count, string Label) Result() => (UntilStoppedBox.IsChecked == true ? 0 : (int)(CountBox.Value ?? 2), (LabelBox.Text ?? "").Trim());
}
