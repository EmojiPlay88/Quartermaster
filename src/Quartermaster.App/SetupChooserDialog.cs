using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.App;

/// <summary>
/// Lists the module's predefined setups plus "New game from module" (<see cref="Selected"/> is null for the latter) with the difficulty;
/// Next goes to the Players page (one player holding every side by default = solo).
/// </summary>
sealed class SetupChooserDialog : Form
{
    readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly List<SetupDef?> _items = new();

    readonly RadioButton[] _radios = new RadioButton[3];
    static readonly (Difficulty D, string Name, string Hint)[] Modes =
    {
        (Difficulty.Hardcore, "Hardcore", "Find every value in the charts and type it in."),
        (Difficulty.SemiAuto, "Semi-automatic", "Type the values in; a cheat sheet shows where each one comes from."),
        (Difficulty.FullyAuto, "Fully automatic", "The game rolls and calculates everything itself and logs it."),
    };

    readonly Panel _gamePage = new() { Dock = DockStyle.Fill };
    readonly GroupBox _playersPage = new() { Text = "Players", Dock = DockStyle.Fill, Visible = false, Padding = new Padding(6) };
    readonly PlayersEditor _players;
    readonly Button _back = new() { Text = "< &Back", Width = 75, Visible = false };
    readonly Button _nextPage = new() { Text = "&Next >", Width = 75 };

    readonly GameMode _mode;
    readonly RulesPack? _pack;
    readonly TextBox _lobbyName = new() { Width = 200, MaxLength = NewGameRules.MaxLobbyName };
    readonly TextBox _password = new() { Width = 200, UseSystemPasswordChar = true };
    readonly Panel _lobbyPanel = new() { Dock = DockStyle.Bottom, Height = 72, Visible = false };
    readonly Label _openWarning = new() { Text = OnlineLobbyModel.OpenLobbyWarning, AutoSize = true, ForeColor = Color.DarkGoldenrod };
    readonly HostSidesPanel? _hostSides;
    readonly Difficulty _difficulty;

    /// <summary>The choice made in the dialog (valid after OK). The caller writes <see cref="NewGameRequest.Difficulty"/> back to <see cref="Settings.LastDifficulty"/>.</summary>
    public NewGameRequest? Request { get; private set; }

    /// <param name="preselect">Selects this setup (the one double-clicked in the window tree).</param>
    /// <param name="preselectNew">Selects "New game from module" instead of the first setup.</param>
    /// <param name="pack">The rules pack whose sides the Players page offers (null: one player plays everything).</param>
    /// <param name="hostName">Online: the name the host plays under (preset in the Players page).</param>
    /// <param name="lobbyName">Online: the preset lobby name.</param>
    public SetupChooserDialog(ModuleModel model, GameMode mode, Difficulty defaultDifficulty = Difficulty.SemiAuto, RulesPack? pack = null, bool preselectNew = false, SetupDef? preselect = null,
        string? hostName = null, string? lobbyName = null)
    {
        _mode = mode;
        _pack = pack;
        _players = new PlayersEditor(pack, Array.Empty<PlayerDef>()) { Dock = DockStyle.Fill };
        _difficulty = defaultDifficulty;
        Text = mode == GameMode.Online ? "Create lobby" : "New game";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ClientSize = new Size(420, 390);

        _items.Add(null);
        _list.Items.Add("New game from module");
        foreach (var s in model.Setups)
        {
            _items.Add(s);
            _list.Items.Add(s.NavPath.Count > 0 ? string.Join(" / ", s.NavPath) + " / " + s.Name : s.Name);
        }
        _list.SelectedIndex = model.Setups.Count > 0 && !preselectNew ? 1 : 0;
        if (preselect != null && _items.IndexOf(preselect) is var at and > 0) _list.SelectedIndex = at;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 34, Padding = new Padding(4),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(_nextPage);
        buttons.Controls.Add(_back);
        _nextPage.Click += (_, _) => ShowPlayersPage();
        _back.Click += (_, _) => ShowGamePage();
        var group = new GroupBox { Text = "Difficulty", Dock = DockStyle.Bottom, Height = 150 };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 2, 6, 2) };
        for (var i = 0; i < Modes.Length; i++)
        {
            var r = new RadioButton { Text = Modes[i].Name, AutoSize = true, Checked = Modes[i].D == defaultDifficulty, Margin = new Padding(3, 4, 3, 0) };
            _radios[i] = r;
            flow.Controls.Add(r);
            flow.Controls.Add(new Label { Text = Modes[i].Hint, AutoSize = false, Width = 380, Height = 17, ForeColor = SystemColors.GrayText, Margin = new Padding(20, 0, 3, 0) });
        }
        group.Controls.Add(flow);
        _lobbyPanel.Controls.Add(new Label { Text = "Lobby name:", Left = 8, Top = 4, Width = 80, AutoSize = false });
        _lobbyName.Left = 92; _lobbyName.Top = 0; _lobbyPanel.Controls.Add(_lobbyName);
        _lobbyPanel.Controls.Add(new Label { Text = "Password:", Left = 8, Top = 28, Width = 80, AutoSize = false });
        _password.Left = 92; _password.Top = 24; _lobbyPanel.Controls.Add(_password);
        _openWarning.Left = 92; _openWarning.Top = 50; _lobbyPanel.Controls.Add(_openWarning);
        _password.TextChanged += (_, _) => _openWarning.Visible = _password.Text.Length == 0;
        _lobbyPanel.Visible = mode == GameMode.Online;
        _gamePage.Controls.Add(_list);
        _gamePage.Controls.Add(group);
        _gamePage.Controls.Add(_lobbyPanel);
        if (mode == GameMode.Online)
        {
            // Online: the host's own sides; every other side is an open seat (launcher spec 3.4).
            _lobbyName.Text = lobbyName ?? "";
            _hostSides = new HostSidesPanel(pack, hostName ?? "") { Dock = DockStyle.Fill };
            _playersPage.Text = "You and the open seats";
            _playersPage.Controls.Add(_hostSides);
            ClientSize = new Size(420, 410);
        }
        else _playersPage.Controls.Add(_players);
        Controls.Add(_gamePage);
        Controls.Add(_playersPage);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedIndex >= 0) DialogResult = DialogResult.OK;
        };
        FormClosing += (_, e) =>
        {
            if (DialogResult == DialogResult.OK && (_hostSides != null ? _hostSides.Problem : _players.Problem) is { } problem)
            {
                ShowPlayersPage();
                MessageBox.Show(this, problem, "Players", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
            if (DialogResult != DialogResult.OK) return;
            var diff = _difficulty;
            for (var i = 0; i < _radios.Length; i++) if (_radios[i].Checked) diff = Modes[i].D;
            var online = _mode == GameMode.Online;
            // Online, the one player is the host (its sides); the game itself starts solo and the seats follow from the lobby.
            var players = _hostSides != null ? new[] { _hostSides.ToPlayer() } : _players.Result.Count >= 2 ? _players.Result : null;
            var req = new NewGameRequest(_mode, _list.SelectedIndex >= 0 ? _items[_list.SelectedIndex] : null, diff,
                players, online ? _lobbyName.Text.Trim() : null, online ? _password.Text : null);
            if (NewGameRules.Validate(req, _pack) is { } bad)
            {
                MessageBox.Show(this, bad, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
            Request = req;
        };
    }

    /// <summary>The Players page (after the difficulty).</summary>
    public void ShowPlayersPage()
    {
        _gamePage.Visible = false;
        _playersPage.Visible = true;
        _nextPage.Visible = false;
        _back.Visible = true;
    }

    void ShowGamePage()
    {
        _playersPage.Visible = false;
        _gamePage.Visible = true;
        _nextPage.Visible = true;
        _back.Visible = false;
    }

    /// <summary>Online: the host's name and sides page (null offline).</summary>
    public HostSidesPanel? HostSidesForSmoke => _hostSides;

    /// <summary>Smoke: the Players page's editor.</summary>
    public PlayersEditor PlayersForSmoke => _players;
}
