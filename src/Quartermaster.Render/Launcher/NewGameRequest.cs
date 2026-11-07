using Quartermaster.Core;
using Quartermaster.Vassal;

namespace Quartermaster.Render;

public enum GameMode { Offline, Online }

/// <summary>What the shared New game setup produced. Setup null = blank game.</summary>
public sealed record NewGameRequest(GameMode Mode, SetupDef? Setup, Difficulty Difficulty, IReadOnlyList<PlayerDef>? Players, string? LobbyName, string? Password);
