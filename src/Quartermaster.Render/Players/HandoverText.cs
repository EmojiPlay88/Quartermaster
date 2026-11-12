using Quartermaster.Core;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>Texts of the hot-seat handover screen and player menu (shared by WinForms and Avalonia).</summary>
public static class HandoverText
{
    public const string NoPlayers = "(no players)";
    public const string PlayerTip = "Who is at the screen. Choose a player to hand over.";

    public static string MessageFor(PlayerDef p, string sidesText) => $"Pass to {p.Name} ({sidesText})";

    /// <summary>Side names of the player in player order (ids when the pack has no such side).</summary>
    public static string SidesText(PlayerDef p, RulesPack? pack)
    {
        var names = (p.Sides ?? Array.Empty<string>()).Select(id =>
            pack?.Sides.FirstOrDefault(s => s != null && s.Id == id) is { } sd && !string.IsNullOrWhiteSpace(sd.Name) ? sd.Name : id);
        return string.Join(", ", names);
    }

    public static string StatusText(PlayerDef? current) => $"Player: {current?.Name ?? "—"}";

    public static string MenuText(PlayerDef p, RulesPack? pack) => $"{p.Name} ({SidesText(p, pack)})";
}
