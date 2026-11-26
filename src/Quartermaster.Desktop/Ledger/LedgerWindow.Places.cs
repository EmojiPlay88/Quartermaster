using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using CoreValue = Quartermaster.Rules.Expr.Value;

namespace Quartermaster.Desktop.Ledgers;

/// <summary>
/// The Places tab (B6): one row per hex with place values (the hex-keyed properties of the Global holder), the place properties as columns,
/// read from the viewer's state (<see cref="LedgerViewRules.Places"/>: a player view leaves out the columns it may not see). A value cell
/// edits as a counter value does (note prompt) but writes through <see cref="IActionPort.EditKeyed"/>. <c>Add place</c> adds an empty row for a
/// hex that has no values yet; nothing is stored until a value is typed.
/// </summary>
public sealed partial class LedgerWindow
{
    const string AddPlaceTip = "Add a row for a hex that has no place values yet.";
    const string HexPlaceholder = "Hex name";
    internal const string PlacesEmptyText = "No place values yet. A formula output keyed by hex() writes one, or add a hex here.";

    readonly TextBox _hexBox = new() { Width = 110, Name = "LedgerHexBox", PlaceholderText = HexPlaceholder };
    readonly Button _addPlace = Plain("Add place", AddPlaceTip);
    readonly TextBlock _placeStatus = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Foreground = BannerText, Margin = new Thickness(4, 0) };
    readonly TextBlock _placesEmpty = new() { Text = PlacesEmptyText, IsVisible = false, Margin = new Thickness(8, 6) };
    readonly StackPanel _placesBar = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(2), IsVisible = false };   // its own row: the toolbar of the other tabs stays as it was
    readonly List<string> _addedHexes = new();   // hexes added with Add place that have no stored value yet

    static readonly string PlacesKey = LedgerKey.Holder(HolderDef.GlobalId);

    bool Places => _tab.Kind == LedgerTabKind.Places;

    internal TextBox HexBox => _hexBox;
    internal Button AddPlaceButton => _addPlace;
    internal string PlaceStatusText => _placeStatus.Text ?? "";
    internal bool PlacesEmptyShown => _placesEmpty.IsVisible;
    internal bool PlacesToolsShown => _placesBar.IsVisible;
    internal bool UnitTogglesShown => _showCounters.IsVisible || _showEmpty.IsVisible;

    void InitPlaces()
    {
        _placesBar.Children.Add(_hexBox);
        _placesBar.Children.Add(_addPlace);
        _placesBar.Children.Add(_placeStatus);
        ToolTip.SetTip(_hexBox, "The hex to add a row for");
        _hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Run(AddPlace); } };
        _addPlace.Click += (_, _) => Run(AddPlace);
        ShowPlacesTools(false);
    }

    /// <summary>The Places tab shows the hex box and Add place instead of the unit-only toggles.</summary>
    void ShowPlacesTools(bool on)
    {
        _placesBar.IsVisible = on;
        _showCounters.IsVisible = _showEmpty.IsVisible = !on;
        if (!on) _placeStatus.Text = "";
    }

    void UpdatePlacesState() => _placesEmpty.IsVisible = Places && _rows.Count == 0;

    /// <summary>The rows of the Places tab over the viewer's state, plus the hexes added by hand, by name.</summary>
    LedgerTabResult PlacesResultFor(RulesPack pack, GameState view)
    {
        var filter = (_filter.Text ?? "").Trim();
        var found = LedgerViewRules.Places(view, pack, filter);
        var rows = found.Rows.ToList();
        foreach (var hex in _addedHexes)
            if ((filter.Length == 0 || hex.Contains(filter, StringComparison.OrdinalIgnoreCase)) && !rows.Any(r => r.Hex == hex))
                rows.Add(new PlaceRow(hex, found.Columns.Select(_ => (double?)null).ToList()));
        var shown = rows.OrderBy(r => r.Hex, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Hex, StringComparer.Ordinal)
            .Select(r => new LedgerRowEx(new LedgerRow(r.Hex, r.Hex, "", r.Values), null, null, null, false)).ToList();
        return new LedgerTabResult(found.Columns, shown, shown.Count, null);
    }

    string PlacesCsvText() => LedgerViewRules.PlacesCsv(_cols, _rows.Select(r => new PlaceRow(r.Row.Name, r.Row.Values)).ToList());

    /// <summary>Add place: a row for the hex in the box (spelled as its board spells it); an unknown hex says so in the status.</summary>
    internal void AddPlace()
    {
        _placeStatus.Text = "";
        var text = (_hexBox.Text ?? "").Trim();
        if (text.Length == 0 || _s.Rules is not { } pack || pack.Properties.FirstOrDefault(KeyedFacts.IsPlace) is not { } place) return;
        string hex;
        try { hex = KeyedValues.NormaliseKey(pack, _s.Model, place, CoreValue.Of(text)); }
        catch (Quartermaster.Rules.Expr.EvalError ex)
        {
            _placeStatus.Text = ex.Message.Contains("too long") ? "The key is too long." : $"No hex named \"{text}\" on any board.";
            return;
        }
        if (!_addedHexes.Contains(hex)) _addedHexes.Add(hex);
        _hexBox.Text = "";
        _debounce.Stop();
        Reload();
        Restore(new HashSet<string>(StringComparer.Ordinal) { hex }, hex);
    }

    /// <summary>
    /// A manual change of one keyed value (a place cell or a keyed panel row): checks the number and the editor's side, asks for the note, then
    /// writes it through the action port (<see cref="IActionPort.EditKeyed"/>) or, without one, straight into the game.
    /// </summary>
    async Task CommitKeyedAsync(string ledgerKey, string valueKey, PropertyDef prop, string text)
    {
        var game = _s.Game;
        if (game == null || _s.Rules is not { } pack) return;
        if (!LedgerViewRules.TryParseNumber(text, out var after)) { await WarnAsync($"'{text}' is not a number."); Reload(); return; }
        if (_closing || !ReferenceEquals(_s.Game, game)) return;
        if (!_s.CanEditLedger(ledgerKey)) { await WarnAsync("That value " + ModuleSession.OtherSideText + "."); Reload(); return; }
        if (LedgerViewRules.EditKey(pack, _s.Model, prop, ledgerKey, valueKey) is not { } key) { Reload(); return; }
        if (KeyedValues.Get(game.State, ledgerKey, prop.Id, key) is { } cur && cur == after) { Reload(); return; }   // unchanged: no history entry
        var note = await _dialogs.AskTextAsync(this, "Manual change",
            $"Why is {prop.Name}[{key}] being set to {LedgerTable.FormatNumber(after)}? (required)", "");
        if (_closing || !ReferenceEquals(_s.Game, game)) return;
        if (string.IsNullOrWhiteSpace(note) || !_s.CanEditLedger(ledgerKey)) { Reload(); return; }
        if (_actions?.Invoke() is { } port) { port.EditKeyed(ledgerKey, prop.Id, key, after, note); return; }
        if (LedgerViewRules.KeyedEdit(pack, _s.Model, game.State, ledgerKey, prop.Id, key, after, note.Trim()) is { } delta)
            game.Execute(CommandLog.Stamp(delta));
    }
}
