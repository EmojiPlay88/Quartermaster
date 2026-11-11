using System.Globalization;

namespace Quartermaster.Render;

/// <summary>Which launcher buttons are enabled for the selected row, and the Play offline tooltip (null: none).</summary>
public sealed record LauncherButtons(bool PlayOffline, bool Remove, string? PlayOfflineTip);

/// <summary>The launcher's texts and rules (as in the WinForms <c>LauncherForm</c>): row cells, rules label, initial selection, buttons.</summary>
public static class LauncherModel
{
    public const string NotFound = "(file not found)";
    public const string MissingModuleText = "This module's file was not found. Remove it from the list or add it again.";
    public const string MissingTooltip = "This module's file was not found. Remove it or add it again.";
    public const string NotReadyText = "This module is still being checked; try again in a moment.";
    public const string FooterFormat = "Quartermaster {0}";

    /// <summary>The Module cell: the title, or the file name when the title is blank.</summary>
    public static string TitleText(ModuleRow r) =>
        string.IsNullOrWhiteSpace(r.Entry.Title) ? SafeFileName(r.Entry.Path) : r.Entry.Title;

    /// <summary>The File cell: the file name, with "(file not found)" when the file is missing.</summary>
    public static string FileText(ModuleRow r)
    {
        var file = SafeFileName(r.Entry.Path);
        return r.Missing ? $"{file} {NotFound}" : file;
    }

    /// <summary>The Last played cell: local time "yyyy-MM-dd HH:mm", or empty when never played.</summary>
    public static string LastPlayedText(ModuleEntry e) =>
        e.LastPlayed is { } lp ? lp.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : "";

    /// <summary>"Rules pack: (none)" or "Rules pack: {file or folder name}".</summary>
    public static string RulesLabel(string? rulesPath) =>
        "Rules pack: " + (string.IsNullOrEmpty(rulesPath) ? "(none)" : SafeFileName(rulesPath.TrimEnd('\\', '/')));

    /// <summary>
    /// The row selected when the launcher opens, or -1: the module just played; else, when "Select the last module in the launcher" is on,
    /// the last module and then the first row whose file exists. Paths compare ignoring case.
    /// </summary>
    public static int InitialSelection(IReadOnlyList<ModuleRow> rows, string? justPlayed, Settings s)
    {
        if (rows.Count == 0) return -1;
        var i = justPlayed == null ? -1 : IndexOf(rows, justPlayed);
        if (i < 0 && s.ReopenLastModule)
        {
            i = s.LastModulePath == null ? -1 : IndexOf(rows, s.LastModulePath);
            if (i < 0)
            {
                for (var k = 0; k < rows.Count; k++)
                {
                    if (!rows[k].Missing) { i = k; break; }
                }
            }
        }
        return i;
    }

    /// <summary>Play offline needs a present module whose hash is ready; Remove needs a selection.</summary>
    public static LauncherButtons Buttons(ModuleRow? selected, bool hashReady)
    {
        if (selected == null) return new LauncherButtons(false, false, null);
        if (selected.Missing) return new LauncherButtons(false, true, MissingTooltip);
        if (!hashReady) return new LauncherButtons(false, true, NotReadyText);
        return new LauncherButtons(true, true, null);
    }

    static int IndexOf(IReadOnlyList<ModuleRow> rows, string path)
    {
        for (var k = 0; k < rows.Count; k++)
        {
            if (string.Equals(rows[k].Entry.Path, path, StringComparison.OrdinalIgnoreCase)) return k;
        }
        return -1;
    }

    internal static string SafeFileName(string path)
    {
        try { return Path.GetFileName(path); } catch (ArgumentException) { return path; }
    }
}
