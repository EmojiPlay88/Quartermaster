namespace Quartermaster.Render;

/// <summary>One stop of the interface tour. <paramref name="TargetId"/> is a control name the app resolves to an on-screen rectangle.</summary>
public sealed record TourStep(string Id, string TargetId, string Title, string Text);

/// <summary>The interface tour script (UI-free). Text describes the engine only, never any game's rules.</summary>
public static class TourSteps
{
    public static IReadOnlyList<TourStep> All { get; } = new TourStep[]
    {
        new("moduleTree", "ModuleTree", "Module tree",
            "This tree lists everything in the opened module: maps, pieces, decks, charts and documents. Double-click an entry to open it."),
        new("map", "MapWindow", "Map",
            "The main map is where you play. Drag counters to move them, and right-click a counter for its menu."),
        new("phaseStrip", "PhaseStrip", "Phase strip and Next Phase",
            "The strip shows the current phase of your rules pack. Use Next Phase to move on; the engine runs the steps your pack defines."),
        new("phasePanel", "PhasePanel", "Phase panel",
            "This panel explains what a phase asks of you and lists pending steps. Difficulty is set in Settings and decides how much the engine does for you."),
        new("alertBar", "AlertBar", "Alert bar",
            "Alerts from your rules pack appear here as coloured chips: red is critical, amber a warning, blue information. Click a chip to jump to the piece."),
        new("outliner", "OutlinerPanel", "Outliner",
            "The outliner lists the pieces on the map with their ledger values. Choose how it groups them, and click a row to find the piece."),
        new("ledger", "LedgerMenu", "Ledger",
            "The ledger records every tracked value and why it changed. Open it from the View menu; the formula dialog's colours and options are in Settings."),
        new("undo", "EditMenu", "Undo and Step Back",
            "Edit has Undo and Redo for single actions. Step Back returns to the start of a phase, and every change stays in the game log."),
    };

    /// <summary>The steps whose target is currently on screen, in order.</summary>
    public static IReadOnlyList<TourStep> Visible(Func<string, bool> isVisible)
        => All.Where(s => isVisible(s.TargetId)).ToList();
}
