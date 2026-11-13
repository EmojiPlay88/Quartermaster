using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Quartermaster.Core;
using Quartermaster.Desktop.Online;
using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Desktop.Shell;

/// <summary>A file-type filter for the pickers, e.g. ("VASSAL modules (*.vmod)", ["*.vmod"]).</summary>
public sealed record FileFilter(string Name, IReadOnlyList<string> Patterns)
{
    public FileFilter(string name, params string[] patterns) : this(name, (IReadOnlyList<string>)patterns) { }
}

/// <summary>The setup chooser's answer: a predefined setup (null = "New game from module"), the difficulty and the players
/// (null = solo; otherwise at least two, checked against the rules pack).</summary>
public sealed record SetupChoice(SetupDef? Setup, Difficulty Difficulty, IReadOnlyList<PlayerDef>? Players = null);

/// <summary>The dice dialog's answer: the dice and how many times to roll them (1 except for Multi-roll).</summary>
public sealed record DiceChoice(DiceSpec Spec, int Times);

/// <summary>The Set Turn dialog's answer: the turn number and the phase as typed.</summary>
public sealed record TurnChoice(int Turn, string Phase);

/// <summary>
/// Every dialog the main window shows: file pickers, Yes/No questions, warnings and the setup chooser. The window only talks to dialogs
/// through this, so tests swap in a fake and never open a real window.
/// </summary>
public interface IDialogs
{
    /// <summary>An existing file's local path, or null when cancelled. The picker starts in <paramref name="initialFolder"/> when it is given
    /// and exists.</summary>
    Task<string?> OpenFileAsync(Window owner, string title, IReadOnlyList<FileFilter> filters, string? initialFolder = null);

    /// <summary>Existing files' local paths (multi-select, as the launcher's Add module), or an empty list when cancelled.</summary>
    Task<IReadOnlyList<string>> OpenFilesAsync(Window owner, string title, IReadOnlyList<FileFilter> filters);

    /// <summary>The "Set shortcut" prompt (WinForms ShortcutCaptureDialog) for <paramref name="commandLabel"/>: the canonical shortcut text pressed,
    /// "" = clear, null = cancelled.</summary>
    Task<string?> AskShortcutAsync(Window owner, string commandLabel);

    /// <summary>The colour chooser opened on <paramref name="initialHex"/>: "#RRGGBB" upper-case, or null when cancelled.</summary>
    Task<string?> AskColourAsync(Window owner, string initialHex);

    /// <summary>A path to save to (with <paramref name="defaultExtension"/> added when the name has none), or null when cancelled.</summary>
    Task<string?> SaveFileAsync(Window owner, string title, FileFilter filter, string defaultExtension, string? suggestedFileName = null);

    /// <summary>A Yes/No question, No focused unless <paramref name="defaultNo"/> is false (WinForms MessageBoxDefaultButton.Button2, used by delete
    /// confirmations; the safer default). True = Yes.</summary>
    Task<bool> AskYesNoAsync(Window owner, string message, string caption, bool defaultNo = true);

    /// <summary>A Yes / No / Cancel question (WinForms MessageBoxButtons.YesNoCancel, Yes focused). Yes = true, No = false, Cancel = null.</summary>
    Task<bool?> AskYesNoCancelAsync(Window owner, string message, string caption);

    /// <summary>A one-line text prompt with OK / Cancel (WinForms InputDialog); the entered text, or null when cancelled.</summary>
    Task<string?> AskTextAsync(Window owner, string title, string prompt, string initial);

    /// <summary>A piece's property sheet (WinForms PropertySheetDialog): one text box per row; the edited values in row order, or null when
    /// cancelled. <paramref name="managedValue"/> gives the Ledger value of a row a rules-pack property manages (that box is read-only).</summary>
    Task<IReadOnlyList<string>?> EditPropertySheetAsync(Window owner, string title, IReadOnlyList<(string Name, string Value)> rows,
        Func<string, string?>? managedValue);

    /// <summary>A warning with an OK button.</summary>
    Task ShowWarningAsync(Window owner, string message, string caption);

    /// <summary>The setup chooser (WinForms SetupChooserDialog); null when cancelled. Its Players page offers <paramref name="pack"/>'s sides
    /// (null: one player plays everything).</summary>
    Task<SetupChoice?> ChooseSetupAsync(Window owner, ModuleModel model, bool preselectNew, Difficulty difficulty, RulesPack? pack);

    /// <summary>The dice expression prompt (WinForms DiceDialog), with Times when <paramref name="multi"/>; null when cancelled.</summary>
    Task<DiceChoice?> AskDiceAsync(Window owner, string title, bool multi);

    /// <summary>The Set Turn dialog (WinForms TurnDialog) opened at <paramref name="current"/>; null when cancelled.</summary>
    Task<TurnChoice?> AskTurnAsync(Window owner, TurnState current);

    /// <summary>The Next Phase warning list (WinForms NextPhaseDialog): true = Continue anyway, false = Go back. A double-clicked decision goes
    /// back and is handed to <paramref name="jumpTo"/> once the dialog has closed.</summary>
    Task<bool> ConfirmOpenDecisionsAsync(Window owner, IReadOnlyList<Decision> open, Action<Decision>? jumpTo = null);

    /// <summary>The fully automatic missing-input dialog (WinForms MissingInputDialog); OK has already applied the answers through <paramref name="port"/>.</summary>
    Task<MissingInputResult> AskMissingAsync(Window owner, ModuleSession s, IActionPort port, PendingRun run);

    /// <summary>The Step Back dialog (WinForms StepBackDialog): the list of checkpoints, or with <paramref name="confirmOnly"/> the one row to
    /// confirm. The chosen row, or null when cancelled.</summary>
    Task<CheckpointRow?> PickCheckpointAsync(Window owner, IReadOnlyList<CheckpointRow> rows, bool confirmOnly);

    /// <summary>The "Save map as picture" choice (WinForms ExportChoiceDialog): the current view or the whole map at 25 / 50 / 100 %; null when cancelled.</summary>
    Task<ExportChoice?> AskExportChoiceAsync(Window owner, string mapName);

    /// <summary>The Ledger tab editor (WinForms EditTabsDialog) opened at user tab <paramref name="select"/>, or with a fresh tab added and
    /// selected when <paramref name="addNew"/> (the "+" tab); the edited list, or null when cancelled.</summary>
    Task<IReadOnlyList<UserLedgerTab>?> EditLedgerTabsAsync(Window owner, IReadOnlyList<UserLedgerTab> tabs, RulesPack? pack, int select = 0, bool addNew = false);

    /// <summary>The Columns dialog (WinForms ColumnsDialog): one key per line, opened on <paramref name="keys"/>; the trimmed non-blank keys, or null when cancelled.</summary>
    Task<IReadOnlyList<string>?> AskColumnsAsync(Window owner, IReadOnlyList<string> keys);

    /// <summary>The paste / import preview (WinForms PasteTableDialog) of <paramref name="text"/>; the parsed grid, or null when cancelled.
    /// <paramref name="tsvOnly"/> is a scanned grid: split on tabs only, OK refused while <see cref="Quartermaster.Rules.Tables.TablePaste.Problem"/> says so.</summary>
    Task<Quartermaster.Rules.Tables.PastedGrid?> PasteTableAsync(Window owner, string text, string? title = null, string? targetDescription = null, bool tsvOnly = false);

    /// <summary>The "Make block" dialog (WinForms PhasesTab.BlockDialog): repeat count and label; null when cancelled.</summary>
    Task<(int Count, string Label)?> AskBlockAsync(Window owner);

    /// <summary>"Where should the scanned rows go?": true = replace <paramref name="selectedTable"/>, false = a new table, null = cancel.</summary>
    Task<bool?> AskImportTargetAsync(Window owner, string selectedTable);

    /// <summary>The clipboard's text, or null when it holds none or is unavailable.</summary>
    Task<string?> GetClipboardTextAsync(Window owner);

    /// <summary>Puts <paramref name="text"/> on the clipboard; never throws (a busy clipboard is only traced).</summary>
    Task SetClipboardTextAsync(Window owner, string text);

    /// <summary>An information box with an OK button (WinForms MessageBoxIcon.Information).</summary>
    Task ShowInfoAsync(Window owner, string message, string caption);

    /// <summary>Opens a local file (an extracted HTML document) in the system browser through <paramref name="owner"/>'s launcher (the
    /// main window's when null). Returns at once; a failed launch is only traced.</summary>
    void OpenInBrowser(TopLevel? owner, string path);

    /// <summary>The tick-box side chooser (a guest choosing its side, or the host assigning sides to a seat); the ticked side ids in pack order,
    /// or null when cancelled. OK with nothing ticked is refused inside the dialog.</summary>
    Task<IReadOnlyList<string>?> ChooseSidesAsync(Window owner, string title, string prompt, IReadOnlyList<SideDef> sides,
        IReadOnlyCollection<string>? ticked = null, string? note = null);

    /// <summary>The Join dialog (WinForms JoinDialog) for a lobby: the player name and the password (when the lobby is locked); null when cancelled.</summary>
    Task<JoinChoice?> AskJoinAsync(Window owner, LobbyRow row, string playerName);

    /// <summary>The Host a saved game dialog (WinForms HostSavedDialog); null when cancelled.</summary>
    Task<HostSavedChoice?> AskHostSavedAsync(Window owner, RulesPack pack, string playerName, string lobbyName, IReadOnlyCollection<string> hostSides);

    /// <summary>The host's Players dialog (WinForms SeatsDialog); returns when it is closed.</summary>
    Task ShowSeatsAsync(Window owner, HostSession host, RulesPack? pack);
}

/// <summary>The real dialogs: the OS file pickers (Avalonia storage provider), small classic message boxes and the setup chooser.</summary>
public sealed class Dialogs : IDialogs
{
    /// <summary>Shown when a picker returns a location without a local path (e.g. a cloud or portal item).</summary>
    public const string NotLocalText = "This location can't be opened directly; choose a file on this computer.";

    public async Task<string?> OpenFileAsync(Window owner, string title, IReadOnlyList<FileFilter> filters, string? initialFolder = null)
    {
        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(initialFolder))
        {
            try { start = await owner.StorageProvider.TryGetFolderFromPathAsync(initialFolder); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.WriteLine("start folder unavailable: " + ex.Message); }
        }
        var files = await ModalTracker.Track(() => owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters.Select(Type).ToList(),
            SuggestedStartLocation = start,
        }));
        if (files.Count == 0) return null;
        if (files[0].TryGetLocalPath() is { } path) return path;
        await MessageBox.ShowAsync(owner, NotLocalText, "Quartermaster", yesNo: false);
        return null;
    }

    /// <summary>Items without a local path are dropped; when nothing local is left of a non-empty choice, says so (<see cref="NotLocalText"/>).</summary>
    public async Task<IReadOnlyList<string>> OpenFilesAsync(Window owner, string title, IReadOnlyList<FileFilter> filters)
    {
        var files = await ModalTracker.Track(() => owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = filters.Select(Type).ToList(),
        }));
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (files.Count > 0 && paths.Count == 0) await MessageBox.ShowAsync(owner, NotLocalText, "Quartermaster", yesNo: false);
        return paths;
    }

    public async Task<string?> SaveFileAsync(Window owner, string title, FileFilter filter, string defaultExtension, string? suggestedFileName = null)
    {
        var file = await ModalTracker.Track(() => owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            DefaultExtension = defaultExtension,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = new[] { Type(filter) },
            ShowOverwritePrompt = true,
        }));
        if (file == null) return null;
        if (file.TryGetLocalPath() is not { } path)
        {
            await MessageBox.ShowAsync(owner, NotLocalText, "Quartermaster", yesNo: false);
            return null;
        }
        return Path.HasExtension(path) ? path : path + "." + defaultExtension;
    }

    static FilePickerFileType Type(FileFilter f) => new(f.Name) { Patterns = f.Patterns.ToList() };

    public Task<bool> AskYesNoAsync(Window owner, string message, string caption, bool defaultNo = true) =>
        MessageBox.ShowAsync(owner, message, caption, yesNo: true, defaultNo);

    public Task ShowInfoAsync(Window owner, string message, string caption) =>
        MessageBox.ShowAsync(owner, message, caption, yesNo: false);

    public Task<IReadOnlyList<string>?> AskColumnsAsync(Window owner, IReadOnlyList<string> keys) =>
        new Workbench.ColumnsDialog(keys).ShowDialogTracked<IReadOnlyList<string>?>(owner);

    public Task<Quartermaster.Rules.Tables.PastedGrid?> PasteTableAsync(Window owner, string text, string? title = null, string? targetDescription = null, bool tsvOnly = false) =>
        new Workbench.PasteTableDialog(text, title, targetDescription, tsvOnly).ShowDialogTracked<Quartermaster.Rules.Tables.PastedGrid?>(owner);

    public Task<(int Count, string Label)?> AskBlockAsync(Window owner) =>
        new Workbench.BlockDialog().ShowDialogTracked<(int Count, string Label)?>(owner);

    public Task<bool?> AskImportTargetAsync(Window owner, string selectedTable) =>
        new Workbench.ImportTargetDialog(selectedTable).ShowDialogTracked<bool?>(owner);

    public async Task<string?> GetClipboardTextAsync(Window owner)
    {
        try
        {
            var cb = TopLevel.GetTopLevel(owner)?.Clipboard;
            return cb == null ? null : await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(cb);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("clipboard busy: " + ex.Message);
            return null;
        }
    }

    public async Task SetClipboardTextAsync(Window owner, string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(owner)?.Clipboard is not { } cb) return;
            var t = new Avalonia.Input.DataTransfer();
            t.Add(Avalonia.Input.DataTransferItem.CreateText(text));
            await cb.SetDataAsync(t);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("clipboard busy: " + ex.Message);
        }
    }

    public Task ShowWarningAsync(Window owner, string message, string caption) =>
        MessageBox.ShowAsync(owner, message, caption, yesNo: false);

    public Task<bool?> AskYesNoCancelAsync(Window owner, string message, string caption) =>
        MessageBox.ShowYesNoCancelAsync(owner, message, caption);

    public Task<string?> AskShortcutAsync(Window owner, string commandLabel) =>
        new Preferences.ShortcutCaptureDialog(commandLabel).ShowDialogTracked<string?>(owner);

    public Task<string?> AskColourAsync(Window owner, string initialHex) =>
        new Preferences.ColourDialog(initialHex).ShowDialogTracked<string?>(owner);

    public Task<string?> AskTextAsync(Window owner, string title, string prompt, string initial) =>
        new InputDialog(title, prompt, initial).ShowDialogTracked<string?>(owner);

    public Task<IReadOnlyList<string>?> EditPropertySheetAsync(Window owner, string title, IReadOnlyList<(string Name, string Value)> rows,
        Func<string, string?>? managedValue) =>
        new PropertySheetDialog(title, rows, managedValue).ShowDialogTracked<IReadOnlyList<string>?>(owner);

    public Task<SetupChoice?> ChooseSetupAsync(Window owner, ModuleModel model, bool preselectNew, Difficulty difficulty, RulesPack? pack) =>
        new SetupChooser(model, difficulty, preselectNew, pack, this).ShowDialogTracked<SetupChoice?>(owner);

    public Task<DiceChoice?> AskDiceAsync(Window owner, string title, bool multi) =>
        new DiceDialog(title, multi).ShowDialogTracked<DiceChoice?>(owner);

    public Task<TurnChoice?> AskTurnAsync(Window owner, TurnState current) =>
        new TurnDialog(current).ShowDialogTracked<TurnChoice?>(owner);

    public async Task<bool> ConfirmOpenDecisionsAsync(Window owner, IReadOnlyList<Decision> open, Action<Decision>? jumpTo = null)
    {
        var dlg = new NextPhaseDialog(open);
        if (await dlg.ShowDialogTracked<bool?>(owner) == true) return true;
        if (dlg.JumpTo is { } d) jumpTo?.Invoke(d);
        return false;
    }

    public async Task<MissingInputResult> AskMissingAsync(Window owner, ModuleSession s, IActionPort port, PendingRun run) =>
        await new MissingInputDialog(s, port, run).ShowDialogTracked<MissingInputResult?>(owner) ?? MissingInputResult.Cancel;

    /// <summary>The summary under the list is the owner's history preview (WinForms HistoryController.Describe(h.Preview(r))).</summary>
    public Task<CheckpointRow?> PickCheckpointAsync(Window owner, IReadOnlyList<CheckpointRow> rows, bool confirmOnly)
    {
        if (rows.Count == 0) return Task.FromResult<CheckpointRow?>(null);
        var h = (owner as MainWindow)?.History;
        Func<CheckpointRow, string> summary = h != null ? r => HistoryController.Describe(h.Preview(r)) : _ => "";
        return new StepBackDialog(rows, summary, confirmOnly).ShowDialogTracked<CheckpointRow?>(owner);
    }

    public Task<ExportChoice?> AskExportChoiceAsync(Window owner, string mapName) =>
        new ExportChoiceDialog(mapName).ShowDialogTracked<ExportChoice?>(owner);

    public Task<IReadOnlyList<UserLedgerTab>?> EditLedgerTabsAsync(Window owner, IReadOnlyList<UserLedgerTab> tabs, RulesPack? pack, int select = 0, bool addNew = false) =>
        new Ledgers.EditTabsDialog(tabs, pack, select, addNew).ShowDialogTracked<IReadOnlyList<UserLedgerTab>?>(owner);

    public Task<IReadOnlyList<string>?> ChooseSidesAsync(Window owner, string title, string prompt, IReadOnlyList<SideDef> sides,
        IReadOnlyCollection<string>? ticked = null, string? note = null) =>
        new SideChoiceDialog(title, prompt, sides, ticked, note).ShowDialogTracked<IReadOnlyList<string>?>(owner);

    public Task<JoinChoice?> AskJoinAsync(Window owner, LobbyRow row, string playerName) =>
        new JoinDialog(row, playerName).ShowDialogTracked<JoinChoice?>(owner);

    public Task<HostSavedChoice?> AskHostSavedAsync(Window owner, RulesPack pack, string playerName, string lobbyName, IReadOnlyCollection<string> hostSides) =>
        new HostSavedDialog(pack, playerName, lobbyName, hostSides).ShowDialogTracked<HostSavedChoice?>(owner);

    public Task ShowSeatsAsync(Window owner, HostSession host, RulesPack? pack) =>
        new SeatsDialog(host, pack).ShowDialogTracked(owner);

    public void OpenInBrowser(TopLevel? owner, string path)
    {
        var top = owner ?? (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (top == null) return;
        _ = LaunchAsync(top, path);
    }

    /// <summary>Never faults: nobody awaits it.</summary>
    static async Task LaunchAsync(TopLevel top, string path)
    {
        try
        {
            if (!await top.Launcher.LaunchFileInfoAsync(new FileInfo(path))) System.Diagnostics.Trace.WriteLine("browser did not open " + path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("browser launch failed: " + ex.Message);
        }
    }
}

/// <summary>A small classic message box: the text, then OK (or Yes / No, Yes focused unless defaultNo, as WinForms Button1 / Button2).</summary>
sealed class MessageBox : Window
{
    MessageBox(string message, string caption, bool yesNo, bool cancel = false, bool defaultNo = true)
    {
        Title = caption;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 260;
        MaxWidth = 520;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 6, Margin = new Thickness(10) };
        Button Add(string text, bool? result, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = text, MinWidth = 75, IsDefault = isDefault, IsCancel = isCancel, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) => Close(result);
            buttons.Children.Add(b);
            return b;
        }
        Button focus;
        if (cancel)
        {
            focus = Add("Yes", true, isDefault: true);
            Add("No", false);
            Add("Cancel", null, isCancel: true);
        }
        else if (yesNo)
        {
            var yes = Add("Yes", true, isDefault: !defaultNo);
            var no = Add("No", false, isDefault: defaultNo, isCancel: true);
            focus = defaultNo ? no : yes;
        }
        else focus = Add("OK", true, isDefault: true, isCancel: true);

        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 16, 16, 6) });
        Content = panel;
        Opened += (_, _) => focus.Focus();
    }

    public static async Task<bool> ShowAsync(Window owner, string message, string caption, bool yesNo, bool defaultNo = true) =>
        await new MessageBox(message, caption, yesNo, defaultNo: defaultNo).ShowDialogTracked<bool?>(owner) == true;

    /// <summary>Yes / No / Cancel; closing the window counts as Cancel.</summary>
    public static Task<bool?> ShowYesNoCancelAsync(Window owner, string message, string caption) =>
        new MessageBox(message, caption, yesNo: true, cancel: true).ShowDialogTracked<bool?>(owner);
}
