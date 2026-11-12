using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The setup chooser, as WinForms SetupChooserDialog (offline): "New game from module" plus the module's setups (with their folder path),
/// and the difficulty; Next goes to the Players page (one player holding every side by default = solo). Closes with a
/// <see cref="SetupChoice"/>, or null when cancelled.
/// </summary>
public partial class SetupChooser : Window
{
    /// <summary>The difficulty radios with the WinForms names and hints.</summary>
    public static readonly (Difficulty D, string Name, string Hint)[] DifficultyModes =
    {
        (Difficulty.Hardcore, "Hardcore", "Find every value in the charts and type it in."),
        (Difficulty.SemiAuto, "Semi-automatic", "Type the values in; a cheat sheet shows where each one comes from."),
        (Difficulty.FullyAuto, "Fully automatic", "The game rolls and calculates everything itself and logs it."),
    };

    readonly List<SetupDef?> _items = new();
    readonly List<RadioButton> _radios = new();
    readonly Difficulty _difficulty;
    readonly RulesPack? _pack;
    readonly IDialogs? _dialogs;
    bool _accepting;

    /// <summary>For the XAML designer only.</summary>
    public SetupChooser() : this(null, Difficulty.SemiAuto, false) { }

    /// <param name="pack">The rules pack whose sides the Players page offers (null: one player plays everything).</param>
    /// <param name="dialogs">Shows the warnings that keep the chooser open (none shown without it).</param>
    public SetupChooser(ModuleModel? model, Difficulty defaultDifficulty, bool preselectNew, RulesPack? pack = null, IDialogs? dialogs = null)
    {
        InitializeComponent();
        _difficulty = defaultDifficulty;
        _pack = pack;
        _dialogs = dialogs;
        Editor = new PlayersEditor(pack, Array.Empty<PlayerDef>());
        PlayersFrame.Child = Editor;

        var names = new List<string> { "New game from module" };
        _items.Add(null);
        foreach (var s in model?.Setups ?? (IReadOnlyList<SetupDef>)Array.Empty<SetupDef>())
        {
            _items.Add(s);
            names.Add(s.NavPath.Count > 0 ? string.Join(" / ", s.NavPath) + " / " + s.Name : s.Name);
        }
        Setups.ItemsSource = names;
        Setups.SelectedIndex = _items.Count > 1 && !preselectNew ? 1 : 0;
        Setups.DoubleTapped += (_, _) => { if (Setups.SelectedIndex >= 0) Accept(); };

        foreach (var (d, name, hint) in DifficultyModes)
        {
            var r = new RadioButton { Content = name, GroupName = "difficulty", IsChecked = d == defaultDifficulty, Margin = new Thickness(0, 4, 0, 0) };
            _radios.Add(r);
            Modes.Children.Add(r);
            Modes.Children.Add(new TextBlock { Text = hint, Foreground = (Avalonia.Media.IBrush?)this.FindResource("Classic.GrayTextBrush"), Margin = new Thickness(20, 0, 0, 0) });
        }

        Next.Click += (_, _) => ShowPlayersPage();
        Back.Click += (_, _) => ShowGamePage();
        Ok.Click += (_, _) => Accept();
        Cancel.Click += (_, _) => Close(null);
    }

    /// <summary>The Players page's editor.</summary>
    internal PlayersEditor Editor { get; }

    /// <summary>True while the Players page (the second page) is shown.</summary>
    public bool OnPlayersPage => PlayersPanel.IsVisible;

    /// <summary>The choice OK closed with (null until then, and when cancelled).</summary>
    public SetupChoice? Result { get; private set; }

    /// <summary>The choice as it stands (what OK returns once the players pass the checks). Players: null unless at least two.</summary>
    public SetupChoice Current
    {
        get
        {
            var diff = _difficulty;
            for (var i = 0; i < _radios.Count; i++) if (_radios[i].IsChecked == true) diff = DifficultyModes[i].D;
            var players = Editor.Result.Count >= 2 ? Editor.Result : null;
            return new SetupChoice(Setups.SelectedIndex >= 0 ? _items[Setups.SelectedIndex] : null, diff, players);
        }
    }

    /// <summary>The Players page (after the difficulty).</summary>
    public void ShowPlayersPage()
    {
        GamePanel.IsVisible = false;
        PlayersPanel.IsVisible = true;
        Next.IsVisible = false;
        Back.IsVisible = true;
    }

    void ShowGamePage()
    {
        PlayersPanel.IsVisible = false;
        GamePanel.IsVisible = true;
        Next.IsVisible = true;
        Back.IsVisible = false;
    }

    /// <summary>The list's lines ("New game from module" first).</summary>
    public IReadOnlyList<string> Lines => (IReadOnlyList<string>)Setups.ItemsSource!;

    public int SelectedIndex { get => Setups.SelectedIndex; set => Setups.SelectedIndex = value; }

    /// <summary>
    /// OK, as the WinForms FormClosing check: a player problem shows the Players page and a "Players" warning and keeps the chooser open;
    /// then the request is checked as a whole (<see cref="NewGameRules.Validate"/>), else the chooser closes with the choice.
    /// </summary>
    async void Accept()
    {
        if (_accepting) return;
        _accepting = true;
        try
        {
            if (Editor.Problem is { } problem)
            {
                ShowPlayersPage();
                if (_dialogs != null) await _dialogs.ShowWarningAsync(this, problem, "Players");
                return;
            }
            var choice = Current;
            var req = new NewGameRequest(GameMode.Offline, choice.Setup, choice.Difficulty, choice.Players, null, null);
            if (NewGameRules.Validate(req, _pack) is { } bad)
            {
                if (_dialogs != null) await _dialogs.ShowWarningAsync(this, bad, Title ?? "");
                return;
            }
            Result = choice;
            Close(choice);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("setup chooser: " + ex); }
        finally { _accepting = false; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled) { Close(null); e.Handled = true; }
    }
}
