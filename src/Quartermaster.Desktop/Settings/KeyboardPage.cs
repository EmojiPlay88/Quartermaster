using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Quartermaster.Desktop.Input;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

/// <summary>The Keyboard page: every command with its shortcut. Edits are held until the dialog is applied; conflicts block OK/Apply.</summary>
public sealed class KeyboardPage : SettingsPage
{
    static readonly double[] Widths = { 250, 150, 100 };

    readonly KeyboardEditModel _model;
    readonly CommandRegistry _registry;
    readonly IDialogs _dialogs;
    readonly Func<Window?> _owner;
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    readonly TextBlock _status = new() { Foreground = Brushes.Firebrick, Height = 34, TextWrapping = TextWrapping.Wrap };
    internal readonly Button SetButton = new() { Content = "Set...", Margin = new Thickness(0, 0, 6, 0) };
    internal readonly Button ClearButton = new() { Content = "Clear", Margin = new Thickness(0, 0, 6, 0) };
    internal readonly Button ResetButton = new() { Content = "Reset", Margin = new Thickness(0, 0, 6, 0) };
    internal readonly Button ResetAllButton = new() { Content = "Reset all" };

    public KeyboardPage(CommandRegistry registry, IDialogs dialogs, Func<Window?> owner)
    {
        _registry = registry;
        _model = new KeyboardEditModel(registry);
        _dialogs = dialogs;
        _owner = owner;

        var header = new Grid { Margin = new Thickness(4, 0, 0, 2) };
        var titles = new[] { "Command", "Shortcut", "Default" };
        for (var i = 0; i < 3; i++)
        {
            header.ColumnDefinitions.Add(new ColumnDefinition(Widths[i], GridUnitType.Pixel));
            var t = new TextBlock { Text = titles[i], FontWeight = FontWeight.SemiBold };
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(SetButton);
        bar.Children.Add(ClearButton);
        bar.Children.Add(ResetButton);
        bar.Children.Add(ResetAllButton);

        var top = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        top.Children.Add(header);
        top.Children.Add(_list);

        var root = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(bar);
        root.Children.Add(top);
        Content = root;

        SetButton.Click += async (_, _) => await Guarded(SetSelectedAsync);
        ClearButton.Click += (_, _) => { if (SelectedId() is { } id) { _model.Assign(id, ""); Refill(); } };
        ResetButton.Click += (_, _) => { if (SelectedId() is { } id) { _model.Reset(id); Refill(); } };
        ResetAllButton.Click += (_, _) => { _model.ResetAll(); Refill(); };
        _list.DoubleTapped += async (_, _) => await Guarded(SetSelectedAsync);
        _list.SelectionChanged += (_, _) => UpdateButtons();
        UpdateButtons();
    }

    public override string Title => "Keyboard";

    internal ListBox List => _list;
    internal string StatusText => _status.Text ?? "";
    internal IReadOnlyList<double> ColumnWidths => Widths;

    /// <summary>The rows as shown: id, label, shortcut text, default text, flagged (red).</summary>
    public IReadOnlyList<(string Id, string Label, string Shortcut, string Default, bool Flagged)> RowsForTest =>
        _list.Items.OfType<ListBoxItem>().Select(i =>
        {
            var g = (Grid)i.Content!;
            string Cell(int c) => ((TextBlock)g.Children[c]).Text ?? "";
            return ((string)i.Tag!, Cell(0), Cell(1), Cell(2), Equals(i.Foreground, Brushes.Red));
        }).ToList();

    string? SelectedId() => _list.SelectedItem is ListBoxItem { Tag: string id } ? id : null;

    public void SelectId(string id) => _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string?)i.Tag == id);

    /// <summary>Asks for a new shortcut for the selected command; cancel changes nothing.</summary>
    public async Task SetSelectedAsync()
    {
        if (SelectedId() is not { } id) return;
        var label = _registry.Find(id)?.Label ?? id;
        var owner = _owner() ?? TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        var r = await _dialogs.AskShortcutAsync(owner, label);
        if (r == null) return;
        _model.Assign(id, r);
        Refill();
    }

    async Task Guarded(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("keyboard page: " + ex);
            var owner = _owner() ?? TopLevel.GetTopLevel(this) as Window;
            if (owner == null) return;
            try { await _dialogs.ShowWarningAsync(owner, ex.Message, "Settings"); }
            catch (Exception ex2) when (ex2 is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("keyboard page warning: " + ex2); }
        }
    }

    void UpdateButtons() => SetButton.IsEnabled = ClearButton.IsEnabled = ResetButton.IsEnabled = SelectedId() != null;

    static string Show(string spec) => spec.Length == 0 ? "(none)" : ShortcutGestures.Display(spec);

    void Refill()
    {
        var sel = SelectedId();
        var bad = _model.Flagged();
        var items = new List<ListBoxItem>();
        foreach (var d in _model.Commands)
        {
            var g = new Grid();
            var cells = new[] { d.Label, Show(_model.Effective(d.Id)), d.DefaultShortcut.Length == 0 ? "" : ShortcutGestures.Display(d.DefaultShortcut) };
            for (var i = 0; i < 3; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition(Widths[i], GridUnitType.Pixel));
                var t = new TextBlock { Text = cells[i], TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }
            var item = new ListBoxItem { Content = g, Tag = d.Id, Padding = new Thickness(4, 2) };
            if (bad.Contains(d.Id)) item.Foreground = Brushes.Red;
            items.Add(item);
        }
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(i => (string?)i.Tag == sel);
        _status.Text = _model.Problem() ?? "";
        UpdateButtons();
    }

    public override string? Validate() => _model.ValidateText();

    public override void Load(Settings s)
    {
        _model.Load(s);
        Refill();
    }

    public override Settings Collect(Settings s) => _model.Collect(s);
}
