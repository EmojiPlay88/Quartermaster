using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Render.Search;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Workbench;

/// <summary>An editor whose items carry a rule ref (Formulas, Tables, Phases): the workbench links it to the rulebook search.</summary>
public interface IRuleRefEditor
{
    /// <summary>An item is selected and editable, so "Use as rule ref" can fill its rule ref.</summary>
    bool CanUseRuleRef { get; }

    /// <summary>Fills the selected item's rule ref with the ref <paramref name="make"/> returns for the item's name (null: nothing changes).</summary>
    bool UseRuleRef(Func<string, RuleRef?> make);

    /// <summary>Redraws the rule-ref link (the index finished, or a document changed).</summary>
    void RefreshRuleRef();

    /// <summary>Set by the workbench: runs a rulebook search ("Find in rulebook").</summary>
    Action<string>? FindInRulebook { get; set; }

    /// <summary>Set by the workbench: how a rule ref reads and whether it is a link.</summary>
    Func<RuleRef?, RefDisplay>? DescribeRef { get; set; }
}

/// <summary>
/// The rule-ref row of the editors: "Rule ref:", the link (opens the page when clicked; "(missing)" is greyed and does nothing),
/// "Link to current page" and "Find in rulebook" (hidden until a search exists; the owner sets <c>FindButton.IsVisible</c>).
/// </summary>
public sealed class RuleRefRow : StackPanel
{
    static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x66, 0xCC));
    static readonly IBrush GreyBrush = new SolidColorBrush(Color.FromRgb(0x6D, 0x6D, 0x6D));

    readonly TextBlock _link = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    RefDisplay _display = new(SearchPanelModel.None, false, false);

    public Button LinkButton { get; } = new() { Content = "Link to current page", Margin = new Thickness(0, 0, 4, 0) };
    public Button FindButton { get; } = new() { Content = "Find in rulebook", IsVisible = false };
    public event Action? LinkClicked;

    public RuleRefRow()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 2;
        Children.Add(new TextBlock { Text = "Rule ref:", VerticalAlignment = VerticalAlignment.Center });
        Children.Add(_link);
        Children.Add(LinkButton);
        Children.Add(FindButton);
        _link.PointerPressed += (_, e) =>
        {
            if (!_display.IsLink || !e.GetCurrentPoint(_link).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            LinkClicked?.Invoke();
        };
        Show(null, null);
    }

    public string Text => _link.Text ?? "";
    public bool IsLink => _display.IsLink;
    public bool IsGreyed => _display.Missing;

    public void Show(RuleRef? r, Func<RuleRef?, RefDisplay>? describe)
    {
        _display = describe?.Invoke(r) ?? (r == null ? new RefDisplay(SearchPanelModel.None, false, false) : new RefDisplay($"{r.Label}, page {r.Page}", true, false));
        _link.Text = _display.Text;
        _link.Foreground = _display.Missing ? GreyBrush : _display.IsLink ? LinkBrush : null;
        _link.TextDecorations = _display.IsLink ? TextDecorations.Underline : null;
        _link.Cursor = _display.IsLink ? new Cursor(StandardCursorType.Hand) : null;
    }

    /// <summary>Raises <see cref="LinkClicked"/> as a click on the link would (only when it is a link). For tests.</summary>
    internal void ClickLink() { if (_display.IsLink) LinkClicked?.Invoke(); }
}
