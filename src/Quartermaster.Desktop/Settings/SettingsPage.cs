using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

/// <summary>One page of the settings window: builds its controls, shows a <see cref="Settings"/> and writes its fields back into one.</summary>
public abstract class SettingsPage : UserControl
{
    public abstract string Title { get; }

    /// <summary>The page's key for opening Settings on it (the lower-case title unless a page names its own).</summary>
    public virtual string Key => Title.ToLowerInvariant();

    /// <summary>A yes/no question to ask before the window closes (work still running), or null to close without asking.</summary>
    public virtual string? CloseQuestion => null;

    /// <summary>Stops the page's running work: the close question was answered Yes, or the window closes without asking.</summary>
    public virtual void StopForClose() { }

    /// <summary>Shows the values of <paramref name="s"/> in the page's controls.</summary>
    public abstract void Load(Settings s);

    /// <summary>Writes the page's controls into <paramref name="s"/>.</summary>
    public abstract Settings Collect(Settings s);

    /// <summary>A reason the page cannot be applied as it stands, or null.</summary>
    public virtual string? Validate() => null;

    /// <summary>Called after the settings were applied.</summary>
    public virtual void AfterApply() { }

    /// <summary>A two-column table: label column auto, control column fill, 10 px padding.</summary>
    protected static Grid Table()
    {
        var g = new Grid { Margin = new Thickness(10) };
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        return g;
    }

    /// <summary>Wraps a table in a scroll viewer (the page's content).</summary>
    protected static ScrollViewer Host(Control table) => new() { Content = table, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    static int NewRow(Grid g)
    {
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        return g.RowDefinitions.Count - 1;
    }

    protected static void Row(Grid g, string label, Control c)
    {
        var row = NewRow(g);
        var l = new TextBlock { Text = label, Margin = new Thickness(3, 6, 12, 6), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(l, row);
        c.Margin = new Thickness(3);
        c.HorizontalAlignment = HorizontalAlignment.Left;
        c.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(c, row);
        Grid.SetColumn(c, 1);
        g.Children.Add(l);
        g.Children.Add(c);
    }

    protected static void Full(Grid g, Control c)
    {
        var row = NewRow(g);
        c.Margin = new Thickness(3, 4);
        Grid.SetRow(c, row);
        Grid.SetColumnSpan(c, 2);
        g.Children.Add(c);
    }

    protected static CheckBox Check(string text) => new() { Content = text };

    protected static NumericUpDown Num(int min, int max) =>
        new() { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 110, Value = min };

    protected static void Set(NumericUpDown n, decimal v) => n.Value = Math.Clamp(v, n.Minimum, n.Maximum);

    protected static int Int(NumericUpDown n) => (int)(n.Value ?? n.Minimum);
}
