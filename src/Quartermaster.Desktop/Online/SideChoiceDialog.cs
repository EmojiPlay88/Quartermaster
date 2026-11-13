using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Online;

/// <summary>Small helpers shared by the online dialogs (classic grey look).</summary>
static class OnlineUi
{
    public static void Gray(TextBlock t) => t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Classic.GrayTextBrush"));

    public static void Face(Window w) => w.Bind(Window.BackgroundProperty, w.GetResourceObservable("Classic.FaceBrush"));

    public static Button Button(string text, bool isDefault = false, bool isCancel = false) =>
        new() { Content = text, MinWidth = 75, IsDefault = isDefault, IsCancel = isCancel, HorizontalContentAlignment = HorizontalAlignment.Center };

    public static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    public static TextBlock Label(string text, Thickness? margin = null) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(3) };

    /// <summary>Runs <paramref name="check"/> (null = fine, else the problem) and shows a problem in a message box; never throws.</summary>
    public static async void Accept(Window w, Func<string?> check, string what)
    {
        try
        {
            if (check() is { } problem) await MessageBox.ShowAsync(w, problem, w.Title ?? "", yesNo: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine(what + ": " + ex.Message); }
    }
}

/// <summary>A tick box per side: a guest choosing its side after joining, or the host assigning sides to a seat (WinForms SideChoiceDialog).
/// Closes with the ticked side ids in pack order; OK with nothing ticked says <see cref="SideChoiceModel.TickOneText"/> and stays open.</summary>
sealed class SideChoiceDialog : Window
{
    readonly List<(CheckBox Box, string Id)> _boxes = new();

    public SideChoiceDialog(string title, string prompt, IReadOnlyList<SideDef> sides, IReadOnlyCollection<string>? ticked = null, string? note = null)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 260;
        MaxWidth = 420;
        OnlineUi.Face(this);

        var ticks = SideChoiceModel.InitialTicks(sides, ticked);
        var panel = new StackPanel { Margin = new Thickness(12), Spacing = 2 };
        panel.Children.Add(OnlineUi.Label(prompt, new Thickness(3, 0, 3, 6)));
        foreach (var s in sides)
        {
            var b = new CheckBox { Content = SideChoiceModel.Label(s), IsChecked = ticks.Contains(s.Id) };
            _boxes.Add((b, s.Id));
            panel.Children.Add(b);
        }
        if (note != null)
        {
            var n = new TextBlock { Text = note, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(3, 8, 3, 3) };
            OnlineUi.Gray(n);
            panel.Children.Add(n);
        }
        Ok = OnlineUi.Button("OK", isDefault: true);
        var cancel = OnlineUi.Button("Cancel", isCancel: true);
        Ok.Click += (_, _) => OnlineUi.Accept(this, TryOk, "side choice");
        cancel.Click += (_, _) => Close(null);
        panel.Children.Add(OnlineUi.Buttons(Ok, cancel));
        Content = panel;
    }

    internal Button Ok { get; }

    internal IReadOnlyList<CheckBox> Boxes => _boxes.Select(b => b.Box).ToList();

    public IReadOnlyList<string> Chosen => _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList();

    /// <summary>Closes with the ticked sides, or returns why it can't (nothing ticked).</summary>
    internal string? TryOk()
    {
        if (Chosen.Count == 0) return SideChoiceModel.TickOneText;
        Close(Chosen);
        return null;
    }
}
