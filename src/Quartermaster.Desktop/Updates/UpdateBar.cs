using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Desktop.Launcher;

namespace Quartermaster.Desktop.Updates;

/// <summary>
/// The pale-yellow strip at the top of the launcher (WinForms UpdateBar): a line of text and a few link actions ("Quartermaster 0.2.0 is
/// available. What's new · Update now · Later · Skip this version"). Hidden until filled.
/// </summary>
public sealed class UpdateBar : Border
{
    static readonly IBrush Fill = new SolidColorBrush(Color.FromRgb(255, 244, 200)); // #FFF4C8, the AlertBar notice-chip yellow

    readonly TextBlock _text = new() { Margin = new Thickness(0, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
    readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal };
    readonly List<Button> _links = new();

    public UpdateBar()
    {
        Background = Fill;
        BorderThickness = new Thickness(0, 0, 0, 1);
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Classic.BorderBrush"));
        Padding = new Thickness(8, 2, 8, 3);
        IsVisible = false;
        _panel.Children.Add(_text);
        Child = _panel;
    }

    public string Message => _text.Text ?? "";

    /// <summary>The link captions now shown.</summary>
    public IReadOnlyList<string> Actions => _links.Select(Caption).ToList();

    /// <summary>Shows <paramref name="text"/> with the given link actions; an empty text hides the bar.</summary>
    public void Show(string text, params (string Caption, Action Click)[] actions)
    {
        foreach (var l in _links) _panel.Children.Remove(l);
        _links.Clear();
        _text.Text = text;
        foreach (var (caption, click) in actions)
        {
            var l = LauncherWindow.Link(caption);
            l.Margin = new Thickness(0, 4, 10, 4);
            l.Click += (_, _) => click();
            _links.Add(l);
            _panel.Children.Add(l);
        }
        IsVisible = text.Length > 0;
    }

    public void Clear() => Show("");

    public void SetActionEnabled(string caption, bool on)
    {
        foreach (var l in _links) if (Caption(l) == caption) l.IsEnabled = on;
    }

    /// <summary>The link with this caption (tests click it), or null.</summary>
    internal Button? ActionButton(string caption) => _links.FirstOrDefault(l => Caption(l) == caption);

    static string Caption(Button b) => (b.Content as TextBlock)?.Text ?? "";
}
