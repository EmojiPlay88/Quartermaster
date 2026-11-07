using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// Edits the player list: one row per player (name + a check box per side of the rules pack), Add player / Remove.
/// Shared by the setup chooser's Players page and Settings → Game → Players. One player = solo (an empty list is returned).
/// </summary>
sealed class PlayersEditor : UserControl
{
    sealed class Row
    {
        public required string Id;
        public required FlowLayoutPanel Panel;
        public required TextBox Name;
        public required CheckBox[] Sides;
        public required Button Remove;
    }

    readonly RulesPack? _pack;
    readonly List<SideDef> _sides;
    readonly FlowLayoutPanel _rows = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    readonly Button _add = new() { Text = "&Add player", AutoSize = true, FlatStyle = FlatStyle.System };
    readonly List<Row> _list = new();

    public PlayersEditor(RulesPack? pack, IReadOnlyList<PlayerDef> players)
    {
        _pack = pack;
        _sides = (pack?.Sides ?? new List<SideDef>()).Where(s => s != null && !string.IsNullOrEmpty(s.Id)).ToList();
        Name = "PlayersEditor";
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        var hint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 8, 3, 3) };
        bottom.Controls.Add(_add);
        bottom.Controls.Add(hint);
        Controls.Add(_rows);
        Controls.Add(bottom);
        _add.Click += (_, _) => { AddRow(NewId(), $"Player {_list.Count + 1}", Array.Empty<string>()); Sync(); };

        if (_sides.Count < 2)
        {
            _add.Enabled = false;
            hint.Text = pack == null ? "Open a rules pack to set up players." : "The rules pack has fewer than two sides: one player plays everything.";
        }
        else hint.Text = $"Each side is held by exactly one player (at most {Players.MaxPlayers} players).";

        var start = players.Where(p => p != null).ToList();
        if (start.Count == 0) start.Add(new PlayerDef("p1", "Player 1", _sides.Select(s => s.Id).ToList()));
        foreach (var p in start) AddRow(p.Id, p.Name, p.Sides ?? Array.Empty<string>());
        Sync();
    }

    /// <summary>True when the pack has sides enough for more than one player.</summary>
    public bool CanHaveSeveral => _sides.Count >= 2;

    public int RowCount => _list.Count;

    readonly HashSet<string> _usedIds = new();
    int _nextId = 1;

    /// <summary>A fresh id, never one used before in this editor (a removed player's id is not handed to someone else).</summary>
    string NewId()
    {
        while (_usedIds.Contains("p" + _nextId)) _nextId++;
        return "p" + _nextId++;
    }

    void AddRow(string id, string name, IReadOnlyList<string> held)
    {
        if (_list.Count >= Players.MaxPlayers) return;
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
        var box = new TextBox { Text = name, Width = 130, Margin = new Padding(3, 3, 8, 3) };
        panel.Controls.Add(box);
        var checks = _sides.Select(s => new CheckBox
        {
            Text = string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name, AutoSize = true, Checked = held.Contains(s.Id), Margin = new Padding(3, 5, 3, 3),
        }).ToArray();
        panel.Controls.AddRange(checks);
        var remove = new Button { Text = "Remove", AutoSize = true, FlatStyle = FlatStyle.System, Margin = new Padding(8, 2, 3, 2) };
        panel.Controls.Add(remove);
        var row = new Row { Id = id, Panel = panel, Name = box, Sides = checks, Remove = remove };
        _usedIds.Add(id);
        remove.Click += (_, _) =>
        {
            if (_list.Count <= 1) return;
            _list.Remove(row);
            _rows.Controls.Remove(panel);
            panel.Dispose();
            Sync();
        };
        // Ticking a side for one player takes it away from the others (each side has exactly one holder).
        for (var i = 0; i < checks.Length; i++)
        {
            var at = i;
            checks[i].CheckedChanged += (_, _) =>
            {
                if (!checks[at].Checked) return;
                foreach (var other in _list) if (other != row && at < other.Sides.Length) other.Sides[at].Checked = false;
            };
        }
        _list.Add(row);
        _rows.Controls.Add(panel);
    }

    void Sync()
    {
        _add.Enabled = CanHaveSeveral && _list.Count < Players.MaxPlayers;
        foreach (var r in _list) r.Remove.Enabled = _list.Count > 1;
    }

    /// <summary>The list as edited: empty for one player (solo).</summary>
    public IReadOnlyList<PlayerDef> Result
    {
        get
        {
            if (_list.Count <= 1) return Array.Empty<PlayerDef>();
            return _list.Select(r => new PlayerDef(r.Id, r.Name.Text.Trim(),
                _sides.Where((_, i) => i < r.Sides.Length && r.Sides[i].Checked).Select(s => s.Id).ToList())).ToList();
        }
    }

    /// <summary>Why the list cannot be used, or null.</summary>
    public string? Problem
    {
        get
        {
            var r = Result;
            if (r.Count < 2) return null;
            if (_pack == null) return "Open a rules pack to set up players.";
            try { return Players.Validate(r, _pack); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return "The player list could not be checked: " + ex.Message; }
        }
    }

    /// <summary>True when two lists name the same players holding the same sides (order counts: the first player starts).</summary>
    public static bool Same(IReadOnlyList<PlayerDef> a, IReadOnlyList<PlayerDef> b)
    {
        if (a.Count <= 1 && b.Count <= 1) return true;
        return a.Count == b.Count && a.Zip(b).All(x => x.First.Id == x.Second.Id && x.First.Name == x.Second.Name && x.First.Sides.SequenceEqual(x.Second.Sides));
    }

    /// <summary>Smoke: fills the editor with two players splitting the sides.</summary>
    public void SmokeTwoPlayers()
    {
        if (!CanHaveSeveral) return;
        while (_list.Count < 2) AddRow(NewId(), $"Player {_list.Count + 1}", Array.Empty<string>());
        for (var i = 0; i < _sides.Count; i++)
        {
            _list[0].Sides[i].Checked = i % 2 == 0;
            _list[1].Sides[i].Checked = i % 2 == 1;
        }
        Sync();
    }
}
