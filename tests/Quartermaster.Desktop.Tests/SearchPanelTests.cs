using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Desktop.Workbench;
using Quartermaster.Render.Search;

namespace Quartermaster.Desktop.Tests;

/// <summary>Port stage 4a task 11: the rulebook results panel (rows, activation, context menu, collapse). Headless, invented rows.</summary>
public sealed class SearchPanelTests : IDisposable
{
    readonly List<Window> _windows = new();

    public void Dispose() { foreach (var w in _windows) w.Close(); }

    static void Pump() => Dispatcher.UIThread.RunJobs();

    static SearchRow Doc(string entry, string title, string text) => new(SearchRowKind.Document, entry, title, 0, text, new[] { new TextRun(text, true) });
    static SearchRow Hit(string entry, int page, params TextRun[] runs) => new(SearchRowKind.Hit, entry, "Invented rules", page, "p. " + page, runs);
    static SearchRow NoText(string entry) => new(SearchRowKind.NotSearchable, entry, "Scanned book", 0, "Scanned book — not searchable", new[] { new TextRun("Scanned book — not searchable", false) });
    static SearchRow Msg(string text) => new(SearchRowKind.Message, "", "", 0, text, new[] { new TextRun(text, false) });

    static readonly SearchRow[] Rows =
    {
        Doc("a.pdf", "Invented rules", "Invented rules — 2 pages"),
        Hit("a.pdf", 2, new TextRun("Each unit traces ", false), new TextRun("supply", true), new TextRun(" to a depot", false)),
        Hit("a.pdf", 5, new TextRun("A unit out of ", false), new TextRun("supply", true)),
        NoText("scan.pdf"),
        Msg("Showing the first 200 pages."),
    };

    SearchPanel Panel(IReadOnlyList<SearchRow>? rows = null, bool show = true)
    {
        var p = new SearchPanel { Height = 190 };
        var w = new Window { Width = 600, Height = 260, Content = p };
        _windows.Add(w);
        w.Show();
        if (rows != null) p.ShowRows(rows);
        Pump();
        w.UpdateLayout();
        Pump();
        return p;
    }

    static IEnumerable<TextBlock> Texts(Control c) => c.GetVisualDescendants().OfType<TextBlock>();

    static Control Row(SearchPanel p, int i) => p.List.ContainerFromIndex(i) ?? throw new InvalidOperationException("row " + i + " is not realized");

    [AvaloniaFact]
    public void Rows_headings_hits_bold_not_searchable_grey()
    {
        var p = Panel(Rows);
        Assert.Equal(5, p.Rows.Count);
        Assert.Equal(1, p.List.SelectedIndex);   // the first hit is selected

        var heading = Texts(Row(p, 0)).Single();
        Assert.Equal("Invented rules — 2 pages", heading.Text);
        Assert.Equal(FontWeight.Bold, heading.FontWeight);

        var texts = Texts(Row(p, 1)).ToList();
        Assert.Contains(texts, t => t.Text == "p. 2");
        var snippet = texts.Single(t => t.Inlines is { Count: > 0 });
        var runs = snippet.Inlines!.OfType<Run>().ToList();
        Assert.Equal(new[] { "Each unit traces ", "supply", " to a depot" }, runs.Select(r => r.Text));
        Assert.Equal(new[] { false, true, false }, runs.Select(r => r.FontWeight == FontWeight.Bold));

        var grey = Texts(Row(p, 3)).Single();
        Assert.Equal("Scanned book — not searchable", grey.Text);
        Assert.True(Application.Current!.TryGetResource("Classic.GrayTextBrush", Application.Current.ActualThemeVariant, out var gray));
        Assert.Equal(((ISolidColorBrush)gray!).Color, ((ISolidColorBrush)grey.Foreground!).Color);
        Assert.Equal(((ISolidColorBrush)gray!).Color, ((ISolidColorBrush)Texts(Row(p, 4)).Single().Foreground!).Color);   // messages too
    }

    [AvaloniaFact]
    public void Enter_and_double_click_activate()
    {
        var p = Panel(Rows);
        var hits = new List<(string, int)>();
        var noText = new List<string>();
        p.HitActivated += (e, pg) => hits.Add((e, pg));
        p.NotSearchableActivated += noText.Add;

        p.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(new[] { ("a.pdf", 2) }, hits);

        p.List.SelectedIndex = 2;
        p.List.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!));
        Assert.Equal(new[] { ("a.pdf", 2), ("a.pdf", 5) }, hits);

        p.List.SelectedIndex = 3;
        p.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(new[] { "scan.pdf" }, noText);
        Assert.Equal(2, hits.Count);

        p.List.SelectedIndex = 0;   // a heading and a message open nothing
        p.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        p.List.SelectedIndex = 4;
        p.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(2, hits.Count);
        Assert.Single(noText);
    }

    [AvaloniaFact]
    public void Context_menu_use_as_rule_ref_enabled_by_callback()
    {
        var p = Panel(Rows);
        var used = new List<(string, int)>();
        p.UseAsRuleRef += (e, pg) => used.Add((e, pg));
        p.SelectHit(5);

        var allowed = false;
        p.CanUseRuleRef = () => allowed;
        Assert.True(p.PrepareMenuForTests());
        Assert.True(p.UseRefItem.IsVisible);
        Assert.False(p.UseRefItem.IsEnabled);
        Assert.Equal("Use as rule ref", p.UseRefItem.Header);

        allowed = true;
        Assert.True(p.PrepareMenuForTests());
        Assert.True(p.UseRefItem.IsEnabled);
        p.UseRefItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(new[] { ("a.pdf", 5) }, used);

        p.List.SelectedIndex = 0;   // a heading has no menu
        Assert.False(p.PrepareMenuForTests());
    }

    [AvaloniaFact]
    public void Scan_page_1_only_on_not_searchable()
    {
        var p = Panel(Rows);
        var scanned = new List<string>();
        p.ScanFirstPage += scanned.Add;

        p.List.SelectedIndex = 3;
        Assert.True(p.PrepareMenuForTests());
        Assert.True(p.ScanFirstItem.IsVisible);
        Assert.False(p.UseRefItem.IsVisible);
        Assert.Equal("Scan page 1", p.ScanFirstItem.Header);
        p.ScanFirstItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(new[] { "scan.pdf" }, scanned);

        p.SelectHit(2);
        Assert.True(p.PrepareMenuForTests());
        Assert.False(p.ScanFirstItem.IsVisible);
        Assert.True(p.UseRefItem.IsVisible);
    }

    [AvaloniaFact]
    public void Collapse_toggle()
    {
        var p = Panel();
        var changes = 0;
        p.CollapsedChanged += () => changes++;
        Assert.Equal(190d, SearchPanel.ExpandedHeight);
        Assert.Equal(24d, SearchPanel.HeaderHeight);
        Assert.False(p.Collapsed);
        Assert.True(p.List.IsVisible);
        Assert.Equal("▾", p.ToggleButton.Content);

        p.ToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(p.Collapsed);
        Assert.False(p.List.IsVisible);
        Assert.Equal("▸", p.ToggleButton.Content);
        Assert.Equal(1, changes);

        p.Collapsed = true;   // no change, no event
        Assert.Equal(1, changes);

        p.ShowRows(Rows);     // results expand a collapsed panel
        Assert.False(p.Collapsed);
        Assert.True(p.List.IsVisible);
        Assert.Equal(2, changes);

        p.Title = "Rulebook search: supply (2 pages)";
        Assert.Equal("Rulebook search: supply (2 pages)", p.Title);
    }
}
