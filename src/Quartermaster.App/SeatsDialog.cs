using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// Game → Players… for the host of an online game (spec 8a.10): every seat with its sides and connection; Assign sides… gives the
/// selected seat free sides (or takes some back), Free seat removes a guest that left (or holds no side yet), so its sides become open
/// seats again. Follows <see cref="HostSession.SeatsChanged"/> while open.
/// </summary>
sealed class SeatsDialog : Form
{
    readonly HostSession _host;
    readonly RulesPack? _pack;
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
    };
    readonly Button _assign = new() { Text = "&Assign sides…", AutoSize = true };
    readonly Button _free = new() { Text = "&Free seat", AutoSize = true };
    readonly Label _note = new() { Dock = DockStyle.Bottom, Height = 34, ForeColor = SystemColors.GrayText, Padding = new Padding(8, 2, 8, 2) };

    public SeatsDialog(HostSession host, RulesPack? pack)
    {
        _host = host;
        _pack = pack;
        Text = "Players";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ClientSize = new Size(520, 300);
        ShowIcon = false;
        _list.Columns.Add("Player", 160);
        _list.Columns.Add("Sides", 200);
        _list.Columns.Add("Status", 130);
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 36, Padding = new Padding(4) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_free);
        buttons.Controls.Add(_assign);
        _note.Text = "Free sides are open seats in the lobby. A seat can be freed when its player has left or holds no side.";
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 0) };
        listHost.Controls.Add(_list);
        Controls.Add(listHost);
        Controls.Add(_note);
        Controls.Add(buttons);
        CancelButton = close;
        _assign.Click += (_, _) => Guarded(Assign);
        _free.Click += (_, _) => Guarded(Free);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => Guarded(Assign);
        _host.SeatsChanged += OnSeatsChanged;
        FormClosed += (_, _) => _host.SeatsChanged -= OnSeatsChanged;
        Fill();
    }

    void OnSeatsChanged()
    {
        if (IsDisposed) return;
        Guarded(Fill);
    }

    string NameOf(string sideId) => _pack?.Sides.FirstOrDefault(x => x?.Id == sideId)?.Name is { Length: > 0 } n ? n : sideId;

    Seat? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as Seat : null;

    void Fill()
    {
        var keep = Selected?.PlayerId;
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var seat in _host.Seats.All)
            {
                var status = seat.IsHost ? "host (you)" : !seat.Connected ? "left" : seat.Sides.Count == 0 ? "choosing a side" : "connected";
                var item = new ListViewItem(new[] { seat.Name, seat.Sides.Count == 0 ? "—" : string.Join(", ", seat.Sides.Select(NameOf)), status }) { Tag = seat };
                if (!seat.Connected) item.ForeColor = SystemColors.GrayText;
                _list.Items.Add(item);
                if (seat.PlayerId == keep) item.Selected = true;
            }
            var free = _host.Seats.FreeSides(_pack);
            if (free.Count > 0)
                _list.Items.Add(new ListViewItem(new[] { "(open seat)", string.Join(", ", free.Select(NameOf)), "free" }) { ForeColor = SystemColors.GrayText });
        }
        finally { _list.EndUpdate(); }
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var s = Selected;
        _assign.Enabled = s != null;
        _free.Enabled = s is { IsHost: false } && (!s.Connected || s.Sides.Count == 0);
    }

    void Assign()
    {
        if (Selected is not { } seat || _pack == null) return;
        var free = _host.Seats.FreeSides(_pack);
        var offered = _pack.Sides.Where(x => x?.Id != null && (free.Contains(x.Id) || seat.Sides.Contains(x.Id))).ToList();
        using var dlg = new SideChoiceDialog($"Sides for {seat.Name}", "Sides this player holds:", offered, seat.Sides);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (_host.AssignSides(seat.PlayerId, dlg.Chosen) is { } why)
            MessageBox.Show(this, why, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Fill();
    }

    void Free()
    {
        if (Selected is not { } seat) return;
        if (MessageBox.Show(this, $"Free {seat.Name}'s seat? Their sides become open seats again.", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        if (!_host.FreeSeat(seat.PlayerId))
            MessageBox.Show(this, "Only a seat whose player has left, or that holds no side yet, can be freed.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        Fill();
    }

    void Guarded(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("players dialog: " + ex.Message);
            if (!IsDisposed) MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>A tick box per side: a guest choosing its side after joining, or the host assigning sides to a seat.</summary>
sealed class SideChoiceDialog : Form
{
    readonly List<(CheckBox Box, string Id)> _boxes = new();

    public IReadOnlyList<string> Chosen => _boxes.Where(b => b.Box.Checked).Select(b => b.Id).ToList();

    public SideChoiceDialog(string title, string prompt, IReadOnlyList<SideDef> sides, IReadOnlyCollection<string>? ticked = null, string? note = null)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ShowIcon = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12), WrapContents = false };
        flow.Controls.Add(new Label { Text = prompt, AutoSize = true, Margin = new Padding(3, 0, 3, 6) });
        foreach (var s in sides)
        {
            var b = new CheckBox { Text = string.IsNullOrEmpty(s.Name) ? s.Id : s.Name, AutoSize = true, Checked = ticked?.Contains(s.Id) == true };
            _boxes.Add((b, s.Id));
            flow.Controls.Add(b);
        }
        if (_boxes.Count == 1 && ticked == null) _boxes[0].Box.Checked = true;
        if (note != null) flow.Controls.Add(new Label { Text = note, AutoSize = true, MaximumSize = new Size(320, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) });
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(3, 10, 3, 3) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        flow.Controls.Add(buttons);
        Controls.Add(flow);
        AcceptButton = ok;
        CancelButton = cancel;
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK || Chosen.Count > 0) return;
            MessageBox.Show(this, "Tick at least one side.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Cancel = true;
        };
    }
}

/// <summary>
/// The host's own part of an online setup (launcher spec 3.4): the name to play under and the sides the host holds. Every other side of
/// the pack becomes an open seat.
/// </summary>
sealed class HostSidesPanel : UserControl
{
    readonly TextBox _name = new() { Width = 200, MaxLength = ChatText.MaxName };
    readonly List<(CheckBox Box, string Id)> _boxes = new();
    readonly RulesPack? _pack;

    public HostSidesPanel(RulesPack? pack, string playerName, IReadOnlyCollection<string>? preselect = null)
    {
        _pack = pack;
        Name = "HostSidesPanel";
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(4) };
        var nameRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        nameRow.Controls.Add(new Label { Text = "Your name:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _name.Text = playerName;
        nameRow.Controls.Add(_name);
        flow.Controls.Add(nameRow);
        flow.Controls.Add(new Label { Text = "Sides you hold (every other side is an open seat):", AutoSize = true, Margin = new Padding(3, 10, 3, 3) });
        var sides = (pack?.Sides ?? new List<SideDef>()).Where(x => !string.IsNullOrEmpty(x?.Id)).ToList();
        var pre = preselect ?? sides.Take(1).Select(x => x.Id).ToList();
        foreach (var s in sides)
        {
            var b = new CheckBox { Text = string.IsNullOrEmpty(s.Name) ? s.Id : s.Name, AutoSize = true, Checked = pre.Contains(s.Id), Margin = new Padding(20, 2, 3, 2) };
            _boxes.Add((b, s.Id));
            flow.Controls.Add(b);
        }
        if (sides.Count < 2)
            flow.Controls.Add(new Label { Text = OnlineLobbyModel.NeedsPackText, AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = Color.Firebrick });
        Controls.Add(flow);
    }

    public string PlayerName => ChatText.CleanName(_name.Text) ?? "";

    public IReadOnlyList<string> HostSides => _boxes.Where(b => b.Box.Checked).Select(b => b.Id).ToList();

    /// <summary>Null when the name and sides are fine, else the problem in words.</summary>
    public string? Problem =>
        PlayerName.Length == 0 ? HostSession.NoNameText : OnlineLobbyModel.ValidateHostSides(_pack, HostSides);

    /// <summary>The host as the one player of the request (its sides; the rest are open seats).</summary>
    public PlayerDef ToPlayer() => new("host", PlayerName, HostSides.ToList());
}
