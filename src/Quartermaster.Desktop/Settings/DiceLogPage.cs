using System.Globalization;
using Avalonia.Controls;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

public sealed class DiceLogPage : SettingsPage
{
    internal readonly CheckBox Physical = Check("Physical dice: ask me for each die instead of rolling");
    internal readonly CheckBox LastRoll = Check("Show the last roll in the status bar");
    internal readonly CheckBox Moves = Check("Show move, place and delete rows in the log panel");
    internal readonly TextBox Format = new() { Width = 120 };
    internal readonly TextBlock Preview = new();

    public DiceLogPage()
    {
        Format.OnTextSet(UpdatePreview);
        var t = Table();
        Full(t, Physical);
        Full(t, LastRoll);
        Full(t, Moves);
        Row(t, "Log time format:", Format);
        Row(t, "Preview:", Preview);
        Content = Host(t);
    }

    public override string Title => "Dice & Log";

    void UpdatePreview() =>
        Preview.Text = SettingsStore.IsValidTimeFormat(Format.Text ?? "") ? DateTime.Now.ToString(Format.Text, CultureInfo.InvariantCulture) : "(not a valid time format)";

    public override void Load(Settings s)
    {
        Physical.IsChecked = s.PhysicalDiceDefault;
        LastRoll.IsChecked = s.ShowLastRollInStatus;
        Moves.IsChecked = s.LogShowMoves;
        Format.Text = s.LogTimeFormat;
        UpdatePreview();
    }

    public override Settings Collect(Settings s) => s with
    {
        PhysicalDiceDefault = Physical.IsChecked == true, ShowLastRollInStatus = LastRoll.IsChecked == true, LogShowMoves = Moves.IsChecked == true,
        LogTimeFormat = SettingsStore.IsValidTimeFormat(Format.Text ?? "") ? Format.Text! : s.LogTimeFormat,
    };
}
