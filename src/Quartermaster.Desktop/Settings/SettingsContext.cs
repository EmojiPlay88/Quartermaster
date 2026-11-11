using Quartermaster.Core;
using Quartermaster.Documents;
using Quartermaster.Desktop.Shell;

namespace Quartermaster.Desktop.Preferences;

/// <summary>What the settings window needs to know about the running app (kept out of the window so tests can stub it).</summary>
/// <param name="ToolbarItems">Toolbar buttons that can be hidden: id and caption.</param>
/// <param name="CacheRoot">Root of the per-module tile/document caches.</param>
/// <param name="OpenModuleCache">Cache folder of the module that is open now (its files are in use), or null.</param>
/// <param name="Notify">Shows a message to the user.</param>
/// <param name="Dialogs">The dialogs (colour chooser).</param>
/// <param name="GameDifficulty">Difficulty of the open game, or null when no game is open.</param>
/// <param name="SetGameDifficulty">Switches the open game's difficulty (takes effect from the next run).</param>
/// <param name="OcrLanguages">The app's OCR language downloads; when set, the OCR languages page is shown (last).</param>
public sealed record SettingsContext(IReadOnlyList<(string Id, string Text)> ToolbarItems, string CacheRoot, Func<string?> OpenModuleCache,
    Func<string, Task> Notify, IDialogs Dialogs, Func<Difficulty?>? GameDifficulty = null, Action<Difficulty>? SetGameDifficulty = null,
    OcrLanguageService? OcrLanguages = null);
