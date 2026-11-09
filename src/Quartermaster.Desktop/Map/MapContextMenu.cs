using Avalonia.Controls;
using Avalonia.Input;
using Quartermaster.Core;
using Quartermaster.Desktop.Shell;
using Quartermaster.Render;

namespace Quartermaster.Desktop.Map;

/// <summary>What a map's right-click menu needs from the window around it (WinForms MapWindow.PieceHooks / Host).</summary>
public sealed class MapMenuServices
{
    /// <summary>The prompt, delete question and property sheet dialogs.</summary>
    public required IDialogs Dialogs { get; init; }

    /// <summary>A command's shortcut, shown next to entries that name one (CommandRouter.Gesture); null shows none.</summary>
    public Func<string, KeyGesture?>? Gesture { get; init; }

    /// <summary>Game master mode (WinForms MainForm.GameMaster): adds "Delete counter". Null = off.</summary>
    public Func<bool>? GameMaster { get; init; }

    /// <summary>Settings "ask before deleting" (WinForms Settings.ConfirmDelete, on by default). Null = on.</summary>
    public Func<bool>? ConfirmDelete { get; init; }

    /// <summary>Opens the formula window for (formula id, piece id): the counter menu's Calculate entries. Null = they open nothing.</summary>
    public Action<string, string>? ShowFormula { get; init; }
}

/// <summary>
/// The map's right-click menu, as WinForms MapWindow.OnContext: on a counter, the shared <see cref="PieceMenuModel"/> entries (clicked counter
/// first, then the selection) turned into Avalonia items with the same labels, separators, submenus, disabled reasons as tooltips and
/// shortcut texts; on the empty map, MapWindow.BuildMapMenu. Commands run inside the view's error guard.
/// </summary>
public static class MapContextMenu
{
    /// <summary>Caption of the delete question (WinForms MessageBox caption).</summary>
    public const string Caption = "Quartermaster";

    /// <summary>The menu for a right-click at a counter (or the empty map for null); null when there is no game or nothing to show.</summary>
    public static ContextMenu? Build(MapView view, string? pieceId)
    {
        var s = view.Session;
        if (s.Game is not { } game) return null;
        var menu = new ContextMenu();
        if (pieceId != null && game.State.Pieces.ContainsKey(pieceId))
        {
            // The clicked counter first, then the selection (which repeats it when it is selected).
            var ids = new List<string> { pieceId };
            ids.AddRange(view.SelectedPieceIds);
            AddEntries(menu.Items, PieceMenuModel.Build(s, view.Map.Id, ids, view.Actions?.Invoke(), Hooks(view)), view);
        }
        else AddMapItems(menu.Items, view, game);
        return menu.Items.Count > 0 ? menu : null;
    }

    /// <summary>The dialogs the shared counter menu opens from this view. No Ledger in the Avalonia app yet: "Show in Ledger" is left out (as
    /// a WinForms map window without a main window). Calculate entries open the formula window through the services.</summary>
    static PieceMenuHooks Hooks(MapView view)
    {
        var services = view.MenuServices;
        return new PieceMenuHooks
        {
            GameMaster = services?.GameMaster?.Invoke() == true,
            AskInputAsync = (title, prompt) => services != null && Owner(view) is { } owner
                ? services.Dialogs.AskTextAsync(owner, title, prompt, "")
                : Task.FromResult<string?>(null),
            // Settings "ask before deleting" off: always yes. Without dialogs nothing is deleted.
            ConfirmDeleteAsync = question => services?.ConfirmDelete?.Invoke() == false ? Task.FromResult(true)
                : services != null && Owner(view) is { } owner ? services.Dialogs.AskYesNoAsync(owner, question, Caption)
                : Task.FromResult(false),
            EditSheet = (action, pieceId) => view.RunMenuCommand(() => EditSheetAsync(view, action, pieceId)),
            ShowFormula = services?.ShowFormula,
        };
    }

    static Window? Owner(MapView view) => TopLevel.GetTopLevel(view) as Window;

    /// <summary>Shared entries as Avalonia items: '&amp;' mnemonics become '_', separators, submenus, the disabled reason as a tooltip that
    /// also shows on a disabled item, the shortcut text (display only), and a click runs the entry in the view's guard.</summary>
    static void AddEntries(ItemCollection items, IReadOnlyList<MenuEntry> entries, MapView view)
    {
        foreach (var e in entries)
        {
            if (e.IsSeparator) { items.Add(new Separator()); continue; }
            var item = new MenuItem { Header = MenuText.FromWinForms(e.Label), IsEnabled = e.Enabled };
            if (e.DisabledReason != null)
            {
                ToolTip.SetTip(item, e.DisabledReason);
                ToolTip.SetShowOnDisabled(item, true);
            }
            if (e.CommandId != null && view.MenuServices?.Gesture?.Invoke(e.CommandId) is { } g) item.InputGesture = g;
            if (e.Children.Count > 0) AddEntries(item.Items, e.Children, view);
            if (e.Run is { } run)
            {
                var entry = e;
                item.Click += (_, _) => view.RunMenuCommand(entry.RunAsync ?? (() => { run(); return Task.CompletedTask; }));
            }
            items.Add(item);
        }
    }

    /// <summary>WinForms MapWindow.BuildMapMenu. "Save map as picture…" comes with stage 3.</summary>
    static void AddMapItems(ItemCollection items, MapView view, GameSession game)
    {
        MenuItem Item(string label, Action click)
        {
            var item = new MenuItem { Header = MenuText.FromWinForms(label) };
            item.Click += (_, _) => click();
            items.Add(item);
            return item;
        }

        var unmoved = PieceActions.ForMap(view.Map)[0];
        Item("Mark All Unmoved", () => view.RunMenuCommand(() =>
        {
            PieceMenuModel.Execute(view.Session, view.Actions?.Invoke(), unmoved.Build(game.State));
            return Task.CompletedTask;
        }));
        items.Add(new Separator());
        var grid = Item("Show Grid", () => view.ShowGrid = !view.ShowGrid);   // this map only, as WinForms (View > Show Grid sets every map)
        grid.ToggleType = MenuItemToggleType.CheckBox;
        grid.IsChecked = view.ShowGrid;
        Item("Zoom to Fit", view.ZoomToFit);
        Item("Zoom 100%", () => view.ZoomCentered(1f));
        items.Add(new Separator());
        var hide = Item("Hide counters", () => view.HideCounters = !view.HideCounters);   // before Centre on selected, as WinForms
        hide.ToggleType = MenuItemToggleType.CheckBox;
        hide.IsChecked = view.HideCounters;
        Item("Centre on selected", view.CentreOnSelection);
    }

    /// <summary>WinForms MapWindow.EditSheet: the property sheet dialog, then one SetTraitState through the action port.</summary>
    static async Task EditSheetAsync(MapView view, PieceAction action, string pieceId)
    {
        var s = view.Session;
        if (view.MenuServices is not { } services || Owner(view) is not { } owner) return;
        if (s.Game is not { } game || !game.State.Pieces.TryGetValue(pieceId, out var piece)) return;
        var index = PieceMenuModel.SheetIndex(s, piece, action.Label);
        if (index < 0) return;
        var rows = PieceActions.ReadSheet(piece, index);
        var values = await services.Dialogs.EditPropertySheetAsync(owner, action.Label, rows, name => PieceMenuModel.ManagedSheetValue(s, piece, name));
        // The dialog is asynchronous: a game replaced or a counter removed meanwhile drops the edit.
        if (values == null || !ReferenceEquals(s.Game, game) || !game.State.Pieces.TryGetValue(pieceId, out piece)) return;
        var write = PieceActions.WriteSheet(piece, index, values);
        if (view.Actions?.Invoke() is { } port) port.PieceCommand(write);
        else game.Execute(write);
    }
}
