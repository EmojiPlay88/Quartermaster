using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The main toolbar under the menu bar (WinForms MainForm._toolbar): flat text buttons keyed by their <c>ToolbarIds</c> id, separators
/// and drop-down buttons. Buttons never take the keyboard focus, so shortcuts stay with the map. Each button's own tooltip is its text,
/// as a WinForms ToolStripButton (AutoToolTip); the main window may replace it with a reason or a shortcut hint.
/// </summary>
public sealed class MainToolbar : Border
{
    /// <summary>One button: its id, its text and the control.</summary>
    public sealed record Entry(string Id, string Text, Button Button);

    readonly StackPanel _bar = new() { Orientation = Orientation.Horizontal };
    readonly List<Entry> _entries = new();
    readonly List<Control> _separators = new();
    readonly Dictionary<string, Func<IReadOnlyList<Control>>> _dropDowns = new(StringComparer.Ordinal);

    public MainToolbar()
    {
        Child = _bar;
        Padding = new Thickness(2, 1);
        BorderThickness = new Thickness(0, 0, 0, 1);
        Classes.Add("mainToolbar");
        this.Bind(BackgroundProperty, this.GetResourceObservable("Classic.FaceBrush"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("Classic.BorderBrush"));
    }

    /// <summary>The buttons in display order.</summary>
    public IReadOnlyList<Entry> Buttons => _entries;

    /// <summary>The button with this id, or null.</summary>
    public Button? Find(string id) => _entries.FirstOrDefault(e => e.Id == id)?.Button;

    /// <summary>Adds a button that runs <paramref name="click"/>.</summary>
    public Button Add(string id, string text, Action click)
    {
        var b = NewButton(id, text);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Adds a drop-down button whose menu is filled by <paramref name="items"/> each time it opens.</summary>
    public Button AddDropDown(string id, string text, Func<IReadOnlyList<Control>> items)
    {
        var b = NewButton(id, text);
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => Fill(menu, items);
        b.Flyout = menu;
        _dropDowns[id] = items;
        return b;
    }

    /// <summary>The items the drop-down <paramref name="id"/> would show now (tests).</summary>
    internal IReadOnlyList<MenuItem> DropDownItems(string id) => _dropDowns[id]().OfType<MenuItem>().ToList();

    /// <summary>A thin etched line between groups, as a ToolStripSeparator.</summary>
    public void AddSeparator()
    {
        var line = new Border { Width = 1, Margin = new Thickness(4, 3), VerticalAlignment = VerticalAlignment.Stretch };
        line.Bind(BackgroundProperty, line.GetResourceObservable("Classic.SeparatorBrush"));
        _bar.Children.Add(line);
        _separators.Add(line);
    }

    /// <summary>The separators in display order (tests).</summary>
    internal IReadOnlyList<Control> Separators => _separators;

    /// <summary>Shows or hides a button (Settings, Toolbar). A separator shows only between two visible buttons, so hiding a whole group
    /// leaves no stray or doubled line.</summary>
    public void SetVisible(string id, bool visible)
    {
        if (Find(id) is not { } b) return;
        b.IsVisible = visible;
        Control? pending = null;
        var before = false;
        foreach (var c in _bar.Children)
        {
            if (_separators.Contains(c))
            {
                c.IsVisible = false;
                if (before) pending = c;
                continue;
            }
            if (!c.IsVisible) continue;
            if (pending != null) { pending.IsVisible = true; pending = null; }
            before = true;
        }
    }

    /// <summary>The tooltip a button shows when nothing else is said: its text (WinForms AutoToolTip).</summary>
    public string? BaseTip(Control c) => _entries.FirstOrDefault(e => ReferenceEquals(e.Button, c))?.Text;

    Button NewButton(string id, string text)
    {
        var b = new Button { Content = text, Focusable = false, Name = id };
        ToolTip.SetTip(b, text);
        ToolTip.SetShowOnDisabled(b, true);
        _bar.Children.Add(b);
        _entries.Add(new Entry(id, text, b));
        return b;
    }

    static void Fill(MenuFlyout menu, Func<IReadOnlyList<Control>> items)
    {
        menu.Items.Clear();
        foreach (var c in items()) menu.Items.Add(c);
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = DiceTools.NoGameText, IsEnabled = false });
    }
}
