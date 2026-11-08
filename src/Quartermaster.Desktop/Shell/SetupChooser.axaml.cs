using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// The setup chooser, as WinForms SetupChooserDialog (offline): "New game from module" plus the module's setups (with their folder path),
/// and the difficulty. Closes with a <see cref="SetupChoice"/>, or null when cancelled. Stage 1 has no Players page (hot-seat comes later).
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

    /// <summary>For the XAML designer only.</summary>
    public SetupChooser() : this(null, Difficulty.SemiAuto, false) { }

    public SetupChooser(ModuleModel? model, Difficulty defaultDifficulty, bool preselectNew)
    {
        InitializeComponent();
        _difficulty = defaultDifficulty;

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

        Ok.Click += (_, _) => Accept();
        Cancel.Click += (_, _) => Close(null);
    }

    /// <summary>The choice as it stands (what OK returns).</summary>
    public SetupChoice Current
    {
        get
        {
            var diff = _difficulty;
            for (var i = 0; i < _radios.Count; i++) if (_radios[i].IsChecked == true) diff = DifficultyModes[i].D;
            return new SetupChoice(Setups.SelectedIndex >= 0 ? _items[Setups.SelectedIndex] : null, diff);
        }
    }

    /// <summary>The list's lines ("New game from module" first).</summary>
    public IReadOnlyList<string> Lines => (IReadOnlyList<string>)Setups.ItemsSource!;

    public int SelectedIndex { get => Setups.SelectedIndex; set => Setups.SelectedIndex = value; }

    void Accept() => Close(Current);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled) { Close(null); e.Handled = true; }
    }
}
