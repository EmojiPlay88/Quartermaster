using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>One player row: name plus one "held" flag per side of the pack. Changed only through <see cref="PlayersEditModel"/>.</summary>
public sealed class PlayerRow
{
    public string Id { get; }
    public string Name { get; internal set; }
    public bool[] Held { get; }

    internal PlayerRow(string id, string name, bool[] held) { Id = id; Name = name; Held = held; }
}

/// <summary>
/// Edit model of the player list: one row per player (name + a flag per side). One player = solo (an empty list is the result).
/// Port of the WinForms PlayersEditor logic.
/// </summary>
public sealed class PlayersEditModel
{
    private readonly RulesPack? _pack;
    private readonly List<SideDef> _sides;
    private readonly List<PlayerRow> _rows = new();
    private readonly HashSet<string> _usedIds = new();
    private int _nextId = 1;

    public PlayersEditModel(RulesPack? pack, IReadOnlyList<PlayerDef> players)
    {
        _pack = pack;
        _sides = (pack?.Sides ?? new List<SideDef>()).Where(s => s != null && !string.IsNullOrEmpty(s.Id)).ToList();
        var start = players.Where(p => p != null).ToList();
        if (start.Count == 0) start.Add(new PlayerDef("p1", "Player 1", _sides.Select(s => s.Id).ToList()));
        foreach (var p in start) AddRow(p.Id, p.Name, p.Sides ?? Array.Empty<string>());
    }

    public event Action? Changed;

    public IReadOnlyList<SideDef> Sides => _sides;
    public IReadOnlyList<PlayerRow> Rows => _rows;

    /// <summary>True when the pack has sides enough for more than one player.</summary>
    public bool CanHaveSeveral => _sides.Count >= 2;
    public bool CanAdd => CanHaveSeveral && _rows.Count < Players.MaxPlayers;
    public bool CanRemove => _rows.Count > 1;

    public string Hint =>
        _sides.Count < 2
            ? (_pack == null ? "Open a rules pack to set up players." : "The rules pack has fewer than two sides: one player plays everything.")
            : $"Each side is held by exactly one player (at most {Players.MaxPlayers} players).";

    /// <summary>A fresh id, never one used before in this editor (a removed player's id is not handed to someone else).</summary>
    private string NewId()
    {
        while (_usedIds.Contains("p" + _nextId)) _nextId++;
        return "p" + _nextId++;
    }

    private PlayerRow? AddRow(string id, string name, IReadOnlyList<string> held)
    {
        if (_rows.Count >= Players.MaxPlayers) return null;
        var row = new PlayerRow(id, name, _sides.Select(s => held.Contains(s.Id)).ToArray());
        _usedIds.Add(id);
        _rows.Add(row);
        return row;
    }

    /// <summary>Adds a player holding no side; returns the existing last row unchanged when already at the maximum.</summary>
    public PlayerRow Add()
    {
        var row = AddRow(NewId(), $"Player {_rows.Count + 1}", Array.Empty<string>());
        if (row == null) return _rows[^1];
        Changed?.Invoke();
        return row;
    }

    public void Remove(PlayerRow row)
    {
        if (_rows.Count <= 1 || !_rows.Remove(row)) return;
        Changed?.Invoke();
    }

    public void SetName(PlayerRow row, string name)
    {
        if (row.Name == name) return;
        row.Name = name;
        Changed?.Invoke();
    }

    /// <summary>Ticking a side for one player takes it away from the others (each side has exactly one holder).</summary>
    public void SetSide(PlayerRow row, int sideIndex, bool held)
    {
        if (sideIndex < 0 || sideIndex >= row.Held.Length || row.Held[sideIndex] == held) return;
        row.Held[sideIndex] = held;
        if (held)
            foreach (var other in _rows)
                if (other != row && sideIndex < other.Held.Length) other.Held[sideIndex] = false;
        Changed?.Invoke();
    }

    /// <summary>The list as edited: empty for one player (solo).</summary>
    public IReadOnlyList<PlayerDef> Result =>
        _rows.Count <= 1
            ? Array.Empty<PlayerDef>()
            : _rows.Select(r => new PlayerDef(r.Id, r.Name.Trim(),
                _sides.Where((_, i) => i < r.Held.Length && r.Held[i]).Select(s => s.Id).ToList())).ToList();

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

    /// <summary>Test helper: two players splitting the sides (even indexes to the first, odd to the second).</summary>
    public void FillTwoPlayersForTests()
    {
        if (!CanHaveSeveral) return;
        while (_rows.Count < 2) AddRow(NewId(), $"Player {_rows.Count + 1}", Array.Empty<string>());
        for (var i = 0; i < _sides.Count; i++)
        {
            _rows[0].Held[i] = i % 2 == 0;
            _rows[1].Held[i] = i % 2 == 1;
        }
        Changed?.Invoke();
    }
}
