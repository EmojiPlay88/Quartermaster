using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Quartermaster.Desktop.Workbench;

/// <summary>"Make block" (WinForms PhasesTab.BlockDialog): a repeat count 1-99 (default 2) and a label.</summary>
sealed class BlockDialog : Window
{
    public NumericUpDown CountBox { get; }
    public TextBox LabelBox { get; }
    public Button OkButton { get; }
    public Button CancelButton { get; }

    public BlockDialog()
    {
        Title = "Make block";
        Width = 300;
        Height = 110;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CountBox = new NumericUpDown { Minimum = 1, Maximum = 99, Value = 2, Increment = 1, FormatString = "0", Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        LabelBox = new TextBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        OkButton = new Button { Content = "OK", Width = 75, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        CancelButton = new Button { Content = "Cancel", Width = 75, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        OkButton.Click += (_, _) => Close(Result());
        CancelButton.Click += (_, _) => Close(null);
        var grid = new Grid { Margin = new Thickness(12, 8), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnDefinitions = new ColumnDefinitions("88,*"), RowSpacing = 6 };
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
        Row(1, "Label:", LabelBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        buttons.Children.Add(OkButton);
        buttons.Children.Add(CancelButton);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
    }

    public (int Count, string Label) Result() => ((int)(CountBox.Value ?? 2), (LabelBox.Text ?? "").Trim());
}
