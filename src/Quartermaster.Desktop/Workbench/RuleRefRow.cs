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
        if (_display.Missing) _link.Foreground = GreyBrush;
        else if (_display.IsLink) _link.Foreground = LinkBrush;
        else _link.ClearValue(TextBlock.ForegroundProperty);   // a null local value would hide the text
        _link.TextDecorations = _display.IsLink ? TextDecorations.Underline : null;
        _link.Cursor = _display.IsLink ? new Cursor(StandardCursorType.Hand) : null;
    }

    /// <summary>Raises <see cref="LinkClicked"/> as a click on the link would (only when it is a link). For tests.</summary>
    internal void ClickLink() { if (_display.IsLink) LinkClicked?.Invoke(); }
}

/// <summary>
/// The rule-ref picker of the property, holder, alert, phase-warning and checklist editors: "(none)", then every rule ref of the pack as
/// "{label}, page {page}". It only chooses existing refs; making one stays with "Link to current page". A ref id that no longer exists shows
/// as "{id} (missing)" so the stale value can be seen and cleared.
/// </summary>
public static class RuleRefPicker
{
    public const string None = "(none)";

    public static string ItemText(RuleRef r) => $"{r.Label}, page {r.Page}";

    /// <summary>The items and, aligned with them, the ref ids ("" for "(none)"); a dangling <paramref name="current"/> is added last.</summary>
    public static (IReadOnlyList<string> Items, IReadOnlyList<string> Ids) Items(RulesPack? pack, string? current = null)
    {
        var items = new List<string> { None };
        var ids = new List<string> { "" };
        foreach (var r in pack?.RuleRefs ?? new List<RuleRef>()) { items.Add(ItemText(r)); ids.Add(r.Id); }
        if (!string.IsNullOrEmpty(current) && !ids.Contains(current)) { items.Add(Missing(current)); ids.Add(current); }
        return (items, ids);
    }

    static string Missing(string id) => $"{id} (missing)";

    /// <summary>The shown text of a ref id: "(none)" for none, the item text, or "{id} (missing)".</summary>
    public static string Text(RulesPack? pack, string? id) =>
        string.IsNullOrEmpty(id) ? None : pack?.ById<RuleRef>(id!) is { } r ? ItemText(r) : Missing(id!);

    /// <summary>The ref id an item text stands for: "" for "(none)", null for a text that is no item (nothing to change). The first ref
    /// wins when two read alike.</summary>
    public static string? IdOf(RulesPack? pack, string? text, string? current = null)
    {
        var (items, ids) = Items(pack, current);
        var i = items.ToList().IndexOf(text ?? "");
        return i < 0 ? null : ids[i];
    }
}
