namespace Quartermaster.Desktop.Launcher;

/// <summary>
/// Where the update screens hook into the launcher (port stage 5a task 5): <see cref="Attach"/> runs once the launcher window is open and may
/// fill its update bar (<see cref="LauncherWindow.UpdateBarHost"/>) and footer links (<see cref="LauncherWindow.FooterLinks"/>).
/// </summary>
public interface IUpdateSlot
{
    void Attach(LauncherWindow w);
}
