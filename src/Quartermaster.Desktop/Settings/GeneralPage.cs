using Avalonia.Controls;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Preferences;

public sealed class GeneralPage : SettingsPage
{
    internal readonly CheckBox Reopen = Check("Select the last module in the launcher");
    internal readonly NumericUpDown Autosave = Num(0, 240);
    internal readonly CheckBox ConfirmDelete = Check("Ask before deleting pieces");
    internal readonly CheckBox GameMaster = Check("Game master mode (shows the counter palette)");
    internal readonly CheckBox Updates = Check("Check for updates on start (at most once a day, in the launcher)");

    public GeneralPage()
    {
        var t = Table();
        Full(t, Reopen);
        Row(t, "Autosave every (minutes, 0 = off):", Autosave);
        Full(t, ConfirmDelete);
        Full(t, GameMaster);
        Full(t, Updates);
        Content = Host(t);
    }

    public override string Title => "General";

    public override void Load(Settings s)
    {
        Reopen.IsChecked = s.ReopenLastModule;
        Set(Autosave, s.AutosaveMinutes);
        ConfirmDelete.IsChecked = s.ConfirmDelete;
        GameMaster.IsChecked = s.GameMasterMode;
        Updates.IsChecked = s.CheckUpdatesOnStart;
    }

    public override Settings Collect(Settings s) => s with
    {
        ReopenLastModule = Reopen.IsChecked == true, AutosaveMinutes = Int(Autosave), ConfirmDelete = ConfirmDelete.IsChecked == true,
        GameMasterMode = GameMaster.IsChecked == true, CheckUpdatesOnStart = Updates.IsChecked == true,
    };
}
