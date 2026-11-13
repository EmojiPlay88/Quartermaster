using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Online;

/// <summary>
/// Game > Players… for the host of an online game (spec 8a.10): every seat with its sides and connection; Assign sides… gives the selected
/// seat free sides (or takes some back), Free seat removes a guest that left (or holds no side yet). Follows <see cref="HostSession.SeatsChanged"/>
/// while open and lets go of it when closed (WinForms SeatsDialog).
/// </summary>
sealed class SeatsDialog : Window
{
    readonly HostSession _host;
    readonly RulesPack? _pack;
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Single };
    readonly Button _assign = new() { Content = MenuText.FromWinForms("&Assign sides…"), HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button _free = new() { Content = MenuText.FromWinForms("&Free seat"), HorizontalContentAlignment = HorizontalAlignment.Center };
    bool _closed;

    public SeatsDialog(HostSession host, RulesPack? pack)
    {
        _host = host;
        _pack = pack;
        Title = "Players";
        Name = "SeatsDialog";
        Width = 540;
        Height = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        OnlineUi.Face(this);

        _list.ItemTemplate = new FuncDataTemplate<SeatRow>((row, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("160,200,130") };
            void Cell(string text, int col)
            {
                var t = new TextBlock { Text = text, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
                if (row.Greyed) OnlineUi.Gray(t);
                Grid.SetColumn(t, col);
                g.Children.Add(t);
            }
            Cell(row.Player, 0);
            Cell(row.Sides, 1);
            Cell(row.Status, 2);
            return g;
        });
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("160,200,130"), Margin = new Thickness(16, 8, 8, 2) };
        string[] names = { "Player", "Sides", "Status" };
        for (var i = 0; i < 3; i++)
        {
            var t = new TextBlock { Text = names[i], FontWeight = Avalonia.Media.FontWeight.SemiBold };
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }
        var close = OnlineUi.Button("Close", isCancel: true);
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(8, 4, 8, 8) };
        foreach (var b in new[] { _assign, _free, close }) { b.MinWidth = 75; buttons.Children.Add(b); }
        var note = new TextBlock { Text = SeatRows.Note, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(8, 2, 8, 2) };
        OnlineUi.Gray(note);
        var bottom = new StackPanel();
        bottom.Children.Add(note);
        bottom.Children.Add(buttons);
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(bottom);
        root.Children.Add(header);
        root.Children.Add(new Border { Margin = new Thickness(8, 0, 8, 0), Child = _list });
        Content = root;

        _assign.Click += (_, _) => _ = Guarded(AssignAsync);
        _free.Click += (_, _) => _ = Guarded(FreeAsync);
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.DoubleTapped += (_, _) => _ = Guarded(AssignAsync);
        _host.SeatsChanged += OnSeatsChanged;
        Closed += (_, _) => { _closed = true; _host.SeatsChanged -= OnSeatsChanged; };
        Fill();
    }

    internal ListBox List => _list;
    internal Button AssignButton => _assign;
    internal Button FreeButton => _free;

    /// <summary>The lines now shown.</summary>
    internal IReadOnlyList<SeatRow> Rows => _list.ItemsSource as IReadOnlyList<SeatRow> ?? Array.Empty<SeatRow>();

    void OnSeatsChanged()
    {
        if (_closed) return;
        _ = Guarded(() => { Fill(); return Task.CompletedTask; });
    }

    Seat? Selected => (_list.SelectedItem as SeatRow)?.Seat;

    internal void Fill()
    {
        var keep = Selected?.PlayerId;
        var rows = SeatRows.For(_host.Seats, _pack);
        _list.ItemsSource = rows;
        if (keep != null) _list.SelectedItem = rows.FirstOrDefault(r => r.Seat?.PlayerId == keep);
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var s = Selected;
        _assign.IsEnabled = SeatRows.CanAssign(s);
        _free.IsEnabled = SeatRows.CanFree(s);
    }

    async Task AssignAsync()
    {
        if (Selected is not { } seat || _pack == null) return;
        var offered = SeatRows.Offered(_host.Seats, _pack, seat);
        var chosen = await new SideChoiceDialog(SeatRows.AssignTitle(seat.Name), SeatRows.AssignPrompt, offered, seat.Sides).ShowDialogTracked<IReadOnlyList<string>?>(this);
        if (chosen == null || _closed) return;
        if (_host.AssignSides(seat.PlayerId, chosen) is { } why) await MessageBox.ShowAsync(this, why, Title ?? "", yesNo: false);
        if (!_closed) Fill();
    }

    async Task FreeAsync()
    {
        if (Selected is not { } seat) return;
        if (!await MessageBox.ShowAsync(this, SeatRows.FreeQuestion(seat.Name), Title ?? "", yesNo: true, defaultNo: false) || _closed) return;
        if (!_host.FreeSeat(seat.PlayerId)) await MessageBox.ShowAsync(this, SeatRows.CannotFreeText, Title ?? "", yesNo: false);
        if (!_closed) Fill();
    }

    async Task Guarded(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("players dialog: " + ex.Message);
            try { if (!_closed) await MessageBox.ShowAsync(this, ex.Message, Title ?? "", yesNo: false); }
            catch (Exception ex2) when (ex2 is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("players dialog warning: " + ex2.Message); }
        }
    }
}
