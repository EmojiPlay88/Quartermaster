using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

/// <summary>
/// Tools, Settings. One tab per area; OK applies and closes, Apply applies and stays, Cancel closes (an earlier Apply stays applied).
/// "Restore defaults" resets only the visible tab, and takes effect when applied.
/// </summary>
public sealed class SettingsWindow : Window
{
    readonly Action<Settings> _apply;
    readonly IReadOnlyList<SettingsPage> _pages;
    internal IReadOnlyList<SettingsPage> Pages => _pages;
    readonly TabControl _tabs = new();
    readonly Func<Settings>? _latest;
    readonly IDialogs? _dialogs;
    Settings _current;

    public Button OkButton { get; } = new() { Name = nameof(OkButton), Content = "OK", Width = 80, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
    public Button CancelButton { get; } = new() { Name = nameof(CancelButton), Content = "Cancel", Width = 80, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
    public Button ApplyButton { get; } = new() { Name = nameof(ApplyButton), Content = "Apply", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
    public Button RestoreButton { get; } = new() { Name = nameof(RestoreButton), Content = "Restore defaults", Width = 110, HorizontalContentAlignment = HorizontalAlignment.Center };

    public SettingsWindow(Settings initial, Action<Settings> apply, IReadOnlyList<SettingsPage> pages, Func<Settings>? latest = null, IDialogs? dialogs = null)
    {
        _current = initial;
        _apply = apply;
        _pages = pages;
        _latest = latest;
        _dialogs = dialogs;

        Title = "Settings";
        Width = 560;
        Height = 400;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        foreach (var p in pages)
        {
            p.Load(initial);
            _tabs.Items.Add(new TabItem { Header = p.Title, Content = p });
        }

        OkButton.Click += async (_, _) => await Guarded(async () => { if (await CommitAsync()) Close(); });
        ApplyButton.Click += async (_, _) => await Guarded(async () => { await CommitAsync(); });
        CancelButton.Click += (_, _) => Close();
        RestoreButton.Click += async (_, _) => await Guarded(() => { RestorePage(); return Task.CompletedTask; });

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(OkButton);
        right.Children.Add(CancelButton);
        right.Children.Add(ApplyButton);
        var bar = new DockPanel { Margin = new Thickness(8, 6) };
        DockPanel.SetDock(RestoreButton, Dock.Left);
        bar.Children.Add(RestoreButton);
        bar.Children.Add(right);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_tabs);
        Content = root;
    }

    public int SelectedPage => _tabs.SelectedIndex;

    public void SelectPage(int i)
    {
        if (i >= 0 && i < _pages.Count) _tabs.SelectedIndex = i;
    }

    public Settings CollectForTest() => Collect(_current);

    Settings Collect(Settings from) => _pages.Aggregate(from, (s, p) => p.Collect(s));

    /// <summary>Applies the pages; false (with a message, nothing saved) while a page reports a problem.</summary>
    public async Task<bool> CommitAsync()
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            if (_pages[i].Validate() is not { } problem) continue;
            SelectPage(i);
            System.Diagnostics.Trace.WriteLine("settings dialog: " + problem);
            if (_dialogs != null) await _dialogs.ShowWarningAsync(this, problem, "Settings");
            return false;
        }
        _current = Collect(_latest?.Invoke() ?? _current); // on top of what the store holds now
        _apply(_current);
        foreach (var p in _pages) p.AfterApply();
        return true;
    }

    void RestorePage()
    {
        var i = _tabs.SelectedIndex;
        if (i < 0 || i >= _pages.Count) return;
        _pages[i].Load(new Settings());
    }

    async Task Guarded(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("settings window: " + ex);
            if (_dialogs != null)
            {
                try { await _dialogs.ShowWarningAsync(this, ex.Message, "Settings"); }
                catch (Exception ex2) when (ex2 is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("settings window warning: " + ex2); }
            }
        }
    }

    /// <summary>The pages in WinForms order; <paramref name="keyboard"/> is the Keyboard page.</summary>
    public static IReadOnlyList<SettingsPage> StandardPages(SettingsContext ctx, SettingsPage keyboard) => new SettingsPage[]
    {
        new GeneralPage(), new GamePage(ctx), new DisplayPage(ctx), new MapPage(), new DiceLogPage(), new ToolbarPage(ctx.ToolbarItems), keyboard, new FoldersPage(ctx),
    };
}
