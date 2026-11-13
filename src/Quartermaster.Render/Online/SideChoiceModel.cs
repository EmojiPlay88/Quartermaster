using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>The tick-box side chooser (a guest picking its side, or the host assigning sides to a seat), shared by every UI.</summary>
public static class SideChoiceModel
{
    public const string TickOneText = "Tick at least one side.";
    public const string ChooseTitle = "Choose your side";
    public const string ChoosePrompt = "Play these sides:";
    public const string NoFreeSideText = "No side is free in this game any more. You can chat; the host can give you a side.";

    /// <summary>The side ids ticked when the chooser opens: the only side when there is exactly one and nothing was given, else what was given.</summary>
    public static IReadOnlyList<string> InitialTicks(IReadOnlyList<SideDef> sides, IReadOnlyCollection<string>? ticked)
    {
        if (ticked == null) return sides.Count == 1 ? new[] { sides[0].Id } : Array.Empty<string>();
        return ticked.ToList();
    }

    public static string Label(SideDef s) => string.IsNullOrEmpty(s.Name) ? s.Id : s.Name;
}
