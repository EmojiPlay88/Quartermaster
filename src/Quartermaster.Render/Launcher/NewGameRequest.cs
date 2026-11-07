using Quartermaster.Core;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

public enum GameMode { Offline, Online }

/// <summary>What the New game dialog collects. <see cref="Setup"/> null = blank game; <see cref="Players"/> null/empty = solo.</summary>
/// <remarks>The password never appears in <see cref="ToString"/>; once handed to the online launch it is cleared here (<see cref="WithoutPassword"/>).</remarks>
public sealed record NewGameRequest(GameMode Mode, SetupDef? Setup, Difficulty Difficulty, IReadOnlyList<PlayerDef>? Players, string? LobbyName, string? Password)
{
    /// <summary>The same request with no password (what goes onward once the online launch carries it).</summary>
    public NewGameRequest WithoutPassword() => this with { Password = null };

    private bool PrintMembers(System.Text.StringBuilder b)
    {
        b.Append($"Mode = {Mode}, Setup = {Setup?.Name}, Difficulty = {Difficulty}, Players = {Players?.Count ?? 0}, LobbyName = {LobbyName}, ");
        b.Append(Password == null ? "Password = (none)" : "Password = (hidden)");
        return true;
    }
}

public static class NewGameRules
{
    public const int MaxLobbyName = 40;

    /// <summary>Null when the request is fine, otherwise the problem in words.</summary>
    public static string? Validate(NewGameRequest r, RulesPack? pack)
    {
        if (r.Mode == GameMode.Offline)
        {
            if (r.LobbyName != null || r.Password != null) return "An offline game has no lobby name or password.";
        }
        else
        {
            var name = r.LobbyName?.Trim() ?? "";
            if (name.Length == 0) return "Give the lobby a name.";
            if (name.Length > MaxLobbyName) return $"The lobby name can be at most {MaxLobbyName} characters.";
        }
        if (r.Players is { Count: >= 2 } players && pack != null) return Players.Validate(players, pack);
        return null;
    }
}
