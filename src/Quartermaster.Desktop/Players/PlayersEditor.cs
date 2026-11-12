using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;

// Namespace Shell, not "Players": a namespace of that name would hide Quartermaster.Core.Players (as Settings/ uses Preferences).
namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Edits the player list, as WinForms PlayersEditor: one row per player (name + a check box per side of the rules pack), Add player / Remove.
/// Shared by the setup chooser's Players page and Settings > Game > Players. One player = solo (an empty <see cref="Result"/>).
/// The logic is <see cref="PlayersEditModel"/>; this control only shows it and feeds edits back.
/// </summary>
public sealed class PlayersEditor : UserControl
{
    sealed class Row
    {
        public required PlayerRow Model;
        public required StackPanel Panel;
        public required TextBox Name;
        public required CheckBox[] Sides;
        public required Button Remove;
    }

    readonly StackPanel _rows = new() { Orientation = Orientation.Vertical };
    readonly List<Row> _list = new();
    bool _syncing;

    internal Button AddButton { get; } = new() { Content = MenuText.FromWinForms("&Add player"), HorizontalContentAlignment = HorizontalAlignment.Center };
    internal TextBlock HintText { get; } = new() { Margin = new Thickness(8, 8, 3, 3), VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public PlayersEditor(RulesPack? pack, IReadOnlyList<PlayerDef> players)
    {
        Name = "PlayersEditor";
        Model = new PlayersEditModel(pack, players);
        HintText.Text = Model.Hint;
        HintText.Bind(TextBlock.ForegroundProperty, HintText.GetResourceObservable("Classic.GrayTextBrush"));

        var bottom = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(AddButton, Dock.Left);
        bottom.Children.Add(AddButton);
        bottom.Children.Add(HintText);
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = root;

        AddButton.Click += (_, _) => Model.Add();
        Model.Changed += Sync;
        Sync();
    }

    /// <summary>The edit model behind the rows (changes to it show at once).</summary>
    public PlayersEditModel Model { get; }

    /// <summary>The list as edited: empty for one player (solo).</summary>
    public IReadOnlyList<PlayerDef> Result => Model.Result;

    /// <summary>Why the list cannot be used, or null.</summary>
    public string? Problem => Model.Problem;

    /// <summary>The rows' name boxes, side check boxes (in side order) and Remove buttons, in player order.</summary>
    internal IReadOnlyList<TextBox> NameBoxes => _list.Select(r => r.Name).ToList();
    internal IReadOnlyList<IReadOnlyList<CheckBox>> SideBoxes => _list.Select(r => (IReadOnlyList<CheckBox>)r.Sides).ToList();
    internal IReadOnlyList<Button> RemoveButtons => _list.Select(r => r.Remove).ToList();

    Row MakeRow(PlayerRow pr)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var box = new TextBox { Text = pr.Name, Width = 130, Margin = new Thickness(3, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(box);
        var checks = Model.Sides.Select(s => new CheckBox
        {
            Content = string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name, Margin = new Thickness(3, 3, 3, 3), VerticalAlignment = VerticalAlignment.Center,
        }).ToArray();
        foreach (var c in checks) panel.Children.Add(c);
        var remove = new Button { Content = "Remove", Margin = new Thickness(8, 2, 3, 2), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(remove);
        var row = new Row { Model = pr, Panel = panel, Name = box, Sides = checks, Remove = remove };

        // TextChanged is posted through the dispatcher; the Text property changes synchronously.
        box.OnTextSet(() => { if (!_syncing) Model.SetName(pr, box.Text ?? ""); });
        for (var i = 0; i < checks.Length; i++)
        {
            var at = i;
            checks[i].PropertyChanged += (_, e) =>
            {
                if (e.Property == CheckBox.IsCheckedProperty && !_syncing) Model.SetSide(pr, at, checks[at].IsChecked == true);
            };
        }
        remove.Click += (_, _) => Model.Remove(pr);
        return row;
    }

    /// <summary>Brings the rows in line with the model: adds and removes row controls (existing ones are kept, so a name box being typed in
    /// keeps its focus), then the check boxes and the buttons' enabled state.</summary>
    void Sync()
    {
        _syncing = true;
        try
        {
            for (var i = _list.Count - 1; i >= 0; i--)
            {
                if (Model.Rows.Contains(_list[i].Model)) continue;
                _rows.Children.Remove(_list[i].Panel);
                _list.RemoveAt(i);
            }
            for (var i = 0; i < Model.Rows.Count; i++)
            {
                var pr = Model.Rows[i];
                var at = _list.FindIndex(r => r.Model == pr);
                if (at == i) continue;
                Row row;
                if (at < 0) row = MakeRow(pr);
                else
                {
                    row = _list[at];
                    _list.RemoveAt(at);
                    _rows.Children.Remove(row.Panel);
                }
                _list.Insert(i, row);
                _rows.Children.Insert(i, row.Panel);
            }
            foreach (var r in _list)
            {
                if ((r.Name.Text ?? "") != r.Model.Name) r.Name.Text = r.Model.Name;
                for (var i = 0; i < r.Sides.Length; i++) r.Sides[i].IsChecked = i < r.Model.Held.Length && r.Model.Held[i];
                r.Remove.IsEnabled = Model.CanRemove;
            }
            AddButton.IsEnabled = Model.CanAdd;
        }
        finally { _syncing = false; }
    }
}
