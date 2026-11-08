using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

/// <summary>
/// One entry of a context menu, toolkit-neutral. <see cref="Label"/> keeps the WinForms mnemonic ampersand ("Show in &amp;Ledger");
/// <see cref="CommandId"/> names the command whose shortcut the entry displays (not a binding: the entry runs <see cref="Run"/>);
/// <see cref="DisabledReason"/> is the tooltip (shown whenever set, as WinForms does); <see cref="Run"/> is null for separators, submenu
/// parents and disabled placeholders. UIs wrap <see cref="Run"/> in their usual error guard.
/// </summary>
public sealed record MenuEntry(string Label, string? CommandId, bool Enabled, string? DisabledReason, IReadOnlyList<MenuEntry> Children, Action? Run, bool IsSeparator)
{
    public static MenuEntry Separator { get; } = new("-", null, false, null, Array.Empty<MenuEntry>(), null, true);

    public static MenuEntry Item(string label, Action? run, bool enabled = true, string? reason = null, string? commandId = null) =>
        new(label, commandId, enabled, reason, Array.Empty<MenuEntry>(), run, false);

    public static MenuEntry Submenu(string label, IReadOnlyList<MenuEntry> children) => new(label, null, true, null, children, null, false);
}

/// <summary>
/// What the counter menu needs from the UI: dialogs and windows, and the game-master switch. Every hook is optional; a missing dialog hook
/// means "nothing to show" (formula, sheet) or "yes" (delete question), and a null <see cref="ShowInLedger"/> leaves that entry out (as a
/// WinForms map window without a main window did).
/// </summary>
public sealed class PieceMenuHooks
{
    /// <summary>Game master mode: adds "Delete counter".</summary>
    public bool GameMaster { get; init; }

    /// <summary>Opens the Ledger at these counters' rows. Null leaves "Show in Ledger" out.</summary>
    public Action<IReadOnlyList<string>>? ShowInLedger { get; init; }

    /// <summary>Opens the formula dialog: (formula id, piece id).</summary>
    public Action<string, string>? ShowFormula { get; init; }

    /// <summary>Asks for a prompt action's text: (title, prompt) → text, or null when cancelled.</summary>
    public Func<string, string, string?>? AskInput { get; init; }

    /// <summary>Yes/No for a deletion, given the question ("Delete Red 1?"); only asked when something would be deleted.</summary>
    public Func<string, bool>? ConfirmDelete { get; init; }

    /// <summary>Opens the property sheet editor: (the "sheet" action, piece id). See <see cref="PieceMenuModel.SheetIndex"/> and
    /// <see cref="PieceMenuModel.ManagedSheetValue"/>.</summary>
    public Action<PieceAction, string>? EditSheet { get; init; }

    /// <summary>New piece ids for actions that create pieces (clone, place marker).</summary>
    public Func<string> NewId { get; init; } = () => "c" + Guid.NewGuid().ToString("N");
}

/// <summary>The right-click menu of a counter on a map, exactly as the WinForms map window built it.</summary>
public static class PieceMenuModel
{
    public const string NoCommands = "(no commands)";
    public const string ShowInLedgerLabel = "Show in &Ledger";
    public const string ShowInLedgerCommand = "view.ledger";
    public const string LedgerNeedsRulesText = "The Ledger needs a rules pack (File > Open Rules Pack… or New Rules Pack…).";

    /// <summary>
    /// The menu for the counter <paramref name="pieceIds"/>[0] on map <paramref name="mapId"/>. The remaining ids are the map's current
    /// selection, which includes the clicked counter only when it is selected (so pass the clicked id twice for a selected counter).
    /// Empty when there is no game, no id, or the counter is not in the game (the caller shows the map menu then). A counter the hot-seat
    /// viewer sees masked (or not at all) gets only a disabled "(no commands)". Commands go through <paramref name="actions"/> (the action
    /// port, read once here); without one they run as plain logged commands, as a map window without a main window did.
    /// </summary>
    public static IReadOnlyList<MenuEntry> Build(ModuleSession s, string mapId, IReadOnlyList<string> pieceIds, IActionPort? actions,
        PieceMenuHooks? hooks = null)
    {
        hooks ??= new PieceMenuHooks();
        var menu = new List<MenuEntry>();
        if (s.Game is not { } game || pieceIds == null || pieceIds.Count == 0) return menu;
        if (!game.State.Pieces.TryGetValue(pieceIds[0], out var piece)) return menu;
        var selected = new HashSet<string>(pieceIds.Skip(1));

        // Hot-seat: a piece drawn as its mask (or not shown at all) must not reveal its real commands.
        if (s.ViewSides != null && (s.View?.Pieces.TryGetValue(piece.Id, out var seen) != true || seen!.TypeString != piece.TypeString))
        {
            menu.Add(MenuEntry.Item(NoCommands, null, enabled: false));
            return menu;
        }

        var pieceActions = PieceActions.For(piece, s.Model, game.State, hooks.NewId);
        foreach (var group in ActionMenu.Group(pieceActions.Where(a => a.Group != "error")))
        {
            if (menu.Count > 0) menu.Add(MenuEntry.Separator);
            foreach (var a in group)
            {
                var action = a;
                menu.Add(MenuEntry.Item(a.Label, () => RunAction(s, actions, hooks, action, piece, selected), a.Enabled, a.DisabledReason));
            }
        }
        AddCalculate(menu, s, game, piece, hooks);
        if (hooks.GameMaster)
        {
            if (menu.Count > 0) menu.Add(MenuEntry.Separator);
            menu.Add(MenuEntry.Item("Delete counter", () => DeleteCounters(s, actions, hooks, piece, game, selected)));
        }
        if (hooks.ShowInLedger is { } show)
        {
            if (menu.Count > 0) menu.Add(MenuEntry.Separator);
            var available = s.Rules != null;
            menu.Add(MenuEntry.Item(ShowInLedgerLabel, () => show(TargetIds(piece.Id, game.State, selected)), available,
                available ? null : LedgerNeedsRulesText, ShowInLedgerCommand));
        }
        if (menu.Count == 0) menu.Add(MenuEntry.Item(NoCommands, null, enabled: false));
        return menu;
    }

    /// <summary>"Calculate" submenu: one entry per formula that applies to the piece. Left out without a rules pack or when the viewer may not act.</summary>
    private static void AddCalculate(List<MenuEntry> menu, ModuleSession s, GameSession game, PieceInstance piece, PieceMenuHooks hooks)
    {
        if (s.Rules is not { } rules || s.Runner == null || !s.CanAct(piece.Id)) return;
        IReadOnlyList<Quartermaster.Rules.FormulaDef> formulas;
        try { formulas = PieceFilter.FormulasFor(rules, piece, game.State, s.Model); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            s.Log.Add(DiagnosticSeverity.Warning, "rules", "Could not list formulas for a piece: " + ex.Message);
            return;
        }
        if (formulas.Count == 0) return;
        if (menu.Count > 0) menu.Add(MenuEntry.Separator);
        var children = formulas.Select(f =>
        {
            var id = f.Id;
            return MenuEntry.Item(f.Name, () => hooks.ShowFormula?.Invoke(id, piece.Id));
        }).ToList();
        menu.Add(MenuEntry.Submenu("Calculate", children));
    }

    /// <summary>The counters a command acts on: the selection (in game order) when the clicked counter is part of it, otherwise just the clicked counter.</summary>
    public static List<string> TargetIds(string clickedId, GameState st, IReadOnlySet<string> selected) => selected.Contains(clickedId)
        ? st.Pieces.Keys.Where(selected.Contains).ToList()
        : new List<string> { clickedId };

    /// <summary>The delete question: "Delete {name}?" for one counter, "Delete {n} pieces?" for several.</summary>
    public static string DeleteQuestion(GameState st, IReadOnlyList<string> ids, string clickedId)
    {
        var what = ids.Count == 1
            ? (st.Pieces.TryGetValue(ids[0], out var p) ? PieceInfo.Name(p) : clickedId) is { Length: > 0 } n ? n : "this piece"
            : ids.Count + " pieces";
        return $"Delete {what}?";
    }

    private static bool Confirm(PieceMenuHooks hooks, GameState st, List<string> ids, string clickedId) =>
        ids.Count == 0 || hooks.ConfirmDelete == null || hooks.ConfirmDelete(DeleteQuestion(st, ids, clickedId));

    /// <summary>
    /// Runs commands as one undoable action: through the action port when there is one (consecutive moves are one move gesture), else as
    /// plain logged commands with the label of the first.
    /// </summary>
    public static void Execute(ModuleSession s, IActionPort? actions, IEnumerable<GameCommand> commands)
    {
        if (s.Game is not { } game) return;
        if (actions != null) { actions.PieceCommands(commands.ToList()); return; }
        using var scope = game.BeginAction();
        foreach (var c in commands) game.Execute(CommandLog.Prepare(c, game.State, s));
    }

    private static void RunAction(ModuleSession s, IActionPort? actions, PieceMenuHooks hooks, PieceAction action, PieceInstance clicked,
        IReadOnlySet<string> selected)
    {
        if (s.Game is not { } game) return;
        if (action.Group == "sheet") { hooks.EditSheet?.Invoke(action, clicked.Id); return; }

        // The action applies to every selected piece that offers the same command (the clicked piece always does).
        var targets = game.State.Pieces.Values.Where(p => p.Id == clicked.Id || selected.Contains(p.Id)).ToList();
        string? input = null;
        if (action.BuildWithInput != null)
        {
            input = hooks.AskInput?.Invoke(action.Label, action.PromptText is { Length: > 0 } t ? t : action.Label);
            if (input == null) return;
        }
        var batches = new List<GameCommand[]>();
        var deleting = new List<string>();
        foreach (var p in targets)
        {
            var mine = p.Id == clicked.Id
                ? action
                : PieceActions.For(p, s.Model, game.State, hooks.NewId).FirstOrDefault(a => a.Label == action.Label && a.Group == action.Group && a.Enabled);
            if (mine == null) continue;
            if (mine.IsDelete) deleting.Add(p.Id);
            batches.Add((input != null && mine.BuildWithInput != null ? mine.BuildWithInput(game.State, input) : mine.Build(game.State)).ToArray());
        }
        if (!Confirm(hooks, game.State, deleting, clicked.Id)) return;
        foreach (var b in batches) Execute(s, actions, b);
    }

    /// <summary>Game master: removes the clicked counter, or the whole selection when the clicked counter is selected (after the delete question).</summary>
    private static void DeleteCounters(ModuleSession s, IActionPort? actions, PieceMenuHooks hooks, PieceInstance clicked, GameSession game,
        IReadOnlySet<string> selected)
    {
        var ids = TargetIds(clicked.Id, game.State, selected);
        if (!Confirm(hooks, game.State, ids, clicked.Id)) return;
        Execute(s, actions, ids.Select(id => new RemovePiece(id)));
    }

    /// <summary>Trait index of the piece's property sheet whose menu name is <paramref name="label"/>; -1 when there is none.</summary>
    public static int SheetIndex(ModuleSession s, PieceInstance piece, string label)
    {
        var traits = PieceTypeCache.For(s.Images).Get(piece.TypeString).Traits;
        for (var i = 0; i < traits.Count; i++)
            if (traits[i] is PropertySheetDef d && d.MenuName == label) return i;
        return -1;
    }

    /// <summary>Ledger value text for a sheet row that a rules-pack property manages ("hidden" when the viewer may not see it); null when no
    /// pack is loaded or no property is bound to the row.</summary>
    public static string? ManagedSheetValue(ModuleSession s, PieceInstance piece, string rowName)
    {
        if (s.Rules is not { } rules || s.Game is not { } game) return null;
        var prop = rules.Properties.FirstOrDefault(p => p.Scope != Quartermaster.Rules.PropertyScope.Holders
            && string.Equals(p.SheetField, rowName, StringComparison.OrdinalIgnoreCase)
            && PieceFilter.Matches(p.AppliesTo, piece, game.State, s.Model, rules));
        if (prop == null) return null;
        if (s.View is not { } view || (s.ViewSides != null && Ledger.Get(view, LedgerKey.Piece(piece.Id), prop.Id) == null
            && Ledger.Get(game.State, LedgerKey.Piece(piece.Id), prop.Id) != null)) return ModuleSession.HiddenText;
        return Ledger.GetOrDefault(view, LedgerKey.Piece(piece.Id), prop).ToString("0.##", CultureInfo.InvariantCulture);
    }
}
