namespace Quartermaster.Render;

public enum StartupAction { Launcher, DirectModule }

/// <summary>Whether startup shows the launcher or goes straight to a module (command-line module path, or any smoke run).</summary>
public static class StartupDecision
{
    public static StartupAction For(string? modulePath, bool smoke)
        => smoke || !string.IsNullOrWhiteSpace(modulePath) ? StartupAction.DirectModule : StartupAction.Launcher;
}

public enum LaunchKind { NewGame, OpenSave, Continue }

/// <summary>What the launcher asks the game window to do.</summary>
public sealed record LaunchRequest(string ModulePath, string? RulesPath, LaunchKind Kind, NewGameRequest? NewGame, string? SavePath);

/// <summary>Library and recent-save bookkeeping the game window performs. Never throws on bad input.</summary>
public static class LaunchBookkeeping
{
    public static void ModuleOpened(ModuleLibrary library, string? modulePath, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(modulePath)) return;
        string full;
        try { full = Path.GetFullPath(modulePath); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { full = modulePath; }
        library.MarkPlayed(full, now);
    }

    public static void SaveUsed(RecentGames recent, string? moduleSha, string? savePath)
    {
        if (string.IsNullOrEmpty(moduleSha) || string.IsNullOrWhiteSpace(savePath)) return;
        recent.Add(moduleSha, savePath);
    }
}
