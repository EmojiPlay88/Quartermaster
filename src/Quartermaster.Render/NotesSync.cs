namespace Quartermaster.Render;

/// <summary>Decisions of the notes window: when the box text is written to the game and when it is refreshed from it.</summary>
public static class NotesSync
{
    /// <summary>True when the box text differs from the game's notes (null counts as empty, so an untouched box never commits).</summary>
    public static bool ShouldCommit(string boxText, string? stateNotes) => (boxText ?? "") != (stateNotes ?? "");

    /// <summary>The box follows the game only when the user is not editing it.</summary>
    public static bool ShouldReload(bool boxFocused, bool commitPending) => !boxFocused && !commitPending;
}
